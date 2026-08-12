using InventoryMonitor.Config;
using InventoryMonitor.Models;

namespace InventoryMonitor.Services;

/// <summary>
/// Filter for <see cref="SnapshotStore.Query"/>. Every field is optional except
/// <see cref="Limit"/>; unset fields do not narrow the result.
/// </summary>
public sealed record SnapshotQuery
{
    /// <summary>Exclusive id floor: only snapshots with a greater id are returned.</summary>
    public long SinceId { get; init; }

    /// <summary>Exact captured name, matched case-insensitively.</summary>
    public string? PlayerName { get; init; }

    public SnapshotKind? Kind { get; init; }

    /// <summary>Inclusive lower bound on capture time (UTC).</summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>
    /// Exclusive upper bound on capture time (UTC). Half-open so adjacent windows can be chained
    /// without overlapping or dropping an entry on the boundary.
    /// </summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>
    /// Return the newest matches instead of the oldest, ordered newest-first. Intended for ad-hoc
    /// lookups; in-order draining should leave this false so batches stay contiguous.
    /// </summary>
    public bool NewestFirst { get; init; }

    public required int Limit { get; init; }
}

/// <summary>
/// Bounded in-memory cache of join/leave snapshots, ordered by ascending <see cref="InventorySnapshot.Id"/>.
/// Evicted by age (<see cref="InvMonitorConfig.SnapshotRetentionMinutes"/>) and by count
/// (<see cref="InvMonitorConfig.SnapshotMaxEntries"/>), oldest first.
///
/// Unlike the rest of the plugin this type is NOT main-thread-only: leave snapshots are added from
/// the server loop thread, join snapshots from the main thread, and queries arrive on the REST
/// listener thread. Every access is therefore taken under <c>_gate</c>.
/// </summary>
public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private readonly List<InventorySnapshot> _entries = new(); // ascending by Id
    private readonly InvMonitorConfig _config;
    private long _lastId;

    public SnapshotStore(InvMonitorConfig config) => _config = config;

    /// <summary>Highest id issued so far. A consumer that polls with this value sees only newer entries.</summary>
    public long Cursor
    {
        get { lock (_gate) return _lastId; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Capture time of the oldest retained snapshot, or null when empty.</summary>
    public DateTime? OldestRetainedUtc
    {
        get { lock (_gate) return _entries.Count > 0 ? _entries[0].CapturedAtUtc : null; }
    }

    public InventorySnapshot Add(SnapshotKind kind, PlayerReport report, DateTime utcNow)
    {
        lock (_gate)
        {
            var snapshot = new InventorySnapshot
            {
                Id = ++_lastId,
                KindValue = kind,
                CapturedAtUtc = utcNow,
                Player = report,
            };

            _entries.Add(snapshot);
            TrimToCapacity();
            return snapshot;
        }
    }

    /// <summary>Drops snapshots older than the retention window. Returns how many were evicted.</summary>
    public int Prune(DateTime utcNow)
    {
        int minutes = _config.SnapshotRetentionMinutes;
        if (minutes <= 0)
            return 0; // retention disabled => age-based eviction off; the count cap still applies

        var cutoff = utcNow.AddMinutes(-minutes);

        lock (_gate)
        {
            // Entries are ascending by capture time, so the expired ones are a prefix.
            int expired = 0;
            while (expired < _entries.Count && _entries[expired].CapturedAtUtc < cutoff)
                expired++;

            if (expired > 0)
                _entries.RemoveRange(0, expired);

            return expired;
        }
    }

    /// <summary>
    /// Returns retained snapshots matching <paramref name="query"/>, oldest first (or newest first
    /// when <see cref="SnapshotQuery.NewestFirst"/> is set), capped at
    /// <see cref="SnapshotQuery.Limit"/>.
    /// </summary>
    public IReadOnlyList<InventorySnapshot> Query(SnapshotQuery query)
    {
        if (query.Limit <= 0)
            return Array.Empty<InventorySnapshot>();

        lock (_gate)
        {
            var results = new List<InventorySnapshot>();

            if (query.NewestFirst)
            {
                for (int i = _entries.Count - 1; i >= 0 && results.Count < query.Limit; i--)
                {
                    if (Matches(_entries[i], query))
                        results.Add(_entries[i]);
                }
            }
            else
            {
                for (int i = 0; i < _entries.Count && results.Count < query.Limit; i++)
                {
                    if (Matches(_entries[i], query))
                        results.Add(_entries[i]);
                }
            }

            return results;
        }
    }

    private static bool Matches(InventorySnapshot entry, SnapshotQuery query)
    {
        if (entry.Id <= query.SinceId)
            return false;
        if (query.Kind is not null && entry.KindValue != query.Kind)
            return false;
        if (query.FromUtc is { } from && entry.CapturedAtUtc < from)
            return false;
        if (query.ToUtc is { } to && entry.CapturedAtUtc >= to)
            return false;
        if (query.PlayerName is not null &&
            !string.Equals(entry.Player.Name, query.PlayerName, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public InventorySnapshot? GetById(long id)
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (entry.Id == id)
                    return entry;
            }

            return null;
        }
    }

    /// <summary>Drops every retained snapshot. The id cursor keeps advancing so consumers see a gap, not a rewind.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            int dropped = _entries.Count;
            _entries.Clear();
            return dropped;
        }
    }

    /// <summary>Caller must hold <c>_gate</c>.</summary>
    private void TrimToCapacity()
    {
        int max = _config.SnapshotMaxEntries;
        if (max <= 0)
        {
            // A non-positive cap means "keep nothing" would be surprising; treat it as unbounded
            // and rely on the retention window instead.
            return;
        }

        if (_entries.Count > max)
            _entries.RemoveRange(0, _entries.Count - max);
    }
}
