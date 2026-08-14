using InventoryMonitor.Models;
using InventoryMonitor.Services;
using TShockAPI;

namespace InventoryMonitor.Commands;

/// <summary>
/// In-game <c>/inv</c> command, a thin dispatcher over the same services the REST layer uses.
/// Commands already execute on the main server thread, so services are called directly.
/// </summary>
public sealed class InvCommands
{
    private readonly InventoryManager _manager;
    private readonly SnapshotService _snapshots;

    public InvCommands(InventoryManager manager, SnapshotService snapshots)
    {
        _manager = manager;
        _snapshots = snapshots;
    }

    public void Handle(CommandArgs args)
    {
        var p = args.Player;
        var ps = args.Parameters;

        if (ps.Count == 0)
        {
            ShowHelp(p);
            return;
        }

        switch (ps[0].ToLowerInvariant())
        {
            case "read": Read(p, ps); break;
            case "readall": ReadAll(p, ps); break;
            case "removeslot": RemoveSlot(p, ps); break;
            case "removeitem": RemoveItem(p, ps); break;
            case "clear": Clear(p, ps); break;
            case "snapshots": Snapshots(p, ps); break;
            case "snapshot": Snapshot(p, ps); break;
            default: ShowHelp(p); break;
        }
    }

    private static void ShowHelp(TSPlayer p)
    {
        p.SendInfoMessage("Inventory Monitor commands:");
        p.SendInfoMessage("  /inv read <player> [page] - show a player's inventory, equipment, buffs, stats");
        p.SendInfoMessage("  /inv readall - summarize every online player");
        p.SendInfoMessage("  /inv removeslot <player> <slot> - [SSC] remove a single global slot (0.." + (SlotMap.MaxSlot - 1) + ")");
        p.SendInfoMessage("  /inv removeitem <player> <id|name> [amount] - [SSC] remove item(s) by type");
        p.SendInfoMessage("  /inv clear <player> [all|main|storage|core|misc|loadouts] - [SSC] clear inventory");
        p.SendInfoMessage("  /inv snapshots [player] [page] - list cached join/leave snapshots");
        p.SendInfoMessage("  /inv snapshot <id> [page] - show one cached snapshot in full");

        if (InventoryManager.RemovalBlockedReason() is not null)
            p.SendInfoMessage("[SSC] commands are disabled: this server runs without ServerSideCharacters.");
    }

    // ---- snapshots --------------------------------------------------------------------------

    private void Snapshots(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Snapshots))
            return;

        // Trailing numeric argument is the page; anything else is a player filter.
        string? player = null;
        int page = 1;
        if (ps.Count >= 2)
        {
            if (int.TryParse(ps[^1], out int pg) && ps.Count == 2)
                page = pg;
            else
            {
                player = ps[1];
                if (ps.Count >= 3 && int.TryParse(ps[2], out int pg2))
                    page = pg2;
            }
        }

        var store = _snapshots.Store;
        var found = store.Query(new SnapshotQuery { PlayerName = player, Limit = int.MaxValue });

        var lines = found
            .Select(s => $"#{s.Id} {s.Kind,-5} {s.Player.Name} - {s.ItemCount} items, " +
                         $"{FormatAge(s.CapturedAtUtc)} ago")
            .ToList();

        PaginationTools.SendPage(p, page, lines, new PaginationTools.Settings
        {
            HeaderFormat = player is null
                ? $"Cached snapshots, {store.Count} retained ({{0}}/{{1}}):"
                : $"Cached snapshots for {player} ({{0}}/{{1}}):",
            FooterFormat = $"Type /inv snapshots{(player is null ? "" : " " + player)} {{0}} for more. " +
                           "Use /inv snapshot <id> for detail.",
            NothingToDisplayString = player is null
                ? "No snapshots cached yet."
                : $"No cached snapshots for '{player}'.",
        });
    }

    private void Snapshot(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Snapshots))
            return;
        if (ps.Count < 2 || !long.TryParse(ps[1], out long id))
        {
            p.SendErrorMessage("Usage: /inv snapshot <id> [page]");
            return;
        }

        var snapshot = _snapshots.Store.GetById(id);
        if (snapshot is null)
        {
            p.SendErrorMessage($"Snapshot {id} is not retained (expired, evicted, or never existed).");
            return;
        }

        int page = ps.Count >= 3 && int.TryParse(ps[2], out int pg) ? pg : 1;
        var report = snapshot.Player;

        var lines = new List<string>
        {
            $"Captured {FormatAge(snapshot.CapturedAtUtc)} ago ({snapshot.CapturedAtUtc:u})" +
            (snapshot.Stale ? " - non-SSC, last client-synced state" : ""),
            $"Life {report.Stats.Life}/{report.Stats.LifeMax}  Mana {report.Stats.Mana}/{report.Stats.ManaMax}" +
            $"  Group {report.Group}  Pos {report.Position}",
        };

        if (report.Buffs.Count > 0)
            lines.Add("Buffs: " + string.Join(", ", report.Buffs.Select(b =>
                $"{b.Name}({(b.SecondsRemaining < 0 ? "perm" : b.SecondsRemaining + "s")})")));

        foreach (var c in report.Containers)
        {
            lines.Add($"== {c.Name} ==");
            foreach (var it in c.Items)
                lines.Add($"  [{it.GlobalSlot}] {Describe(it)}");
        }

        PaginationTools.SendPage(p, page, lines, new PaginationTools.Settings
        {
            HeaderFormat = $"Snapshot #{snapshot.Id} ({snapshot.Kind}) of {report.Name} ({{0}}/{{1}}):",
            FooterFormat = $"Type /inv snapshot {snapshot.Id} {{0}} for more.",
            NothingToDisplayString = "This snapshot is empty.",
        });
    }

    private static string FormatAge(DateTime capturedUtc)
    {
        var age = DateTime.UtcNow - capturedUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        return age.TotalMinutes < 1
            ? $"{(int)age.TotalSeconds}s"
            : age.TotalHours < 1
                ? $"{(int)age.TotalMinutes}m"
                : $"{(int)age.TotalHours}h{age.Minutes:00}m";
    }

    // ---- read -----------------------------------------------------------------------------

    private static void Read(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Read))
            return;
        if (ps.Count < 2)
        {
            p.SendErrorMessage("Usage: /inv read <player> [page]");
            return;
        }

        var target = FindOnline(p, ps[1]);
        if (target is null)
            return;

        int page = ps.Count >= 3 && int.TryParse(ps[2], out int pg) ? pg : 1;
        var report = InventoryReader.BuildReport(target, ReportGroups.All);

        var lines = new List<string>
        {
            $"Life {report.Stats.Life}/{report.Stats.LifeMax}  Mana {report.Stats.Mana}/{report.Stats.ManaMax}" +
            $"  Group {report.Group}  Pos {report.Position}",
        };

        if (report.Buffs.Count > 0)
            lines.Add("Buffs: " + string.Join(", ", report.Buffs.Select(b =>
                $"{b.Name}({(b.SecondsRemaining < 0 ? "perm" : b.SecondsRemaining + "s")})")));

        foreach (var c in report.Containers)
        {
            lines.Add($"== {c.Name} ==");
            foreach (var it in c.Items)
                lines.Add($"  [{it.GlobalSlot}] {Describe(it)}");
        }

        PaginationTools.SendPage(p, page, lines, new PaginationTools.Settings
        {
            HeaderFormat = $"Inventory of {report.Name} ({{0}}/{{1}}):",
            FooterFormat = $"Type /inv read {report.Name} {{0}} for more.",
            NothingToDisplayString = "This player has no items.",
        });
    }

    private static void ReadAll(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Read))
            return;

        int page = ps.Count >= 2 && int.TryParse(ps[1], out int pg) ? pg : 1;

        var lines = new List<string>();
        foreach (var tsp in TShock.Players)
        {
            if (tsp is null || !tsp.Active)
                continue;

            var r = InventoryReader.BuildReport(tsp, ReportGroups.All);
            int itemCount = r.Containers.Sum(c => c.Items.Count);
            lines.Add($"{r.Name} (idx {r.Index}) - {itemCount} items, {r.Buffs.Count} buffs, " +
                      $"life {r.Stats.Life}/{r.Stats.LifeMax}");
        }

        PaginationTools.SendPage(p, page, lines, new PaginationTools.Settings
        {
            HeaderFormat = "Online players ({0}/{1}):",
            FooterFormat = "Type /inv readall {0} for more. Use /inv read <player> for detail.",
            NothingToDisplayString = "No players online.",
        });
    }

    // ---- removals -------------------------------------------------------------------------

    private void RemoveSlot(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Remove) || !RequireSsc(p))
            return;
        if (ps.Count < 3 || !int.TryParse(ps[2], out int slot))
        {
            p.SendErrorMessage("Usage: /inv removeslot <player> <slot 0.." + (SlotMap.MaxSlot - 1) + ">");
            return;
        }

        var target = FindOnline(p, ps[1]);
        if (target is null)
            return;

        var result = _manager.RemoveSlot(target, slot);
        if (!result.Removed)
        {
            p.SendErrorMessage($"Nothing removed: {result.Error}");
            return;
        }

        p.SendSuccessMessage($"Removed {Describe(result.Item!)} from slot {slot} of {target.Name}.");
    }

    private void RemoveItem(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Remove) || !RequireSsc(p))
            return;
        if (ps.Count < 3)
        {
            p.SendErrorMessage("Usage: /inv removeitem <player> <id|name> [amount]");
            return;
        }

        var target = FindOnline(p, ps[1]);
        if (target is null)
            return;

        int netId;
        if (int.TryParse(ps[2], out int id))
        {
            netId = id;
        }
        else
        {
            var matches = TShock.Utils.GetItemByIdOrName(ps[2]);
            if (matches.Count == 0)
            {
                p.SendErrorMessage($"No item matched '{ps[2]}'.");
                return;
            }

            if (matches.Count > 1)
            {
                p.SendErrorMessage($"'{ps[2]}' matched {matches.Count} items; use the numeric id.");
                return;
            }

            netId = matches[0].type;
        }

        int amount = ps.Count >= 4 && int.TryParse(ps[3], out int a) ? a : 0;
        var result = _manager.RemoveByType(target, netId, amount);

        if (result.SlotsAffected == 0)
            p.SendInfoMessage($"{target.Name} had no item {netId}.");
        else
            p.SendSuccessMessage($"Removed {result.CountRemoved} of item {netId} across {result.SlotsAffected} slot(s) from {target.Name}.");
    }

    private void Clear(TSPlayer p, List<string> ps)
    {
        if (!Require(p, Permissions.Clear) || !RequireSsc(p))
            return;
        if (ps.Count < 2)
        {
            p.SendErrorMessage("Usage: /inv clear <player> [all|main|storage|core|misc|loadouts]");
            return;
        }

        var target = FindOnline(p, ps[1]);
        if (target is null)
            return;

        string? scope = ps.Count >= 3 ? ps[2] : null;
        var result = _manager.Clear(target, scope);
        p.SendSuccessMessage($"Cleared {result.SlotsCleared} slot(s) [{result.Scope}] from {target.Name}.");
    }

    // ---- helpers --------------------------------------------------------------------------

    private static bool Require(TSPlayer p, string permission)
    {
        if (p.HasPermission(permission))
            return true;

        p.SendErrorMessage($"You do not have permission to use this command ({permission}).");
        return false;
    }

    /// <summary>Refuses a removal when the client, not the server, owns the inventory.</summary>
    private static bool RequireSsc(TSPlayer p)
    {
        if (InventoryManager.RemovalBlockedReason() is not { } reason)
            return true;

        p.SendErrorMessage(reason);
        p.SendErrorMessage("Enable ServerSideCharacters in TShock's config to allow removals.");
        return false;
    }

    private static TSPlayer? FindOnline(TSPlayer p, string name)
    {
        var found = TSPlayer.FindByNameOrID(name);
        switch (found.Count)
        {
            case 1:
                return found[0];
            case 0:
                p.SendErrorMessage($"Player '{name}' was not found.");
                return null;
            default:
                p.SendErrorMessage($"'{name}' matches {found.Count} players; be more specific.");
                return null;
        }
    }

    private static string Describe(SlotEntry it)
    {
        string prefix = it.PrefixName is not null ? it.PrefixName + " " : "";
        string stack = it.Stack > 1 ? $" x{it.Stack}" : "";
        string fav = it.Favorited ? " *" : "";
        return $"{prefix}{it.Name} (id {it.NetId}){stack}{fav}";
    }
}
