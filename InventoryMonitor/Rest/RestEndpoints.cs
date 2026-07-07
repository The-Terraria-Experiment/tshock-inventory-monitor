using InventoryMonitor.Config;
using InventoryMonitor.Models;
using InventoryMonitor.Services;
using Rests;
using TShockAPI;

namespace InventoryMonitor.Rest;

/// <summary>
/// Registers and handles the plugin's REST endpoints. Handlers run on the HTTP listener thread and
/// marshal all Terraria access onto the main thread via <see cref="MainThreadDispatcher"/>.
/// </summary>
public sealed class RestEndpoints
{
    private readonly MainThreadDispatcher _dispatcher;
    private readonly InventoryManager _manager;
    private readonly InvMonitorConfig _config;

    public RestEndpoints(MainThreadDispatcher dispatcher, InventoryManager manager, InvMonitorConfig config)
    {
        _dispatcher = dispatcher;
        _manager = manager;
        _config = config;
    }

    public void Register(SecureRest api)
    {
        api.Register(new SecureRestCommand("/inventory/read", ReadPlayer, Permissions.RestRead));
        api.Register(new SecureRestCommand("/inventory/readall", ReadAll, Permissions.RestRead));
        api.Register(new SecureRestCommand("/inventory/removeslot", RemoveSlot, Permissions.RestRemove));
        api.Register(new SecureRestCommand("/inventory/removeitem", RemoveItem, Permissions.RestRemove));
        api.Register(new SecureRestCommand("/inventory/clear", Clear, Permissions.RestClear));
    }

    private int Timeout => _config.MainThreadTimeoutMs;

    // ---- Handlers -------------------------------------------------------------------------

    private object ReadPlayer(RestRequestArgs args)
    {
        var (player, error) = FindPlayer(args.Parameters["player"]);
        if (error is not null)
            return error;

        var groups = ParseGroups(args.Parameters["include"]);
        var report = _dispatcher.Invoke(() => InventoryReader.BuildReport(player!, groups), Timeout);
        return Success(new() { { "player", report } });
    }

    private object ReadAll(RestRequestArgs args)
    {
        var groups = ParseGroups(args.Parameters["include"]);
        int cap = _config.ReadAllMaxPlayers;

        var reports = _dispatcher.Invoke(() =>
        {
            var list = new List<PlayerReport>();
            foreach (var tsp in TShock.Players)
            {
                if (tsp is null || !tsp.Active)
                    continue;
                if (list.Count >= cap)
                    break;
                list.Add(InventoryReader.BuildReport(tsp, groups));
            }

            return list;
        }, Timeout);

        return Success(new() { { "playercount", reports.Count }, { "players", reports } });
    }

    private object RemoveSlot(RestRequestArgs args)
    {
        var (player, error) = FindPlayer(args.Parameters["player"]);
        if (error is not null)
            return error;

        if (!int.TryParse(args.Parameters["slot"], out int slot))
            return Error("Missing or invalid 'slot' (expected 0.." + (SlotMap.MaxSlot - 1) + ").");

        var result = _dispatcher.Invoke(() => _manager.RemoveSlot(player!, slot), Timeout);
        if (!result.Removed)
            return Error(result.Error ?? "nothing removed");

        return Success(new()
        {
            { "removed", true },
            { "item", result.Item },
            { "note", RemovalNote() },
        });
    }

    private object RemoveItem(RestRequestArgs args)
    {
        var (player, error) = FindPlayer(args.Parameters["player"]);
        if (error is not null)
            return error;

        var (netId, itemError) = ResolveItem(args.Parameters["item"]);
        if (itemError is not null)
            return itemError;

        int amount = int.TryParse(args.Parameters["amount"], out int a) ? a : 0; // <=0 => all
        var result = _dispatcher.Invoke(() => _manager.RemoveByType(player!, netId, amount), Timeout);

        return Success(new()
        {
            { "netId", result.NetId },
            { "slotsAffected", result.SlotsAffected },
            { "countRemoved", result.CountRemoved },
            { "note", RemovalNote() },
        });
    }

    private object Clear(RestRequestArgs args)
    {
        var (player, error) = FindPlayer(args.Parameters["player"]);
        if (error is not null)
            return error;

        string? scope = args.Parameters["scope"];
        var result = _dispatcher.Invoke(() => _manager.Clear(player!, scope), Timeout);

        return Success(new()
        {
            { "scope", result.Scope },
            { "slotsCleared", result.SlotsCleared },
            { "note", RemovalNote() },
        });
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static (TSPlayer? Player, RestObject? Error) FindPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, Error("Missing 'player' parameter."));

        var found = TSPlayer.FindByNameOrID(name);
        return found.Count switch
        {
            1 => (found[0], null),
            0 => (null, Error($"Player '{name}' was not found.")),
            _ => (null, Error($"Player '{name}' matches {found.Count} players; be more specific.")),
        };
    }

    private static (int NetId, RestObject? Error) ResolveItem(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return (0, Error("Missing 'item' parameter (item id or name)."));

        if (int.TryParse(item, out int id))
            return (id, null);

        var matches = TShock.Utils.GetItemByIdOrName(item);
        return matches.Count switch
        {
            1 => (matches[0].type, null),
            0 => (0, Error($"No item matched '{item}'.")),
            _ => (0, Error($"Item '{item}' matched {matches.Count} items; use the numeric id.")),
        };
    }

    internal static ReportGroups ParseGroups(string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
            return ReportGroups.All;

        ReportGroups groups = ReportGroups.None;
        foreach (var token in include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            groups |= token.ToLowerInvariant() switch
            {
                "core" => ReportGroups.Core,
                "storage" => ReportGroups.Storage,
                "misc" => ReportGroups.Misc,
                "loadouts" => ReportGroups.Loadouts,
                "all" => ReportGroups.All,
                _ => ReportGroups.None,
            };
        }

        return groups == ReportGroups.None ? ReportGroups.All : groups;
    }

    private string RemovalNote() =>
        _config.RemovalRetryCount > 0
            ? $"Cleared server-side and pushed to client; will re-apply up to {_config.RemovalRetryCount}x if the client re-syncs."
            : "Cleared server-side and pushed to client (single best-effort attempt).";

    private static RestObject Success(RestObject data) => data;

    private static RestObject Error(string message) => new("400") { { "error", message } };
}
