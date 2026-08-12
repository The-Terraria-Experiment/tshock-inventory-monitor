using System.Globalization;
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
    private readonly SnapshotService _snapshots;
    private readonly InvMonitorConfig _config;

    public RestEndpoints(
        MainThreadDispatcher dispatcher,
        InventoryManager manager,
        SnapshotService snapshots,
        InvMonitorConfig config)
    {
        _dispatcher = dispatcher;
        _manager = manager;
        _snapshots = snapshots;
        _config = config;
    }

    public void Register(SecureRest api)
    {
        api.Register(new SecureRestCommand("/inventory/read", ReadPlayer, Permissions.RestRead));
        api.Register(new SecureRestCommand("/inventory/readall", ReadAll, Permissions.RestRead));
        api.Register(new SecureRestCommand("/inventory/removeslot", RemoveSlot, Permissions.RestRemove));
        api.Register(new SecureRestCommand("/inventory/removeitem", RemoveItem, Permissions.RestRemove));
        api.Register(new SecureRestCommand("/inventory/clear", Clear, Permissions.RestClear));
        api.Register(new SecureRestCommand("/inventory/snapshots", Snapshots, Permissions.RestSnapshots));
        api.Register(new SecureRestCommand("/inventory/snapshot", Snapshot, Permissions.RestSnapshots));
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

    // ---- Snapshots ------------------------------------------------------------------------

    /// <summary>
    /// Polling endpoint. Returns cached join/leave snapshots with id &gt; <c>since</c>, oldest
    /// first. The consumer stores the returned <c>cursor</c> and passes it back as <c>since</c>
    /// on the next poll. No main-thread hop: the store is independently synchronized.
    /// </summary>
    private object Snapshots(RestRequestArgs args)
    {
        long since = long.TryParse(args.Parameters["since"], out long s) ? s : 0;

        var (kind, kindError) = ParseKind(args.Parameters["kind"]);
        if (kindError is not null)
            return kindError;

        var (fromUtc, fromError) = ParseUtc(args.Parameters["from"], "from");
        if (fromError is not null)
            return fromError;

        var (toUtc, toError) = ParseUtc(args.Parameters["to"], "to");
        if (toError is not null)
            return toError;

        if (fromUtc is { } f && toUtc is { } t && t < f)
            return Error("'to' is earlier than 'from'.");

        string? player = string.IsNullOrWhiteSpace(args.Parameters["player"]) ? null : args.Parameters["player"];
        int limit = int.TryParse(args.Parameters["limit"], out int l) && l > 0
            ? l
            : _config.SnapshotQueryDefaultLimit;

        bool metaOnly = IsTrue(args.Parameters["meta"]);
        bool newestFirst = IsTrue(args.Parameters["latest"]);
        var groups = ParseGroups(args.Parameters["include"]);

        var store = _snapshots.Store;
        var found = store.Query(new SnapshotQuery
        {
            SinceId = since,
            PlayerName = player,
            Kind = kind,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            NewestFirst = newestFirst,
            Limit = limit,
        });

        // Highest id in the batch, so a consumer capped by `limit` resumes exactly where it left
        // off. Taken as a max rather than "last element" so it stays correct under `latest=true`,
        // where results come back newest-first.
        long cursor = found.Count > 0 ? found.Max(sn => sn.Id) : since;

        return Success(new()
        {
            { "snapshots", metaOnly
                ? found.Select(sn => (object)sn.ToSummary()).ToList()
                : found.Select(sn => (object)Projected(sn, groups)).ToList() },
            { "count", found.Count },
            { "cursor", cursor },
            { "head", store.Cursor },
            // Only true when the page filled up AND newer ids exist — i.e. drain again now. A
            // short page means the consumer is caught up with everything matching its filter.
            { "more", found.Count >= limit && cursor < store.Cursor },
            { "retained", store.Count },
            { "oldestRetainedUtc", store.OldestRetainedUtc },
            { "note", SnapshotNote() },
        });
    }

    /// <summary>Fetches a single cached snapshot by id.</summary>
    private object Snapshot(RestRequestArgs args)
    {
        if (!long.TryParse(args.Parameters["id"], out long id))
            return Error("Missing or invalid 'id'.");

        var snapshot = _snapshots.Store.GetById(id);
        if (snapshot is null)
            return Error($"Snapshot {id} is not retained (expired, evicted, or never existed).");

        var groups = ParseGroups(args.Parameters["include"]);
        return Success(new()
        {
            { "snapshot", Projected(snapshot, groups) },
            { "note", SnapshotNote() },
        });
    }

    /// <summary>
    /// Rebuilds a snapshot with its report narrowed to the requested groups. Snapshots are always
    /// captured in full, so this is purely a response-shaping filter.
    /// </summary>
    private static InventorySnapshot Projected(InventorySnapshot snapshot, ReportGroups groups) =>
        groups == ReportGroups.All
            ? snapshot
            : new InventorySnapshot
            {
                Id = snapshot.Id,
                KindValue = snapshot.KindValue,
                CapturedAtUtc = snapshot.CapturedAtUtc,
                Player = InventoryReader.Project(snapshot.Player, groups),
            };

    private static (SnapshotKind? Kind, RestObject? Error) ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return (null, null);

        return kind.Trim().ToLowerInvariant() switch
        {
            "join" => (SnapshotKind.Join, null),
            "leave" => (SnapshotKind.Leave, null),
            _ => (null, Error($"Invalid 'kind' '{kind}' (expected 'join' or 'leave').")),
        };
    }

    /// <summary>
    /// Parses an ISO-8601 timestamp into UTC. A value with no offset is read as UTC rather than
    /// server-local time, so consumers in another timezone can't silently shift their window.
    /// </summary>
    private static (DateTime? Value, RestObject? Error) ParseUtc(string? raw, string parameter)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);

        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return (null, Error($"Invalid '{parameter}' timestamp '{raw}' (expected ISO-8601, e.g. 2026-08-12T18:00:00Z)."));

        return (parsed, null);
    }

    private static bool IsTrue(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private string SnapshotNote() =>
        $"Snapshots are in-memory only (retained {_config.SnapshotRetentionMinutes} min, " +
        $"max {_config.SnapshotMaxEntries}) and are lost on restart. Without SSC they reflect the " +
        "last inventory the client synced, not an authoritative record.";

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
