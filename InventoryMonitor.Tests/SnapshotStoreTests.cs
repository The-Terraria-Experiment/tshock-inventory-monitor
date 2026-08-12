using InventoryMonitor.Config;
using InventoryMonitor.Models;
using InventoryMonitor.Services;
using Xunit;

namespace InventoryMonitor.Tests;

/// <summary>
/// Covers the snapshot cache's contract with external consumers: monotonic ids that never rewind,
/// cursor-based polling, and the two independent eviction policies (age and capacity).
/// </summary>
public class SnapshotStoreTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SnapshotStore NewStore(int maxEntries = 100, int retentionMinutes = 60) =>
        new(new InvMonitorConfig
        {
            SnapshotMaxEntries = maxEntries,
            SnapshotRetentionMinutes = retentionMinutes,
        });

    /// <summary>Unfiltered query with a generous limit — the base most tests narrow from.</summary>
    private static SnapshotQuery All(int limit = 100) => new() { Limit = limit };

    private static PlayerReport Report(string name, int items = 0)
    {
        var container = new ContainerReport { Name = "Inventory", Group = nameof(ReportGroups.Core) };
        for (int i = 0; i < items; i++)
            container.Items.Add(new SlotEntry { Slot = i, GlobalSlot = i, NetId = 1 + i, Stack = 1 });

        return new PlayerReport { Name = name, Containers = { container } };
    }

    [Fact]
    public void Add_Assigns_Monotonic_Ids_And_Advances_Cursor()
    {
        var store = NewStore();

        var first = store.Add(SnapshotKind.Join, Report("Caleb"), T0);
        var second = store.Add(SnapshotKind.Leave, Report("Caleb"), T0.AddMinutes(1));

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(2, store.Cursor);
        Assert.Equal("join", first.Kind);
        Assert.Equal("leave", second.Kind);
    }

    [Fact]
    public void Query_Returns_Only_Entries_Newer_Than_Cursor_Oldest_First()
    {
        var store = NewStore();
        store.Add(SnapshotKind.Join, Report("A"), T0);
        store.Add(SnapshotKind.Leave, Report("A"), T0.AddMinutes(1));
        store.Add(SnapshotKind.Join, Report("B"), T0.AddMinutes(2));

        var page = store.Query(All() with { SinceId = 1 });

        Assert.Equal(new long[] { 2, 3 }, page.Select(s => s.Id));
    }

    [Fact]
    public void Query_Filters_By_Player_Case_Insensitively()
    {
        var store = NewStore();
        store.Add(SnapshotKind.Join, Report("Caleb"), T0);
        store.Add(SnapshotKind.Join, Report("Someone"), T0);

        var page = store.Query(All() with { PlayerName = "caleb" });

        Assert.Single(page);
        Assert.Equal("Caleb", page[0].Player.Name);
    }

    [Fact]
    public void Query_Filters_By_Kind()
    {
        var store = NewStore();
        store.Add(SnapshotKind.Join, Report("A"), T0);
        store.Add(SnapshotKind.Leave, Report("A"), T0.AddMinutes(1));

        var leaves = store.Query(All() with { Kind = SnapshotKind.Leave });

        Assert.Single(leaves);
        Assert.Equal("leave", leaves[0].Kind);
    }

    [Fact]
    public void Query_Respects_Limit_So_Caller_Can_Resume()
    {
        var store = NewStore();
        for (int i = 0; i < 5; i++)
            store.Add(SnapshotKind.Join, Report("A"), T0.AddMinutes(i));

        var first = store.Query(All(limit: 2));
        Assert.Equal(new long[] { 1, 2 }, first.Select(s => s.Id));

        // Resuming from the last returned id yields the remainder with no gap or repeat.
        var next = store.Query(All(limit: 2) with { SinceId = first[^1].Id });
        Assert.Equal(new long[] { 3, 4 }, next.Select(s => s.Id));
    }

    [Fact]
    public void Query_Time_Window_Is_From_Inclusive_And_To_Exclusive()
    {
        var store = NewStore();
        for (int i = 0; i < 4; i++)
            store.Add(SnapshotKind.Join, Report("A"), T0.AddMinutes(i)); // ids 1..4 at T0..T0+3

        var window = store.Query(All() with { FromUtc = T0.AddMinutes(1), ToUtc = T0.AddMinutes(3) });

        // Includes the entry exactly on `from`, excludes the one exactly on `to`.
        Assert.Equal(new long[] { 2, 3 }, window.Select(s => s.Id));
    }

    [Fact]
    public void Adjacent_Time_Windows_Neither_Overlap_Nor_Drop_A_Boundary_Entry()
    {
        var store = NewStore();
        for (int i = 0; i < 6; i++)
            store.Add(SnapshotKind.Join, Report("A"), T0.AddMinutes(i));

        var firstHalf = store.Query(All() with { FromUtc = T0, ToUtc = T0.AddMinutes(3) });
        var secondHalf = store.Query(All() with { FromUtc = T0.AddMinutes(3), ToUtc = T0.AddMinutes(6) });

        var combined = firstHalf.Concat(secondHalf).Select(s => s.Id).ToList();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, combined);
        Assert.Equal(combined.Count, combined.Distinct().Count());
    }

    [Fact]
    public void Query_Time_Window_Composes_With_Other_Filters()
    {
        var store = NewStore();
        store.Add(SnapshotKind.Join, Report("A"), T0);
        store.Add(SnapshotKind.Leave, Report("A"), T0.AddMinutes(1));
        store.Add(SnapshotKind.Leave, Report("B"), T0.AddMinutes(2));
        store.Add(SnapshotKind.Leave, Report("A"), T0.AddMinutes(9));

        var found = store.Query(All() with
        {
            PlayerName = "A",
            Kind = SnapshotKind.Leave,
            FromUtc = T0,
            ToUtc = T0.AddMinutes(5),
        });

        Assert.Equal(new long[] { 2 }, found.Select(s => s.Id));
    }

    [Fact]
    public void NewestFirst_Returns_The_Most_Recent_Matches_In_Descending_Order()
    {
        var store = NewStore();
        for (int i = 0; i < 5; i++)
            store.Add(SnapshotKind.Leave, Report("A"), T0.AddMinutes(i));

        var latest = store.Query(All(limit: 2) with { NewestFirst = true });

        Assert.Equal(new long[] { 5, 4 }, latest.Select(s => s.Id));
    }

    [Fact]
    public void NewestFirst_With_Limit_One_Returns_The_Newest_Not_The_Oldest()
    {
        // The trap this flag exists to remove: an oldest-first query capped at 1 returns the
        // stalest entry, which is never what a point lookup wants.
        var store = NewStore();
        store.Add(SnapshotKind.Leave, Report("A"), T0);
        store.Add(SnapshotKind.Leave, Report("A"), T0.AddMinutes(10));

        Assert.Equal(1, store.Query(All(limit: 1))[0].Id);
        Assert.Equal(2, store.Query(All(limit: 1) with { NewestFirst = true })[0].Id);
    }

    [Fact]
    public void Capacity_Eviction_Drops_Oldest_First()
    {
        var store = NewStore(maxEntries: 3);
        for (int i = 0; i < 5; i++)
            store.Add(SnapshotKind.Join, Report("A"), T0.AddMinutes(i));

        Assert.Equal(3, store.Count);
        Assert.Null(store.GetById(1));
        Assert.Null(store.GetById(2));
        Assert.Equal(new long[] { 3, 4, 5 }, store.Query(All()).Select(s => s.Id));
    }

    [Fact]
    public void Prune_Drops_Entries_Past_The_Retention_Window()
    {
        var store = NewStore(retentionMinutes: 30);
        store.Add(SnapshotKind.Join, Report("old"), T0);
        store.Add(SnapshotKind.Join, Report("fresh"), T0.AddMinutes(45));

        int evicted = store.Prune(T0.AddMinutes(50)); // cutoff = T0+20

        Assert.Equal(1, evicted);
        Assert.Equal(1, store.Count);
        Assert.Equal("fresh", store.Query(All())[0].Player.Name);
    }

    [Fact]
    public void Prune_Is_A_Noop_When_Retention_Is_Disabled()
    {
        var store = NewStore(retentionMinutes: 0);
        store.Add(SnapshotKind.Join, Report("ancient"), T0);

        Assert.Equal(0, store.Prune(T0.AddYears(1)));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Clear_Drops_Entries_But_Never_Rewinds_The_Cursor()
    {
        var store = NewStore();
        store.Add(SnapshotKind.Join, Report("A"), T0);
        store.Add(SnapshotKind.Join, Report("B"), T0);

        Assert.Equal(2, store.Clear());
        Assert.Equal(0, store.Count);
        Assert.Equal(2, store.Cursor);

        // A consumer polling from its old cursor sees a gap, not replayed ids.
        var next = store.Add(SnapshotKind.Join, Report("C"), T0);
        Assert.Equal(3, next.Id);
    }

    [Fact]
    public void OldestRetainedUtc_Tracks_The_Front_Of_The_Window()
    {
        var store = NewStore();
        Assert.Null(store.OldestRetainedUtc);

        store.Add(SnapshotKind.Join, Report("A"), T0);
        store.Add(SnapshotKind.Join, Report("B"), T0.AddMinutes(5));

        Assert.Equal(T0, store.OldestRetainedUtc);
    }

    [Fact]
    public void ItemCount_Counts_Every_Container()
    {
        var store = NewStore();
        var snapshot = store.Add(SnapshotKind.Leave, Report("A", items: 4), T0);

        Assert.Equal(4, snapshot.ItemCount);
        Assert.Equal(4, snapshot.ToSummary().ItemCount);
    }

    [Fact]
    public void Concurrent_Adds_Never_Reuse_An_Id()
    {
        // Leave snapshots are written from the server loop thread while join snapshots are written
        // from the main thread, so Add must be safe under contention.
        var store = NewStore(maxEntries: 0); // unbounded, so nothing is evicted mid-test
        const int perThread = 200;

        Parallel.For(0, 4, _ =>
        {
            for (int i = 0; i < perThread; i++)
                store.Add(SnapshotKind.Join, Report("A"), T0);
        });

        var ids = store.Query(All(int.MaxValue)).Select(s => s.Id).ToList();

        Assert.Equal(4 * perThread, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(x => x), ids); // still ascending
    }
}
