namespace InventoryMonitor;

/// <summary>
/// Permission nodes used by the plugin. Add these to TShock groups (e.g. via
/// <c>/group addperm &lt;group&gt; invmonitor.read</c>) to grant access. REST tokens are
/// authorized against the <c>rest.*</c> nodes via the token's associated user group.
/// </summary>
public static class Permissions
{
    // In-game command nodes.
    public const string Read = "invmonitor.read";
    public const string Remove = "invmonitor.remove";
    public const string Clear = "invmonitor.clear";
    public const string Snapshots = "invmonitor.snapshots";

    // REST endpoint nodes.
    public const string RestRead = "invmonitor.rest.read";
    public const string RestRemove = "invmonitor.rest.remove";
    public const string RestClear = "invmonitor.rest.clear";
    public const string RestSnapshots = "invmonitor.rest.snapshots";
    public const string RestItemNames = "invmonitor.rest.itemnames";
}
