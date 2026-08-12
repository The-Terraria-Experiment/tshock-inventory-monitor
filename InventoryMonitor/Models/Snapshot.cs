namespace InventoryMonitor.Models;

/// <summary>Which side of a play session a snapshot was taken on.</summary>
public enum SnapshotKind
{
    Join,
    Leave,
}

/// <summary>
/// A point-in-time capture of one player's inventory surface, taken as they joined or left.
/// Snapshots are immutable and live only in memory (see <see cref="Services.SnapshotStore"/>);
/// a server restart drops them.
/// </summary>
public sealed class InventorySnapshot
{
    /// <summary>
    /// Monotonically increasing id, unique for the lifetime of the process. External consumers
    /// poll with <c>since=&lt;id&gt;</c> to fetch only what they have not seen yet.
    /// </summary>
    public long Id { get; init; }

    /// <summary>"join" or "leave" — a string so REST consumers never depend on enum ordinals.</summary>
    public string Kind => KindValue == SnapshotKind.Join ? "join" : "leave";

    /// <summary>Internal (non-serialized) form of <see cref="Kind"/>, used for filtering.</summary>
    internal SnapshotKind KindValue { get; init; }

    public DateTime CapturedAtUtc { get; init; }

    /// <summary>
    /// The captured state. On a non-SSC server this is the last inventory the client synced to
    /// the server, not an authoritative record — see <see cref="Stale"/>.
    /// </summary>
    public PlayerReport Player { get; init; } = new();

    /// <summary>Convenience count so consumers can triage without walking every container.</summary>
    public int ItemCount => Player.Containers.Sum(c => c.Items.Count);

    /// <summary>
    /// True when the server did not own the inventory at capture time (non-SSC). The snapshot
    /// then reflects the last state the client chose to sync, which may lag a final pickup or
    /// drop by a few packets.
    /// </summary>
    public bool Stale => !Player.ServerSideCharacter;

    /// <summary>Projects to the lightweight form used by list endpoints.</summary>
    public SnapshotSummary ToSummary() => new()
    {
        Id = Id,
        Kind = Kind,
        CapturedAtUtc = CapturedAtUtc,
        Name = Player.Name,
        Account = Player.Account,
        Index = Player.Index,
        ItemCount = ItemCount,
        Stale = Stale,
    };
}

/// <summary>Metadata-only view of a snapshot, for cheap listing/indexing.</summary>
public sealed class SnapshotSummary
{
    public long Id { get; init; }
    public string Kind { get; init; } = "";
    public DateTime CapturedAtUtc { get; init; }
    public string Name { get; init; } = "";
    public string? Account { get; init; }
    public int Index { get; init; }
    public int ItemCount { get; init; }
    public bool Stale { get; init; }
}
