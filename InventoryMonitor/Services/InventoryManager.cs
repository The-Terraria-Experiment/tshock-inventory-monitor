using InventoryMonitor.Config;
using InventoryMonitor.Models;
using Terraria;
using Terraria.Localization;
using TShockAPI;

namespace InventoryMonitor.Services;

public readonly record struct RemoveOneResult(bool Removed, SlotEntry? Item, string? Error);
public readonly record struct RemoveByTypeResult(int NetId, int SlotsAffected, int CountRemoved);
public readonly record struct ClearResult(string Scope, int SlotsCleared);

/// <summary>
/// Performs inventory removals and re-applies them for a bounded window to survive benign
/// client/server sync races (non-SSC). All public mutation methods and <see cref="Tick"/> must
/// run on the main server thread; the plugin marshals REST calls in via the dispatcher.
/// </summary>
public sealed class InventoryManager
{
    private sealed class VerifyJob
    {
        public int PlayerIndex;
        public string PlayerName = "";
        public int GlobalSlot;
        public int ExpectedNetId; // -1 => re-clear whatever occupies the slot
        public int AttemptsLeft;
        public int TicksLeft;
    }

    private readonly InvMonitorConfig _config;
    private readonly List<VerifyJob> _jobs = new(); // main-thread only

    public InventoryManager(InvMonitorConfig config) => _config = config;

    // ---- Public operations (main thread) --------------------------------------------------

    public RemoveOneResult RemoveSlot(TSPlayer tsp, int globalSlot)
    {
        if (globalSlot < 0 || globalSlot >= SlotMap.MaxSlot)
            return new RemoveOneResult(false, null, $"slot must be 0..{SlotMap.MaxSlot - 1}");

        var item = SlotMap.GetItem(tsp.TPlayer, globalSlot);
        if (!IsOccupied(item))
            return new RemoveOneResult(false, null, "slot is empty");

        var entry = ToEntry(globalSlot, item!);
        int netId = item!.type;
        ClearSlot(tsp, globalSlot);
        EnqueueVerify(tsp, globalSlot, netId);
        return new RemoveOneResult(true, entry, null);
    }

    public RemoveByTypeResult RemoveByType(TSPlayer tsp, int netId, int amount)
    {
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
                EnqueueVerify(tsp, global, netId);
            }
            else
            {
                item.stack -= take;
                SendSlot(tsp, global, item.prefix);
                // partial reduction: no verify job (the item legitimately remains)
            }

            remaining -= take;
            count += take;
            slots++;
        }

        return new RemoveByTypeResult(netId, slots, count);
    }

    public ClearResult Clear(TSPlayer tsp, string? scope)
    {
        var (filter, scopeName) = ResolveClearScope(scope);
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

                int global = seg.Start + i;
                ClearSlot(tsp, global);
                EnqueueVerify(tsp, global, -1);
                cleared++;
            }
        }

        return new ClearResult(scopeName, cleared);
    }

    // ---- Verification retry loop (main thread, pumped from GameUpdate) ---------------------

    public void Tick()
    {
        if (_jobs.Count == 0)
            return;

        for (int i = _jobs.Count - 1; i >= 0; i--)
        {
            var job = _jobs[i];
            if (--job.TicksLeft > 0)
                continue;

            var tsp = ResolvePlayer(job);
            if (tsp is null)
            {
                _jobs.RemoveAt(i);
                continue;
            }

            var item = SlotMap.GetItem(tsp.TPlayer, job.GlobalSlot);
            bool present = IsOccupied(item) && (job.ExpectedNetId < 0 || item!.type == job.ExpectedNetId);
            if (!present)
            {
                _jobs.RemoveAt(i); // removal held
                continue;
            }

            // Client re-synced the item back — clear it again.
            ClearSlot(tsp, job.GlobalSlot);
            if (--job.AttemptsLeft <= 0)
            {
                TShock.Log.ConsoleInfo(
                    $"[InventoryMonitor] Gave up re-clearing slot {job.GlobalSlot} for {tsp.Name}: " +
                    "client keeps re-adding it. Not enforceable without SSC — consider banning.");
                _jobs.RemoveAt(i);
            }
            else
            {
                job.TicksLeft = Math.Max(1, _config.RemovalRetryIntervalTicks);
            }
        }
    }

    // ---- Internals ------------------------------------------------------------------------

    private void ClearSlot(TSPlayer tsp, int globalSlot)
    {
        var item = SlotMap.GetItem(tsp.TPlayer, globalSlot);
        if (item is null)
            return;

        item.TurnToAir(true);
        SendSlot(tsp, globalSlot, 0);
    }

    /// <summary>
    /// Broadcasts the (now updated) server-side slot via PlayerSlot. The owning client applies it
    /// to its own inventory; other clients update any visible equipment. number = player index,
    /// number2 = global slot, number3 = prefix (all confirmed against Terraria's packet-5 layout).
    /// </summary>
    private static void SendSlot(TSPlayer tsp, int globalSlot, int prefix) =>
        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, NetworkText.Empty,
            tsp.Index, globalSlot, prefix, 0f, 0, 0, 0);

    private void EnqueueVerify(TSPlayer tsp, int globalSlot, int expectedNetId)
    {
        if (_config.RemovalRetryCount <= 0)
            return;

        _jobs.Add(new VerifyJob
        {
            PlayerIndex = tsp.Index,
            PlayerName = tsp.Name,
            GlobalSlot = globalSlot,
            ExpectedNetId = expectedNetId,
            AttemptsLeft = _config.RemovalRetryCount,
            TicksLeft = Math.Max(1, _config.RemovalRetryIntervalTicks),
        });
    }

    private static TSPlayer? ResolvePlayer(VerifyJob job)
    {
        if (job.PlayerIndex < 0 || job.PlayerIndex >= TShock.Players.Length)
            return null;

        var tsp = TShock.Players[job.PlayerIndex];
        if (tsp is null || !tsp.Active || tsp.Name != job.PlayerName)
            return null; // slot recycled to a different player

        return tsp;
    }

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
