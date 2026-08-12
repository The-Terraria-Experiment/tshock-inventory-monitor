using System.Text.Json;
using TShockAPI;

namespace InventoryMonitor.Config;

/// <summary>
/// Plugin configuration, persisted as <c>InventoryMonitor.json</c> in the TShock save path.
/// </summary>
public sealed class InvMonitorConfig
{
    /// <summary>
    /// How many times, after the initial clear, to re-clear a slot the client re-syncs back.
    /// Non-SSC only: this resolves benign client/server sync races. It is NOT anti-cheat
    /// enforcement — a client that keeps re-adding an item will win; ban it instead.
    /// Set to 0 for a single best-effort clear with no retries.
    /// </summary>
    public int RemovalRetryCount { get; set; } = 3;

    /// <summary>Ticks (~60/sec) to wait between removal verification passes.</summary>
    public int RemovalRetryIntervalTicks { get; set; } = 20;

    /// <summary>Safety cap on how many players a single <c>readall</c> will serialize.</summary>
    public int ReadAllMaxPlayers { get; set; } = 255;

    /// <summary>Timeout (ms) a REST/off-thread call waits for its main-thread work to run.</summary>
    public int MainThreadTimeoutMs { get; set; } = 3000;

    // ---- Join/leave snapshots -------------------------------------------------------------

    /// <summary>Capture a snapshot shortly after each player finishes connecting.</summary>
    public bool CaptureJoinSnapshots { get; set; } = true;

    /// <summary>Capture a snapshot as each player disconnects.</summary>
    public bool CaptureLeaveSnapshots { get; set; } = true;

    /// <summary>
    /// Ticks (~60/sec) to wait after a player is greeted before taking the join snapshot, so the
    /// client's initial inventory packets have all been applied. Raise it if joins look empty.
    /// </summary>
    public int JoinSnapshotDelayTicks { get; set; } = 60;

    /// <summary>
    /// Most join snapshots to build in a single game tick. A mass join would otherwise serialize
    /// hundreds of full inventories in one frame; the overflow waits for the next tick instead.
    /// 0 disables the budget (capture everything due, immediately).
    /// </summary>
    public int MaxJoinCapturesPerTick { get; set; } = 25;

    /// <summary>
    /// How long a snapshot stays queryable. Snapshots are memory-only and do not survive a
    /// restart. Set to 0 to disable age-based eviction and rely on
    /// <see cref="SnapshotMaxEntries"/> alone.
    /// </summary>
    public int SnapshotRetentionMinutes { get; set; } = 60;

    /// <summary>
    /// Hard cap on retained snapshots; the oldest are dropped first. 0 means unbounded. Sized for
    /// event-scale bursts (a 300-player event produces ~600 snapshots per join/leave cycle), so
    /// that <see cref="SnapshotRetentionMinutes"/> is normally the binding constraint rather than
    /// this. Each retained snapshot holds a full 350-slot report, so raising it costs memory.
    /// </summary>
    public int SnapshotMaxEntries { get; set; } = 20000;

    /// <summary>
    /// Default page size for a snapshot query that does not pass its own <c>limit</c>. Batch
    /// consumers should set <c>limit</c> explicitly and drain while the response says <c>more</c>.
    /// </summary>
    public int SnapshotQueryDefaultLimit { get; set; } = 100;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(TShock.SavePath, "InventoryMonitor.json");

    /// <summary>Loads the config, writing defaults if the file is missing or unreadable.</summary>
    public static InvMonitorConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<InvMonitorConfig>(json, Options);
                if (cfg is not null)
                    return cfg;
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[InventoryMonitor] Failed to read config, using defaults: {ex.Message}");
        }

        var fresh = new InvMonitorConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[InventoryMonitor] Failed to write config: {ex.Message}");
        }
    }
}
