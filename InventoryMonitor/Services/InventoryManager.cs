using InventoryMonitor.Models;
using Terraria;
using Terraria.Localization;
using TShockAPI;

namespace InventoryMonitor.Services;

public readonly record struct RemoveOneResult(bool Removed, SlotEntry? Item, string? Error);
public readonly record struct RemoveByTypeResult(int NetId, int SlotsAffected, int CountRemoved, string? Error);
public readonly record struct ClearResult(string Scope, int SlotsCleared, string? Error);

/// <summary>
/// Performs inventory removals. Removals only work under ServerSideCharacters: the vanilla client
/// discards an inbound PlayerSlot packet aimed at its own player when SSC is off (see
/// <see cref="RemovalBlockedReason"/>), so every operation here is gated on it. All public methods
/// must run on the main server thread; the plugin marshals REST calls in via the dispatcher.
/// </summary>
public sealed class InventoryManager
{
    /// <summary>
    /// Null when removals are authoritative, otherwise the reason they are refused.
    /// <para>
    /// Terraria's <c>MessageBuffer.GetData</c> case 5 (PlayerSlot) begins with
    /// <c>if (player == Main.myPlayer &amp;&amp; !Main.ServerSideCharacter &amp;&amp;
    /// !Main.player[player].HasLockedInventory()) break;</c> — without SSC the owning client throws
    /// the packet away before applying anything. Clearing server-side anyway would only desync our
    /// copy from the client's (reads and snapshots would report an item the player still holds),
    /// so removal is refused outright rather than half-applied.
    /// </para>
    /// </summary>
    public static string? RemovalBlockedReason() => RemovalBlockedReason(Main.ServerSideCharacter);

    /// <summary>
    /// Testable core of <see cref="RemovalBlockedReason()"/>. Split out because touching
    /// <c>Terraria.Main</c> at all runs its static constructor, which needs a live server.
    /// </summary>
    internal static string? RemovalBlockedReason(bool serverSideCharacters) =>
        serverSideCharacters
            ? null
            : "ServerSideCharacters is disabled, so the client owns its inventory and ignores "
              + "server-side slot updates. Removal is unsupported on non-SSC characters.";

    // ---- Public operations (main thread) --------------------------------------------------

    public RemoveOneResult RemoveSlot(TSPlayer tsp, int globalSlot)
    {
        if (RemovalBlockedReason() is { } blocked)
            return new RemoveOneResult(false, null, blocked);
        if (globalSlot < 0 || globalSlot >= SlotMap.MaxSlot)
            return new RemoveOneResult(false, null, $"slot must be 0..{SlotMap.MaxSlot - 1}");

        var item = SlotMap.GetItem(tsp.TPlayer, globalSlot);
        if (!IsOccupied(item))
            return new RemoveOneResult(false, null, "slot is empty");

        var entry = ToEntry(globalSlot, item!);
        ClearSlot(tsp, globalSlot);
        return new RemoveOneResult(true, entry, null);
    }

    public RemoveByTypeResult RemoveByType(TSPlayer tsp, int netId, int amount)
    {
        if (RemovalBlockedReason() is { } blocked)
            return new RemoveByTypeResult(netId, 0, 0, blocked);

        int remaining = amount <= 0 ? int.MaxValue : amount;
        int slots = 0, count = 0;

        foreach (var (global, _, _, item) in SlotMap.Enumerate(tsp.TPlayer, ReportGroups.All))
        {
            if (remaining <= 0)
                break;
            if (item.type != netId || !IsOccupied(item))
                continue;

            int take = Math.Min(item.stack, remaining);
            if (take >= item.stack)
            {
                ClearSlot(tsp, global);
            }
            else
            {
                item.stack -= take;
                SendSlot(tsp, global, item.prefix);
            }

            remaining -= take;
            count += take;
            slots++;
        }

        return new RemoveByTypeResult(netId, slots, count, null);
    }

    public ClearResult Clear(TSPlayer tsp, string? scope)
    {
        var (filter, scopeName) = ResolveClearScope(scope);
        if (RemovalBlockedReason() is { } blocked)
            return new ClearResult(scopeName, 0, blocked);

        var p = tsp.TPlayer;
        int cleared = 0;

        foreach (var seg in SlotMap.Segments)
        {
            if (!filter(seg))
                continue;

            var arr = seg.ArrayOf(p);
            if (arr is null)
                continue;

            for (int i = 0; i < seg.Count && i < arr.Length; i++)
            {
                if (!IsOccupied(arr[i]))
                    continue;

                ClearSlot(tsp, seg.Start + i);
                cleared++;
            }
        }

        return new ClearResult(scopeName, cleared, null);
    }

    // ---- Internals ------------------------------------------------------------------------

    private static void ClearSlot(TSPlayer tsp, int globalSlot)
    {
        var item = SlotMap.GetItem(tsp.TPlayer, globalSlot);
        if (item is null)
            return;

        item.TurnToAir(true);
        SendSlot(tsp, globalSlot, 0);
    }

    /// <summary>
    /// Broadcasts the (now updated) server-side slot via PlayerSlot. Under SSC the owning client
    /// applies it to its own inventory; other clients update any visible equipment. number =
    /// player index, number2 = global slot, number3 = prefix (all confirmed against Terraria's
    /// packet-5 layout).
    /// </summary>
    private static void SendSlot(TSPlayer tsp, int globalSlot, int prefix) =>
        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, NetworkText.Empty,
            tsp.Index, globalSlot, prefix, 0f, 0, 0, 0);

    internal static (Func<SlotSegment, bool> Filter, string Name) ResolveClearScope(string? scope)
    {
        switch ((scope ?? "all").Trim().ToLowerInvariant())
        {
            case "main": return (s => s.Name == "Inventory", "main");
            case "core": return (s => (s.Group & ReportGroups.Core) != 0, "core");
            case "storage": return (s => (s.Group & ReportGroups.Storage) != 0, "storage");
            case "misc": return (s => (s.Group & ReportGroups.Misc) != 0, "misc");
            case "loadouts": return (s => (s.Group & ReportGroups.Loadouts) != 0, "loadouts");
            default: return (_ => true, "all");
        }
    }

    private static bool IsOccupied(Item? item) =>
        item is not null && item.active && item.type != 0 && item.stack > 0;

    private static SlotEntry ToEntry(int globalSlot, Item item)
    {
        SlotMap.TryLocate(globalSlot, out var seg, out int local);
        return new SlotEntry
        {
            Slot = local,
            GlobalSlot = globalSlot,
            NetId = item.type,
            Name = item.Name ?? "",
            Stack = item.stack,
            Prefix = item.prefix,
            Favorited = item.favorited,
        };
    }
}
