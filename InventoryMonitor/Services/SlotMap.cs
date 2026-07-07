using InventoryMonitor.Models;
using Terraria;

namespace InventoryMonitor.Services;

/// <summary>
/// A contiguous block of item slots that all live in one backing array on the player.
/// The <see cref="Start"/> offset is the canonical NetItem/PlayerSlot index of local slot 0.
/// </summary>
public sealed class SlotSegment
{
    public required string Name { get; init; }
    public required ReportGroups Group { get; init; }
    public required int Start { get; init; }
    public required int Count { get; init; }

    /// <summary>Returns the backing item array for this segment on the given player (or null if unavailable).</summary>
    public required Func<Player, Item[]?> ArrayOf { get; init; }

    public int End => Start + Count; // exclusive
}

/// <summary>
/// Single source of truth mapping the canonical global slot index (0..349, matching
/// <c>TShockAPI.NetItem</c>) to the concrete backing array + local index on a
/// <see cref="Terraria.Player"/>. Used by both reading (enumeration) and writing (removal),
/// so the two can never disagree about what a slot number means.
/// </summary>
public static class SlotMap
{
    // Ranges intentionally mirror TShockAPI.NetItem.*Index, which in turn match Terraria's
    // PlayerSlot packet slot numbering. Kept as literals here so read + write share one map.
    public static readonly IReadOnlyList<SlotSegment> Segments = new SlotSegment[]
    {
        new() { Name = "Inventory",   Group = ReportGroups.Core,     Start = 0,   Count = 59, ArrayOf = p => p.inventory },
        new() { Name = "Armor",       Group = ReportGroups.Core,     Start = 59,  Count = 20, ArrayOf = p => p.armor },
        new() { Name = "Dyes",        Group = ReportGroups.Core,     Start = 79,  Count = 10, ArrayOf = p => p.dye },
        new() { Name = "MiscEquips",  Group = ReportGroups.Misc,     Start = 89,  Count = 5,  ArrayOf = p => p.miscEquips },
        new() { Name = "MiscDyes",    Group = ReportGroups.Misc,     Start = 94,  Count = 5,  ArrayOf = p => p.miscDyes },
        new() { Name = "PiggyBank",   Group = ReportGroups.Storage,  Start = 99,  Count = 40, ArrayOf = p => p.bank?.item },
        new() { Name = "Safe",        Group = ReportGroups.Storage,  Start = 139, Count = 40, ArrayOf = p => p.bank2?.item },
        new() { Name = "Trash",       Group = ReportGroups.Storage,  Start = 179, Count = 1,  ArrayOf = p => p.trashItem is null ? null : new[] { p.trashItem } },
        new() { Name = "Forge",       Group = ReportGroups.Storage,  Start = 180, Count = 40, ArrayOf = p => p.bank3?.item },
        new() { Name = "VoidVault",   Group = ReportGroups.Storage,  Start = 220, Count = 40, ArrayOf = p => p.bank4?.item },
        new() { Name = "Loadout1Armor", Group = ReportGroups.Loadouts, Start = 260, Count = 20, ArrayOf = p => LoadoutArmor(p, 0) },
        new() { Name = "Loadout1Dyes",  Group = ReportGroups.Loadouts, Start = 280, Count = 10, ArrayOf = p => LoadoutDye(p, 0) },
        new() { Name = "Loadout2Armor", Group = ReportGroups.Loadouts, Start = 290, Count = 20, ArrayOf = p => LoadoutArmor(p, 1) },
        new() { Name = "Loadout2Dyes",  Group = ReportGroups.Loadouts, Start = 310, Count = 10, ArrayOf = p => LoadoutDye(p, 1) },
        new() { Name = "Loadout3Armor", Group = ReportGroups.Loadouts, Start = 320, Count = 20, ArrayOf = p => LoadoutArmor(p, 2) },
        new() { Name = "Loadout3Dyes",  Group = ReportGroups.Loadouts, Start = 340, Count = 10, ArrayOf = p => LoadoutDye(p, 2) },
    };

    /// <summary>Highest addressable global slot + 1 (matches NetItem.MaxInventory = 350).</summary>
    public static int MaxSlot => Segments[^1].End;

    private static Item[]? LoadoutArmor(Player p, int i) =>
        p.Loadouts is { } l && i < l.Length ? l[i]?.Armor : null;

    private static Item[]? LoadoutDye(Player p, int i) =>
        p.Loadouts is { } l && i < l.Length ? l[i]?.Dye : null;

    /// <summary>Finds the segment and local index that a global slot belongs to.</summary>
    public static bool TryLocate(int globalSlot, out SlotSegment segment, out int localIndex)
    {
        foreach (var seg in Segments)
        {
            if (globalSlot >= seg.Start && globalSlot < seg.End)
            {
                segment = seg;
                localIndex = globalSlot - seg.Start;
                return true;
            }
        }

        segment = null!;
        localIndex = -1;
        return false;
    }

    /// <summary>Resolves the live <see cref="Item"/> object backing a global slot, or null.</summary>
    public static Item? GetItem(Player player, int globalSlot)
    {
        if (!TryLocate(globalSlot, out var seg, out var local))
            return null;

        var arr = seg.ArrayOf(player);
        return arr is not null && local < arr.Length ? arr[local] : null;
    }

    /// <summary>Enumerates every slot (including empty ones) in the requested container groups.</summary>
    public static IEnumerable<(int Global, SlotSegment Segment, int Local, Item Item)> Enumerate(
        Player player, ReportGroups groups)
    {
        foreach (var seg in Segments)
        {
            if ((seg.Group & groups) == 0)
                continue;

            var arr = seg.ArrayOf(player);
            if (arr is null)
                continue;

            for (int i = 0; i < seg.Count && i < arr.Length; i++)
            {
                var item = arr[i];
                if (item is not null)
                    yield return (seg.Start + i, seg, i, item);
            }
        }
    }
}
