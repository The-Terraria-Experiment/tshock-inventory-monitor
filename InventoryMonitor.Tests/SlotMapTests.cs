using InventoryMonitor.Models;
using InventoryMonitor.Services;
using TShockAPI;
using Xunit;

namespace InventoryMonitor.Tests;

/// <summary>
/// Guards the canonical slot layout. These are the tests most likely to catch a breaking TShock or
/// Terraria update: the layout is cross-checked against TShock's own <see cref="NetItem"/> ranges,
/// so drift on either side fails here rather than silently corrupting reads/removals in production.
/// </summary>
public class SlotMapTests
{
    // Segment name -> the NetItem (start, end) range it must exactly match.
    private static readonly (string Name, Tuple<int, int> Range)[] Expected =
    {
        ("Inventory",     NetItem.InventoryIndex),
        ("Armor",         NetItem.ArmorIndex),
        ("Dyes",          NetItem.DyeIndex),
        ("MiscEquips",    NetItem.MiscEquipIndex),
        ("MiscDyes",      NetItem.MiscDyeIndex),
        ("PiggyBank",     NetItem.PiggyIndex),
        ("Safe",          NetItem.SafeIndex),
        ("Trash",         NetItem.TrashIndex),
        ("Forge",         NetItem.ForgeIndex),
        ("VoidVault",     NetItem.VoidIndex),
        ("Loadout1Armor", NetItem.Loadout1Armor),
        ("Loadout1Dyes",  NetItem.Loadout1Dye),
        ("Loadout2Armor", NetItem.Loadout2Armor),
        ("Loadout2Dyes",  NetItem.Loadout2Dye),
        ("Loadout3Armor", NetItem.Loadout3Armor),
        ("Loadout3Dyes",  NetItem.Loadout3Dye),
    };

    [Fact]
    public void Segments_Match_NetItem_Ranges_Exactly()
    {
        var byName = SlotMap.Segments.ToDictionary(s => s.Name);

        foreach (var (name, range) in Expected)
        {
            Assert.True(byName.ContainsKey(name), $"missing segment '{name}'");
            var seg = byName[name];
            Assert.Equal(range.Item1, seg.Start);
            Assert.Equal(range.Item2, seg.End);
            Assert.Equal(range.Item2 - range.Item1, seg.Count);
        }

        // No extra segments beyond the ones we assert on.
        Assert.Equal(Expected.Length, SlotMap.Segments.Count);
    }

    [Fact]
    public void Segments_Are_Contiguous_And_Cover_Zero_To_MaxInventory()
    {
        var ordered = SlotMap.Segments.OrderBy(s => s.Start).ToList();

        Assert.Equal(0, ordered[0].Start);
        for (int i = 1; i < ordered.Count; i++)
            Assert.Equal(ordered[i - 1].End, ordered[i].Start); // no gaps, no overlaps

        Assert.Equal(NetItem.MaxInventory, ordered[^1].End);
        Assert.Equal(NetItem.MaxInventory, SlotMap.MaxSlot);
    }

    [Theory]
    [InlineData(0, "Inventory", 0)]
    [InlineData(58, "Inventory", 58)]
    [InlineData(59, "Armor", 0)]
    [InlineData(78, "Armor", 19)]
    [InlineData(179, "Trash", 0)]
    [InlineData(180, "Forge", 0)]
    [InlineData(260, "Loadout1Armor", 0)]
    [InlineData(349, "Loadout3Dyes", 9)]
    public void TryLocate_Maps_Boundaries_To_Correct_Segment_And_Local(int global, string segment, int local)
    {
        Assert.True(SlotMap.TryLocate(global, out var seg, out int localIndex));
        Assert.Equal(segment, seg.Name);
        Assert.Equal(local, localIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(350)]
    [InlineData(9999)]
    public void TryLocate_Rejects_Out_Of_Range(int global)
    {
        Assert.False(SlotMap.TryLocate(global, out _, out _));
    }

    [Fact]
    public void Every_ReportGroup_Has_At_Least_One_Segment()
    {
        foreach (var group in new[] { ReportGroups.Core, ReportGroups.Storage, ReportGroups.Misc, ReportGroups.Loadouts })
            Assert.Contains(SlotMap.Segments, s => s.Group == group);
    }
}
