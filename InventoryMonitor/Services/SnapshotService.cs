using System.Collections.Concurrent;
using InventoryMonitor.Config;
using InventoryMonitor.Models;
using Terraria;
using TShockAPI;

namespace InventoryMonitor.Services;

/// <summary>
/// Captures a player's inventory as they join and as they leave, into <see cref="SnapshotStore"/>.
///
/// The two sides have deliberately different shapes, because their hooks have different
/// constraints:
///
/// <list type="bullet">
/// <item><b>Leave</b> is captured inline, on the server loop thread. TSAPI raises ServerLeave from a
/// pre-hook on <c>RemoteClient.Reset()</c>, whose body then runs <c>Main.player[Id] = new Player()</c>.
/// Deferring the read to the next GameUpdate would therefore capture an empty player, so this one
/// cannot be marshalled — it reads Terraria state off-thread by necessity.</item>
/// <item><b>Join</b> is deferred. NetGreetPlayer also fires off-thread, but nothing is about to
/// destroy the player, so the capture is queued and taken on the main thread a configurable number
/// of ticks later — which additionally lets the client's inbound slot packets settle.</item>
/// </list>
/// </summary>
public sealed class SnapshotService
{
    /// <summary>A join capture waiting for its delay to elapse.</summary>
    private sealed class PendingJoin
    {
        public int Index;
        public string Name = "";
        public int TicksLeft;
    }

    /// <summary>How often <see cref="Tick"/> runs age-based eviction (~10s at 60 ticks/sec).</summary>
    private const int PruneIntervalTicks = 600;

    private readonly SnapshotStore _store;
    private readonly InvMonitorConfig _config;

    // Written from the network thread, drained on the main thread.
    private readonly ConcurrentQueue<PendingJoin> _incoming = new();

    // Main-thread only.
    private readonly List<PendingJoin> _pending = new();
    private int _pruneCountdown = PruneIntervalTicks;

    public SnapshotService(SnapshotStore store, InvMonitorConfig config)
    {
        _store = store;
        _config = config;
    }

    public SnapshotStore Store => _store;

    // ---- Hook entry points (both off the main thread) --------------------------------------

    /// <summary>
    /// NetGreetPlayer. Schedules a deferred main-thread capture rather than reading here, so the
    /// client's initial PlayerSlot packets have landed before we look.
    /// </summary>
    public void OnPlayerGreeted(int who)
    {
        if (!_config.CaptureJoinSnapshots)
            return;
        if (who < 0 || who >= Main.player.Length)
            return;

        var name = ResolveName(who);
        if (string.IsNullOrEmpty(name))
            return;

        _incoming.Enqueue(new PendingJoin
        {
            Index = who,
            Name = name,
            TicksLeft = Math.Max(1, _config.JoinSnapshotDelayTicks),
        });
    }

    /// <summary>
    /// ServerLeave. Captures synchronously — see the type remarks; by the next GameUpdate the
    /// player's inventory no longer exists.
    /// </summary>
    public void OnPlayerLeaving(int who)
    {
        if (!_config.CaptureLeaveSnapshots)
            return;
        if (who < 0 || who >= Main.player.Length)
            return;

        try
        {
            // TShock's own ServerLeave handler nulls TShock.Players[who] as its first statement.
            // We currently run before it (hooks are invoked in descending plugin Order, and ours
            // is 1 vs TShock's 0), but don't rely on that: fall back to the raw Terraria player.
            var tsp = who < TShock.Players.Length ? TShock.Players[who] : null;
            var player = tsp?.TPlayer ?? Main.player[who];
            if (player is null)
                return;

            // Clients that dropped mid-handshake never sent an inventory; their Player is a blank
            // placeholder and a snapshot of it would be noise.
            bool received = tsp is not null ? tsp.ReceivedInfo : !string.IsNullOrEmpty(player.name);
            if (!received)
                return;

            var identity = tsp is not null
                ? InventoryReader.IdentityOf(tsp)
                : InventoryReader.IdentityOf(player, who);

            var report = InventoryReader.BuildReport(player, identity, ReportGroups.All);
            var snapshot = _store.Add(SnapshotKind.Leave, report, DateTime.UtcNow);
            LogCaptured(snapshot);
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[InventoryMonitor] Failed to capture leave snapshot for index {who}: {ex.Message}");
        }
    }

    // ---- Main thread -----------------------------------------------------------------------

    /// <summary>Pumped once per GameUpdate: runs due join captures and ages out old snapshots.</summary>
    public void Tick()
    {
        while (_incoming.TryDequeue(out var queued))
            _pending.Add(queued);

        // Age everything first, so entries held back by the per-tick budget below still become due
        // on schedule rather than having their delay silently extended.
        foreach (var join in _pending)
        {
            if (join.TicksLeft > 0)
                join.TicksLeft--;
        }

        // Then capture what's due, in arrival order, up to this tick's budget. A mass join (event
        // start, or a server restart everyone reconnects to) would otherwise build hundreds of full
        // reports in a single frame; the overflow simply waits for the next tick.
        int budget = _config.MaxJoinCapturesPerTick > 0 ? _config.MaxJoinCapturesPerTick : int.MaxValue;
        int captured = 0;
        int index = 0;

        while (index < _pending.Count && captured < budget)
        {
            if (_pending[index].TicksLeft > 0)
            {
                index++;
                continue;
            }

            var join = _pending[index];
            _pending.RemoveAt(index);
            CaptureJoin(join);
            captured++;
        }

        if (--_pruneCountdown <= 0)
        {
            _pruneCountdown = PruneIntervalTicks;
            _store.Prune(DateTime.UtcNow);
        }
    }

    private void CaptureJoin(PendingJoin join)
    {
        try
        {
            if (join.Index < 0 || join.Index >= TShock.Players.Length)
                return;

            var tsp = TShock.Players[join.Index];

            // The player may have left (or the slot been recycled) during the delay.
            if (tsp is null || !tsp.Active || tsp.TPlayer is null || tsp.Name != join.Name)
                return;

            var report = InventoryReader.BuildReport(tsp, ReportGroups.All);
            var snapshot = _store.Add(SnapshotKind.Join, report, DateTime.UtcNow);
            LogCaptured(snapshot);
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[InventoryMonitor] Failed to capture join snapshot for {join.Name}: {ex.Message}");
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static string ResolveName(int who)
    {
        var tsp = who < TShock.Players.Length ? TShock.Players[who] : null;
        if (tsp is not null && !string.IsNullOrEmpty(tsp.Name))
            return tsp.Name;

        return Main.player[who]?.name ?? "";
    }

    private static void LogCaptured(InventorySnapshot snapshot) =>
        TShock.Log.ConsoleDebug(
            $"[InventoryMonitor] Snapshot #{snapshot.Id} ({snapshot.Kind}) for {snapshot.Player.Name}: " +
            $"{snapshot.ItemCount} items.");
}
