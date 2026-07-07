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
