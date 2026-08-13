# InventoryMonitor

A TShock **6.1** plugin (.NET 9) for reading and managing player inventories over the built-in
**REST API** and equivalent **in-game commands**. It reports every item across a player's entire
inventory surface — main inventory, armor/accessories, dyes, misc equips, personal storage
(piggy bank / safe / forge / void vault / trash), the three equipment loadouts — plus active
buffs/status effects and core stats, and it can remove items or clear inventories. It also caches a
snapshot of each player's inventory as they **join and leave**, which an external consumer polls for.

DISCLAIMER: This plugin is almost entirely created by AI. Although it has been tested and used, most of the source code has not been thoroughly human-reviewed. Use with caution.

## Build & install

```sh
dotnet build InventoryMonitor/InventoryMonitor.csproj -c Release
```

Copy `bin/Release/InventoryMonitor.dll` into your server's `ServerPlugins/` folder and restart.
The build references the `TShock` 6.1.0 NuGet package with compile-only assets, so **only
`InventoryMonitor.dll` is produced** — no TShock/OTAPI assemblies are bundled. To auto-copy on
build, pass your server path:

```sh
dotnet build InventoryMonitor/InventoryMonitor.csproj -c Release -p:ServerPluginsPath="C:\TShock\ServerPlugins"
```

## Tests

```sh
dotnet test
```

`InventoryMonitor.Tests` (xUnit) covers the logic that's safe to exercise off-server and most
prone to breaking on a TShock/Terraria update:

- **`SlotMapTests`** — cross-checks every segment against TShock's own `NetItem.*Index` ranges and
  asserts the layout is contiguous and covers 0..`MaxInventory`. If a future TShock/Terraria update
  shifts the slot layout, these fail instead of silently corrupting reads/removals.
- **`MainThreadDispatcherTests`** — inline vs. cross-thread execution, FIFO order, exception
  propagation, and the timeout guard.
- **`ParsingTests`** — `include`/`scope` string parsing for the REST and command layers.
- **`SnapshotStoreTests`** — monotonic ids, cursor-based paging, time-window and newest-first
  queries, both eviction policies (age and capacity), and concurrent `Add` (join and leave
  snapshots are written from different threads).

Removal/packet paths (`InventoryManager` write ops, `InventoryReader`) aren't unit-tested because
they require a live server (packet sends, item/localization data); verify those on a running server
per the steps below.

## Removal & ServerSideCharacters (important)

This server runs **without SSC**, so the Terraria client owns its inventory. Removals are therefore
**best-effort**: the plugin clears the slot server-side, pushes a `PlayerSlot` packet to the client,
then re-applies the clear for a few passes to win benign client/server sync races. A client that
keeps re-adding an item (i.e. a cheat client) will eventually win — this is **not** an anti-cheat
enforcement mechanism; ban such players instead. When the retry budget is exhausted while an item
is still being re-added, the plugin logs a console notice. If SSC is enabled later, the same code
path persists automatically via TShock's character DB (see `ServerSideCharacter` in reports).

> **JSON casing.** TShock serializes REST responses with stock Newtonsoft settings, so the
> top-level keys this plugin sets are lowercase (`snapshots`, `cursor`, `count`) while everything
> nested is PascalCase from the property names (`Id`, `Kind`, `Player.Name`). Worth knowing when
> writing a deserializer.

Tunable in `tshock/InventoryMonitor.json`:

| Key | Default | Meaning |
|-----|---------|---------|
| `RemovalRetryCount` | 3 | Re-clear passes after the initial clear (0 = single best-effort). |
| `RemovalRetryIntervalTicks` | 20 | Ticks (~60/s) between verification passes. |
| `ReadAllMaxPlayers` | 255 | Cap on players serialized by a single `readall`. |
| `MainThreadTimeoutMs` | 3000 | How long a REST call waits for its main-thread work. |

## Join/leave snapshots

The plugin captures each player's full inventory surface twice per session — shortly after they
join, and as they disconnect — and keeps the results in a bounded in-memory cache. Nothing is
pushed anywhere; an external consumer **polls** `/inventory/snapshots` with a cursor.

Snapshots are **memory-only and do not survive a restart**, and on a non-SSC server they capture the
last inventory the client synced to the server — a player who quits instantly after a pickup may
never have sent that slot. Treat them as "last known state", not an audit record.

### Draining in order (the intended pattern)

Each snapshot gets a process-unique, monotonically increasing `id`. The intended consumer **drains
in id order**: store the `cursor` from the last response, pass it back as `since`, repeat while the
response says `more`.

```sh
curl "http://localhost:7878/inventory/snapshots?since=0&limit=200&token=TOKEN"
# -> { "snapshots": [...], "count": 200, "cursor": 8412, "head": 8900, "more": true, ... }
curl "http://localhost:7878/inventory/snapshots?since=8412&limit=200&token=TOKEN"
```

- `cursor` — highest id in this batch; feed it back as `since`. Correct even when the page was
  capped by `limit`, so batches are contiguous with no repeats and no skips.
- `head` — highest id the plugin has issued overall.
- `more` — the page filled up *and* newer ids exist, so drain again immediately. A short page means
  you're caught up.

Note `since` is **exclusive**: passing the current `head` returns an empty list. That's the point —
it's "what's new", not "what's latest".

This pattern pairs well with an external event feed: let a join/leave notification *wake* the
consumer, then drain the cursor rather than looking players up by name. Doing so sidesteps the join
capture delay entirely (a snapshot that isn't ready yet simply arrives in the next drain) and avoids
a burst of per-player requests when a lot of players connect at once.

### Ad-hoc lookups

`latest=true` flips to newest-first, for when you want the most recent rather than the next batch.
Without it, results are oldest-first — so `limit=1` alone returns the *stalest* retained entry, not
the newest.

```sh
# most recent leave snapshot for one player
curl "http://localhost:7878/inventory/snapshots?player=Alice&kind=leave&latest=true&limit=1&token=TOKEN"

# everything captured in a time window (from inclusive, to exclusive)
curl "http://localhost:7878/inventory/snapshots?from=2026-08-12T18:00:00Z&to=2026-08-12T19:00:00Z&token=TOKEN"
```

`from`/`to` are ISO-8601; a value with no offset is read as UTC, not server-local. The window is
half-open so adjacent windows chain without overlapping or dropping a boundary entry.

Because the cache is bounded, a consumer that drains slower than eviction will miss entries —
compare `oldestRetainedUtc` against its own last-seen time to detect a gap, and size retention to
the drain interval.

| Key | Default | Meaning |
|-----|---------|---------|
| `CaptureJoinSnapshots` | `true` | Capture on join. |
| `CaptureLeaveSnapshots` | `true` | Capture on disconnect. |
| `JoinSnapshotDelayTicks` | 60 | Ticks (~60/s) to wait after greet so the client's inventory packets land. Raise if joins look empty. |
| `MaxJoinCapturesPerTick` | 25 | Join snapshots built per game tick; the overflow waits for the next tick (0 = no budget). |
| `SnapshotRetentionMinutes` | 60 | Age-based eviction window (0 = disable, rely on the count cap). |
| `SnapshotMaxEntries` | 20000 | Hard cap; oldest dropped first (0 = unbounded). |
| `SnapshotQueryDefaultLimit` | 100 | Page size when a query omits `limit`. |

### Sizing for large events

A join/leave cycle produces **two** snapshots per player, so a 300-player event generates ~600 —
enough to churn a small cache in minutes and evict data your consumer hasn't drained yet. The
default `SnapshotMaxEntries` is set high so that `SnapshotRetentionMinutes` is normally the binding
constraint (time-bounded, which is predictable) rather than the count cap (burst-bounded, which
isn't). Each retained snapshot holds a full 350-slot report, so the cap costs memory — measure
against your own player inventories before raising it much further.

`MaxJoinCapturesPerTick` exists for the same reason from the other direction: at event start, or
after a restart everyone reconnects to, hundreds of join captures can come due on the same tick.
The budget spreads that work across frames instead of serializing every inventory in one.

### Capture timing

Worth knowing if you extend this. Both hooks fire **off the main server thread**, and they are
handled differently on purpose:

- **Leave** (`ServerLeave`) is raised from a pre-hook on Terraria's `RemoteClient.Reset()`, whose
  body then runs `Main.player[i] = new Player()`. The inventory is intact *during* the hook and gone
  immediately after, so the snapshot is built **inline on the server loop thread**. Deferring it to
  the next `GameUpdate` would capture an empty player.
- **Join** (`NetGreetPlayer`) has no such deadline, so it is queued and taken on the main thread
  `JoinSnapshotDelayTicks` later, which also lets the client's inbound slot packets settle.

TShock's own `ServerLeave` handler nulls `TShock.Players[who]` as its first statement. Hooks are
invoked in **descending** plugin `Order` (the inverse of load order), and this plugin uses `Order = 1`
against TShock's `0`, so it runs first and still sees a live `TSPlayer` — but the capture path falls
back to the raw `Terraria.Player` rather than depending on that.

## Slot numbering

Removal targets a **global slot index** matching TShock's `NetItem` layout:

| Range | Container | Range | Container |
|-------|-----------|-------|-----------|
| 0–58 | Inventory (incl. coins 50–53, ammo 54–57) | 179 | Trash |
| 59–78 | Armor + accessories (+ vanity) | 180–219 | Defender's Forge |
| 79–88 | Dyes | 220–259 | Void Vault |
| 89–93 | Misc equips (pet/light/cart/mount/hook) | 260–289 | Loadout 1 (armor, dyes) |
| 94–98 | Misc dyes | 290–319 | Loadout 2 |
| 99–138 | Piggy Bank | 320–349 | Loadout 3 |
| 139–178 | Safe | | |

## REST endpoints

All endpoints require a REST token (`&token=...`) whose group holds the relevant permission.

| Method | Route | Params | Permission |
|--------|-------|--------|------------|
| GET | `/inventory/read` | `player`, optional `include` | `invmonitor.rest.read` |
| GET | `/inventory/readall` | optional `include` | `invmonitor.rest.read` |
| GET | `/inventory/removeslot` | `player`, `slot` | `invmonitor.rest.remove` |
| GET | `/inventory/removeitem` | `player`, `item` (id or name), optional `amount` | `invmonitor.rest.remove` |
| GET | `/inventory/clear` | `player`, optional `scope` | `invmonitor.rest.clear` |
| GET | `/inventory/snapshots` | optional `since`, `from`, `to`, `player`, `kind`, `latest`, `limit`, `meta`, `include` | `invmonitor.rest.snapshots` |
| GET | `/inventory/snapshot` | `id`, optional `include` | `invmonitor.rest.snapshots` |
| GET | `/inventory/itemnames` | — | `invmonitor.rest.itemnames` |

Snapshot query params:

| Param | Meaning |
|-------|---------|
| `since` | Exclusive id floor — the drain cursor. |
| `from` / `to` | Capture-time window, ISO-8601 UTC. `from` inclusive, `to` exclusive. |
| `player` | Exact captured name, case-insensitive. |
| `kind` | `join` or `leave` (default both). |
| `latest` | `true` returns the newest matches, newest-first, instead of the oldest. |
| `limit` | Page size (default `SnapshotQueryDefaultLimit`). |
| `meta` | `true` returns id/kind/name/time/item-count only, no containers or buffs. |
| `include` | Which container groups appear in the body; snapshots are always *captured* in full. |

`meta=true` is for indexing cheaply — list what exists, then fetch the ones you want by id. A
consumer that wants the items anyway should skip it and use `include` to trim instead.

`include` is a comma list of `core,storage,misc,loadouts` (default all). `scope` is one of
`all` (default), `main`, `storage`, `core`, `misc`, `loadouts`. Responses are JSON; reports nest
containers, buffs, and stats.

```sh
curl "http://localhost:7878/inventory/read?player=Alice&token=TOKEN"
curl "http://localhost:7878/inventory/removeslot?player=Alice&slot=0&token=TOKEN"
curl "http://localhost:7878/inventory/removeitem?player=Alice&item=Zenith&token=TOKEN"
curl "http://localhost:7878/inventory/clear?player=Alice&scope=main&token=TOKEN"
```

### Item name catalog

`/inventory/itemnames` dumps every item id the running server knows about with its display name,
for consumers that need to resolve ids offline (useful when an extractor can't be run against the
game files). Names come from the server's loaded localization, so they match exactly what the read
and snapshot endpoints report.

```sh
curl "http://localhost:7878/inventory/itemnames?token=TOKEN" > items.json
```

```json
{ "status": "200", "version": "1.4.5.6", "count": 5455,
  "items": { "-48": "…", "1": "Iron Pickaxe", "2": "Dirt Block" } }
```

`version` is Terraria's version, and the map includes the negative net ids Terraria uses for item
variants. This is a manual, occasional tool, not a hot path: the first call builds the whole table
on the main thread (then caches it for the life of the process) and every call serializes several
thousand entries. Dump it once per server version and keep the file.

## In-game commands

Equivalent to the REST endpoints, dispatched under `/inv` (alias `/inventory`):

| Command | Permission |
|---------|------------|
| `/inv read <player> [page]` | `invmonitor.read` |
| `/inv readall [page]` | `invmonitor.read` |
| `/inv removeslot <player> <slot>` | `invmonitor.remove` |
| `/inv removeitem <player> <id\|name> [amount]` | `invmonitor.remove` |
| `/inv clear <player> [all\|main\|storage\|core\|misc\|loadouts]` | `invmonitor.clear` |
| `/inv snapshots [player] [page]` | `invmonitor.snapshots` |
| `/inv snapshot <id> [page]` | `invmonitor.snapshots` |

## Permissions

Grant nodes to TShock groups, e.g. `/group addperm admin invmonitor.read invmonitor.remove invmonitor.clear`.

- In-game: `invmonitor.read`, `invmonitor.remove`, `invmonitor.clear`, `invmonitor.snapshots`
- REST: `invmonitor.rest.read`, `invmonitor.rest.remove`, `invmonitor.rest.clear`, `invmonitor.rest.snapshots`, `invmonitor.rest.itemnames`
