# InventoryMonitor

A TShock **6.1** plugin (.NET 9) for reading and managing player inventories over the built-in
**REST API** and equivalent **in-game commands**. It reports every item across a player's entire
inventory surface — main inventory, armor/accessories, dyes, misc equips, personal storage
(piggy bank / safe / forge / void vault / trash), the three equipment loadouts — plus active
buffs/status effects and core stats, and it can remove items or clear inventories.

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
path persists automatically via TShock's character DB (see `serverSideCharacter` in reports).

Tunable in `tshock/InventoryMonitor.json`:

| Key | Default | Meaning |
|-----|---------|---------|
| `RemovalRetryCount` | 3 | Re-clear passes after the initial clear (0 = single best-effort). |
| `RemovalRetryIntervalTicks` | 20 | Ticks (~60/s) between verification passes. |
| `ReadAllMaxPlayers` | 255 | Cap on players serialized by a single `readall`. |
| `MainThreadTimeoutMs` | 3000 | How long a REST call waits for its main-thread work. |

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

`include` is a comma list of `core,storage,misc,loadouts` (default all). `scope` is one of
`all` (default), `main`, `storage`, `core`, `misc`, `loadouts`. Responses are JSON; reports nest
containers, buffs, and stats.

```sh
curl "http://localhost:7878/inventory/read?player=Alice&token=TOKEN"
curl "http://localhost:7878/inventory/removeslot?player=Alice&slot=0&token=TOKEN"
curl "http://localhost:7878/inventory/removeitem?player=Alice&item=Zenith&token=TOKEN"
curl "http://localhost:7878/inventory/clear?player=Alice&scope=main&token=TOKEN"
```

## In-game commands

Equivalent to the REST endpoints, dispatched under `/inv` (alias `/inventory`):

| Command | Permission |
|---------|------------|
| `/inv read <player> [page]` | `invmonitor.read` |
| `/inv readall [page]` | `invmonitor.read` |
| `/inv removeslot <player> <slot>` | `invmonitor.remove` |
| `/inv removeitem <player> <id\|name> [amount]` | `invmonitor.remove` |
| `/inv clear <player> [all\|main\|storage\|core\|misc\|loadouts]` | `invmonitor.clear` |

## Permissions

Grant nodes to TShock groups, e.g. `/group addperm admin invmonitor.read invmonitor.remove invmonitor.clear`.

- In-game: `invmonitor.read`, `invmonitor.remove`, `invmonitor.clear`
- REST: `invmonitor.rest.read`, `invmonitor.rest.remove`, `invmonitor.rest.clear`
