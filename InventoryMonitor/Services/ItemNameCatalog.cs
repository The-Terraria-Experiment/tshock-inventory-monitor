using Terraria;
using Terraria.ID;

namespace InventoryMonitor.Services;

/// <summary>A netId -&gt; display name map for every item the running Terraria build knows about.</summary>
public sealed record ItemCatalog(string Version, Dictionary<string, string> Items);

/// <summary>
/// Builds the item-name catalog by asking Terraria itself for each item's name, which is the only
/// reliable source for a server that has no extractable game files. Names come from the loaded
/// localization, so the catalog matches exactly what the read/snapshot endpoints report.
///
/// The result is immutable for the life of the process and is built once, then cached: a build
/// walks ~6k items through <see cref="Item.netDefaults"/> and is far too coarse to run on the main
/// thread per request.
/// </summary>
public static class ItemNameCatalog
{
    /// <summary>
    /// How far below zero to probe. Negative net ids are a small fixed block of item variants
    /// (-1..-48 as of 1.4.5) rather than a range Terraria exposes a count for. Probing well past
    /// the known end costs nothing here and picks up any future additions; ids outside the block
    /// come back as an empty item and are skipped.
    /// </summary>
    private const int NegativeProbeFloor = -256;

    private static readonly object Gate = new();
    private static ItemCatalog? _cached;

    /// <summary>
    /// Returns the catalog, building it on first use. Must be called on the main thread — item
    /// defaults and localization are shared Terraria state.
    /// </summary>
    public static ItemCatalog Get()
    {
        lock (Gate)
        {
            return _cached ??= Build();
        }
    }

    private static ItemCatalog Build()
    {
        // Insertion order carries into the JSON object: variants first, then ids ascending.
        var items = new Dictionary<string, string>(ItemID.Count + 64);

        for (int netId = NegativeProbeFloor; netId < 0; netId++)
            Add(items, netId);

        for (int netId = 1; netId < ItemID.Count; netId++)
            Add(items, netId);

        return new ItemCatalog(TerrariaVersion(), items);
    }

    private static void Add(Dictionary<string, string> items, int netId)
    {
        var item = new Item();
        try
        {
            item.netDefaults(netId);
        }
        catch
        {
            return; // unknown id — nothing to name
        }

        // type 0 is "no item": either an unassigned id or a probe past the end of the variant block.
        if (item.type == 0)
            return;

        string? name = null;
        try { name = item.Name; }
        catch { /* localization unavailable for this id — fall through */ }

        items[netId.ToString()] = string.IsNullOrEmpty(name) ? $"Item {netId}" : name;
    }

    /// <summary>Terraria's version as bare digits (<c>1.4.5.6</c>), stripping its <c>v</c> prefix.</summary>
    private static string TerrariaVersion()
    {
        try
        {
            return (Main.versionNumber ?? "").Trim().TrimStart('v', 'V');
        }
        catch
        {
            return "";
        }
    }
}
