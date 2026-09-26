# XivHubPluginKit

Shared C# source for the XIV Hub Dalamud plugins. There is no NuGet package and no DLL to bundle: a
plugin links the kit files it needs into its own project, and they compile as part of that plugin.
Each plugin therefore carries its own copy of every static, and a kit change reaches a plugin the
next time that plugin is built.

## Using it in a plugin

Clone this repo next to the plugin's repo, so that from a project at `<plugin repo>/<Project>/` the
kit is at `..\..\XivHubPluginKit`. Then link files into the csproj, one line per file:

```xml
<ItemGroup>
  <Compile Include="..\..\XivHubPluginKit\KitServices.cs" Link="Kit\KitServices.cs" />
  <Compile Include="..\..\XivHubPluginKit\Inventory\ItemSheet.cs" Link="Kit\Inventory\ItemSheet.cs" />
  <Compile Include="..\..\XivHubPluginKit\Inventory\VendorPrice.cs" Link="Kit\Inventory\VendorPrice.cs" />
</ItemGroup>
```

Linking a file does not bring in the files it uses. The dependency table under "What is in it" lists
them; the compiler names anything missed.

Kit files read Dalamud services through the static `KitServices` rather than any one plugin's
service class, and many use [ECommons](https://github.com/NightmareXIV/ECommons). Initialise both at
the top of the plugin constructor, before any kit code runs:

```csharp
ECommonsMain.Init(PluginInterface, this);
KitServices.Init(DataManager, Log, ChatGui, "[MyPlugin]");
```

`KitServices.Init` stores the data manager, the log, the chat and a prefix for chat messages the kit
prints. Forgetting it shows up as a `NullReferenceException` the first time a kit file logs, often
from inside a `catch` block.

## What is in it

| Area | For | Files | Needs |
| --- | --- | --- | --- |
| Services | Dalamud services for kit code | `KitServices.cs` | nothing |
| Plugin presence | "is plugin X loaded", cheap enough to ask every frame | `PluginPresence.cs` | ECommons |
| Dev log | streaming logs, state snapshots, JSON records and file dumps to a LAN server during development | `DevTelemetry.cs`, `devlog_server.py` | nothing |
| Game | mount, dismount, sprint, jump, flight unlock, line of sight, a stuck screenshot flag | `Game/*.cs` | ECommons for `LineOfSight`, `MountHelper`, `SprintHelper`; `KitServices` for `FlightHelper`, `ScreenshotLatch` |
| Inventory | item sheet lookups, container scans, main-bag counts, NPC price floors, frame-spaced item moves | `Inventory/*.cs` | `KitServices`, except `SlotView` and `MoveOp` |
| Crafting | creating and importing Artisan crafting lists over IPC | `Crafting/ArtisanBridge.cs` | ECommons, `KitServices`, `PluginPresence` |
| Shop | buying from the NPC gil shop the player stands next to, then leaving the conversation | `Shop/*.cs` | ECommons, `KitServices`, `Inventory/` bag files |
| Retainer | the summoning-bell UI walk, the native retrieve command that moves a stack out of a retainer's inventory, a live memory read of an open retainer's listings, and AutoRetainer suppression | `Retainer/*.cs` | ECommons; `AutoRetainerSuppress` and `RetainerRetrieve` also need `KitServices`, and `RetainerRetrieve` needs `Inventory/InventoryScan`, `Inventory/SlotView` and `Inventory/ItemSheet` |
| Board | searching the market board for one item and reading its listings; live Compare Prices lookups from a retainer's sell window | `Board/*.cs` | ECommons, `KitServices`, `DevTelemetry`, `Inventory/` bag files; `MarketBoardListener` needs `IMarketBoard` and `IGameInteropProvider` |
| Pinch | repricing retainer listings from live Compare Prices lookups | `Pinch/*.cs` | ECommons, `KitServices`, `Inventory/ItemSheet`, `Inventory/VendorPrice`, `Shop/TalkSkipper`, `Retainer/`, `Board/` listener and probe; `PinchModels`, `PinchDecision`, `PinchPlanning` are BCL only |
| UI | the shared XIV Hub ImGui theme, its settings editor, and table, tab and text helpers | `UI/*.cs` | nothing; see `UI/THEME.md` |

## PluginPresence

`DalamudReflector.TryGetDalamudPlugin` with `ignoreCache` walks Dalamud's whole installed-plugin
list through reflection on every call, which is too slow for a per-frame check.
`PluginPresence.IsInstalled(internalName)` caches each answer for 2 seconds, still short enough to
notice a plugin loaded or unloaded mid-session.

```csharp
public static bool NavmeshInstalled => PluginPresence.IsInstalled("vnavmesh");
```

## Dev log

`DevTelemetry` buffers log lines, snapshots and structured records on the framework thread and posts
them from a background timer to `devlog_server.py`. It does nothing unless its enabled toggle is on
and a URL is set, so it can ship in Release builds.

```csharp
Telemetry = new DevTelemetry("MyPlugin", () => C.DevLog, () => C.DevLogUrl);

Telemetry.Log("did a thing");
Telemetry.Snapshot(() => $"state={state} pos={pos}");   // every frame; builds at most once a second
Telemetry.Record("callback", new Dictionary<string, object?> { ["addon"] = "ItemSearch", ["round"] = 3 });
var path = await Telemetry.UploadFileAsync("capture", "json", bytes);

Telemetry.Dispose();
```

`Snapshot` queues a line only when it differs from the last one, so an idle window adds nothing to
the log. `Log` splits a message on `\n` and prefixes each part.

`Record` writes one JSON object per call, for traces you filter with `jq` rather than read top to
bottom. Every record starts with `ts`, `src`, `session`, `seq` and `kind`, followed by your fields
in dictionary order. A field named after one of those five is written with a trailing `_`.
`Record` never throws: a value that cannot be serialised becomes the string
`"!{ExceptionType}: {message}"`, NaN and the infinities become strings, and a record over 1 MiB is
replaced by an `oversize` stub with the same `seq`. That makes it safe inside a hook detour.

`UploadFileAsync` sends one whole artefact (an addon dump, a capture) to the server's `/file`
endpoint. It needs only a URL, not the toggle, because it runs from an explicit user action. The
server rejects bodies over 8 MB, so check the size before calling.

A batch the client retries lands in the file once. Each batch carries
`X-Devlog-Session` and `X-Devlog-Offset`, and the server writes only the part of a retried batch it
does not already hold. Log lines and records travel on separate channels, so a failing one never
holds the other back. While the server is unreachable, records queue up to 16 MiB and the oldest are
dropped first. `Dispose` spends at most 3 seconds delivering what is left, so a dead server delays
plugin unload by no more than that.

### TeeLog

`TeeLog` wraps a plugin's `IPluginLog` and mirrors every line (and rendered Serilog template) to
`DevTelemetry`, with no call-site changes. Hand a kit class that takes an `IPluginLog` this tee, not
the plugin's own `IPluginLog`, if its log lines are meant to reach the dev-log server; kit code that
logs through `KitServices.Log` instead of an injected `IPluginLog` bypasses the tee, because
`KitServices.Init` is wired to the plain log.

```csharp
var devLog = new TeeLog(Log, Telemetry);
```

### Running the server

```bash
python3 devlog_server.py
# listens on 0.0.0.0:9999 and writes under ~/.cache/zhyra-devlog/
```

Point the plugin's dev log URL at `http://<server LAN address>:9999/log`. The server echoes every
line to stdout and appends it to `live.log`; `tail -f ~/.cache/zhyra-devlog/live.log` follows it.
Every line is prefixed with the sender's hostname (by reverse DNS, else its address), so several
machines can share one server. Add `?client=<name>` to the URL to set the label yourself:

```
192.168.1.20 12:00:00.000 [Gardener] tended bed 1
laptop       12:00:01.000 [Gardener] tended bed 2
```

| Endpoint | Does |
| --- | --- |
| `POST /log` | appends plain lines to `live.log` |
| `POST /records` | appends JSON lines to `records.jsonl`, adding `client` and `rx` (receive time); a line that is not a JSON object is kept as `{"kind":"invalid", "raw": ...}` |
| `POST /file?plugin=&name=&ext=` | stores the body under `dumps/` as `<time>-<client>-<plugin>-<name>.<ext>` and answers with the path |
| `GET /log`, `GET /records` | the tail of either file; `?n=200` for 200 lines, `?n=0` for all |
| `GET /setup/<name>` | serves `setup/<name>.json`, for a plugin to load a capture setup |
| `GET /health` | liveness check |

`live.log` and `records.jsonl` each rotate to a `.1` file past 25 MB. `PORT`, `LOGFILE`,
`RECORDSFILE`, `DUMPDIR`, `SETUPDIR`, `MAX_BYTES` and `MAX_DUMP_BYTES` override the defaults. To
read records in order, pass the rotated file first; jq reports it missing until the first rotation
and still reads the second file:

```bash
cd ~/.cache/zhyra-devlog
jq -c 'select(.src=="UiCapture" and .kind=="callback")' records.jsonl.1 records.jsonl
```

The server has no authentication and speaks plain HTTP. Run it on a local network only.

## Game

| Call | Does |
| --- | --- |
| `LineOfSight.Clear(target)` | the game's own collision raycast between the player and a target; distance alone does not say whether an action can land |
| `FlightHelper.FlyingUnlocked(territoryId)` | whether the zone's aether currents are complete; unlike `Control.CanFly` it works before mounting |
| `MountHelper.Mount()` | Mount Roulette (general action 9) |
| `MountHelper.Dismount()` | one press of Dismount (general action 23); in the air this only starts the descent |
| `MountHelper.Ground()` | call every frame until it returns true: lands, dismounts and waits until the character is on foot |
| `MountHelper.Jump()` | general action 2, for getting unstuck on geometry |
| `SprintHelper.TrySprint()` | Sprint (general action 4) when it is ready and the player is on foot, at most once per 2 seconds |
| `ScreenshotLatch.TryRelease()` | clears the game's `ScreenShotRequested` flag when a capture never completed, through the game's own completion routine; call `ScreenshotLatch.Resolve(sigScanner)` once at startup first |

The mount and sprint helpers check `GetActionStatus` before pressing, so a press that the game
would refuse is skipped rather than counted.

## Inventory

`ItemSheet` wraps the Lumina `Item` sheet with cached lookups: `Name`, `StackSize`, `IsMarketable`,
`Ilvl`, `Rarity`, `EquipSlotCategory`. `InventoryScan.ScanContainer(type)` yields a `SlotView` per
occupied slot of any container, with quantity, HQ and spiritbond; a `SlotView` holds no game
pointers and can be kept across frames. `MainBags` reads gil, free slots and per-quality item counts
across `Inventory1` to `Inventory4`, the only containers a purchase lands in.

`VendorPrice` answers what a unit is worth away from the market board: `Buyback` (what an NPC pays),
`Replacement` (what a gil shop charges), and `Floor`, the lowest listing price whose after-tax
proceeds still match the better of the two. `ProfitFloor(itemId, margin)` adds a margin on top.

`MoveQueue` moves items between the player's own containers one per frame through
`InventoryManager.MoveItemSlot`, re-reading free slots every tick. Enqueue `MoveOp`s, call `Tick()`
from the framework update, and `Abort()` to stop.

## Crafting

`ArtisanBridge` creates Artisan crafting lists through the IPC of the XIV Hub Artisan fork. Upstream
Artisan has none of these endpoints, and the bridge treats that as "unavailable", so a caller can
fall back to copying Artisan JSON to the clipboard. The fork advertises what it supports through
`Artisan.ApiVersion`:

| Version | Adds |
| --- | --- |
| 1 | `ArtisanBridge.CreateList(name, items, subcrafts: false)`: the final items only |
| 2 | `CreateList(..., subcrafts: true)`: also every intermediate craft |
| 3 | `ArtisanBridge.ImportList(json)`: a full Artisan export, keeping `SkipIfEnough`, `SkipLiteral` and each row's Quick Synthesis choice |

Both calls return the new list id, `-1` when Artisan refused the input, or `null` when the IPC is
unavailable or threw. `Available`, `SupportsSubcrafts` and `SupportsImport` read a version cached for
2 seconds, so a UI can check them every frame. Each kind of failure is logged once per plugin load.
Call from the framework thread only.

## Shop

`ShopBuyer.RunStopAsync(visit, purchases, gilReserve, ct)` buys a list of items from the NPC the
player is standing next to (within 6 yalms) and returns one `BuyResult` per item: requested, bought,
the unit price the shop charged, and a failure reason. `FindVendor` and `IsAtVendorAsync` let a UI
check range first. Timings come from a `Func<ShopBuyerSettings>` read on every use, and an optional
callback fires once per item that bought anything. `AbortAll()` drops queued steps.

`ConversationUnwinder.Start(ConversationUnwinder.ShopSteps)` closes whatever conversation windows
are still open after a stop, one per frame, within 8 seconds. `TalkSkipper.Register()` clicks
through `Talk` bubbles until `Unregister()`. `Humanizer.PauseAsync` waits a random interval between
whole actions.

The buyer follows these rules, each learned from a failure in game:

| Rule | Why |
| --- | --- |
| Buy a stackable item in one click, a non-stacking one (every furnishing) one unit per click | a non-stacking item has no quantity field; asking for more buys nothing and jams the shop |
| Leave a menu through its last entry ("Nothing"), never the cancel callback or `Close(true)` | cancelling hides the window without ending the event, and the player stays stuck with the NPC until a client restart |
| Close `Shop` fully and talk to the NPC again for each menu category | the NPC does not reliably hand its menu back, and the previous category's stock stays on screen |
| Count a purchase only once the bag count stops changing, up to 2.5 seconds | reading it the same frame made a good buy look failed, and it was bought twice |
| Check gil and bag space before every click | both can change while the plan is on screen; buying stops at `gilReserve` |

Menus drawn as `SelectString` and `SelectIconString` are both handled. `ShopPurchase.Menu` must
match the English entry label exactly, so shop buying works on an English client only.

## Retainer

| File | Holds |
| --- | --- |
| `RetainerWalk.cs` | the summoning-bell UI walk shared by every session that drives a retainer (`RetainerList` → row → "Sell Items" → `RetainerSellList`, or → "Entrust or withdraw items" (`ClickEntrustOrWithdraw`, matched by Addon sheet row) → the retainer inventory, and the teardown back out, including `CloseRetainerInventory`, which leaves the inventory window for the retainer menu), `DescribeSelectString` for failure messages, the roster read, and the sell-list row names |
| `RetainerRetrieve.cs` | the native retainer item command: whole-stack retrieve from an open retainer inventory, refused unless the slot still holds the item and quantity the caller scanned; the live page scan, the pages-loaded check, the inventory-ready check |
| `RetainerMarket.cs` | `RetainerMarket.ReadLive`, a live memory read of the currently-open retainer's market listings |
| `AutoRetainerSuppress.cs` | toggles AutoRetainer's suppression IPC around a session that drives a retainer by hand |

Every `RetainerWalk` step, every `RetainerRetrieve` call and `RetainerMarket.ReadLive` must run on the
framework thread. `RetainerWalk` and `AutoRetainerSuppress` need ECommons (`Svc`); `AutoRetainerSuppress`
also needs `KitServices.Init` for `KitServices.Log`. `RetainerRetrieve` needs ECommons
(`Svc.SigScanner`, `Svc.Framework`) and `KitServices.Init`, and scans pages through
`Inventory/InventoryScan`, which builds `Inventory/SlotView` rows named through `Inventory/ItemSheet`,
so link all three too.

```xml
<Compile Include="..\..\XivHubPluginKit\Retainer\RetainerWalk.cs" Link="Kit\Retainer\RetainerWalk.cs" />
<Compile Include="..\..\XivHubPluginKit\Retainer\RetainerMarket.cs" Link="Kit\Retainer\RetainerMarket.cs" />
<Compile Include="..\..\XivHubPluginKit\Retainer\AutoRetainerSuppress.cs" Link="Kit\Retainer\AutoRetainerSuppress.cs" />
<Compile Include="..\..\XivHubPluginKit\Retainer\RetainerRetrieve.cs" Link="Kit\Retainer\RetainerRetrieve.cs" />
```

## Board

`BoardSearch.Start(itemId)` types an item's name into the market board search, opens its result by
item id and waits for the listings to settle. `Outcome` then says `Opened`, `NoListings` or `Failed`
with a reason; `ItemProblem` marks a failure that belongs to the item (no name to search, not in the
results) rather than to the board. `Guard()` says why a search cannot start: the board is closed, or
its filter, history or confirm window is open. `Stop(reason)` ends a search.

The only callbacks it fires are `ItemSearch [7, -1, 0]`, `ItemSearch [9, ...]`, `ItemSearch [5, i]`
and `ItemSearchResult [-1]`. It never picks a listing or answers `SelectYesno`, so a search cannot
start a purchase.

`BoardReader` reads the board's windows and structs without firing anything: `Read()` returns a
`BoardSnapshot` of the listings, `ReadSearchPage()` the result ids, and `SelectedIndex`,
`LastPurchasedListingId`, `YesnoText` and `HeldForBuy` cover the purchase side. `ListingsGate`
decides when a read is the finished page for this request rather than a page still arriving or the
previous item's slots.

```csharp
// after ECommonsMain.Init and KitServices.Init
BoardSearch = new BoardSearch(
    Framework,               // the plugin's IFramework
    Log,
    () => C.UiStepDelayMs,   // minimum gap between clicks
    id => ItemName(id),      // plain item name as the board matches it, "" when unknown
    () => Telemetry);        // DevTelemetry or null; each outcome is written as a boardsearch record

// Dispose():
BoardSearch.Dispose();
```

`ItemSheet.Name` is not a good `nameOf`: its text keeps payload markup. Call `Start`, `Stop` and
every `BoardReader` method from the framework thread (a window draw or a framework update). Each
plugin has its own `BoardSearch`, but the board window is shared, so two plugins searching at once
fight over it.

The live-price lookup reads the board from the packets the game receives rather than from its
windows:

| File | Holds | Needs |
| --- | --- | --- |
| `BoardObservation.cs` | `LivePrice` (one item's cheapest competitor, last sale, own cheapest, and whether the offerings side answered) and `BoardObservation`, the full ladder and history of one lookup | nothing (BCL only) |
| `MarketBoardListener.cs` | subscribes to `IMarketBoard` offerings and history and hooks the request-start packet; `BeginRequest`, `TryGetCheapestCompetitor`, `TryGetHistory`, `RequestStatus`, `Observation`, `EndRequest` | `IMarketBoard` and `IGameInteropProvider` passed in |
| `LivePriceProbe.cs` | drives Compare Prices on an open `RetainerSell` window, retries a silent board, and caches each `(item, hq)` answer for the session | ECommons, `MarketBoardListener`, `BoardObservation` |

`MarketBoardListener` awaits one request at a time: a new `BeginRequest` replaces the last, so a
plugin runs one lookup session at a time, and `EndRequest` ends only the request its caller began.
The request-start packet is what tells an empty board (status 0, no listings) from a reply that
never came; the detour records it and always calls the original handler, so two plugins hooking it
at once both see every packet. Create one listener per plugin and dispose it on unload.

Each `LivePriceProbe` takes an EzThrottler name and a log tag from its caller; give each caller its
own throttle name, since ECommons throttle names are global inside a plugin. `onResolved(itemId,
hq)` fires once per finished lookup, before the answer is cached, including a lookup the
per-session cap skipped; call `listener.Observation(itemId, hq)` there to get the ladder, which is
null when the listener holds nothing for that item.

```xml
<Compile Include="..\..\XivHubPluginKit\Board\BoardObservation.cs" Link="Kit\Board\BoardObservation.cs" />
<Compile Include="..\..\XivHubPluginKit\Board\MarketBoardListener.cs" Link="Kit\Board\MarketBoardListener.cs" />
<Compile Include="..\..\XivHubPluginKit\Board\LivePriceProbe.cs" Link="Kit\Board\LivePriceProbe.cs" />
```

```csharp
Listener = new MarketBoardListener(MarketBoard, GameInterop, log);   // IMarketBoard, IGameInteropProvider
var probe = new LivePriceProbe(Listener, () => ownRetainerCids, log,
    "MyPluginMBThrottle", "MyPlugin", onResolved: null);

// every framework tick while a RetainerSell window is open:
if (probe.Step(sell, itemId, hq, name, maxPerSession: 0, delayMs: C.MarketBoardDelayMs, out LivePrice price)) { ... }

// Dispose():
Listener.Dispose();
```

## Pinch

Repricing a retainer's listings: walk its sell list, open each row's Adjust Price window, and write
the price a live Compare Prices lookup decides.

| File | Holds | Needs |
| --- | --- | --- |
| `PinchEngine.cs` | the engine: `RunOpenAsync` (the retainer whose sell list is open), `RunAllAsync` (every retainer from `RetainerList`), `RunForOpenRetainerAsync` (the retainer AutoRetainer's post-process window hands over), the row steps, the early-exit budget, `AllLive`, `LiveRowNeedsVisit`, and the `PlanFn` delegate | ECommons, `KitServices`, everything below |
| `PinchModels.cs` | `PinchSettings`, `RetainerPlan`, `PinchRetainer`, `PinchWrite`, `PriceDecision`, `DryRunVisit`, `PinchRetainerResult`, `PinchSessionResult` | nothing (BCL only) |
| `PinchDecision.cs` | `PinchDecision.Decide`: the price to write from one board answer, the current price and the floor | `Board/BoardObservation` |
| `PinchPlanning.cs` | `RowsToVisit` (which sell-list rows a plan claims) and `NeedsVisit` (whether a cached board answer still calls for a write) | `Board/BoardObservation` |

```xml
<Compile Include="..\..\XivHubPluginKit\KitServices.cs" Link="Kit\KitServices.cs" />
<Compile Include="..\..\XivHubPluginKit\Inventory\ItemSheet.cs" Link="Kit\Inventory\ItemSheet.cs" />
<Compile Include="..\..\XivHubPluginKit\Inventory\VendorPrice.cs" Link="Kit\Inventory\VendorPrice.cs" />
<Compile Include="..\..\XivHubPluginKit\Shop\TalkSkipper.cs" Link="Kit\Shop\TalkSkipper.cs" />
<Compile Include="..\..\XivHubPluginKit\Retainer\RetainerWalk.cs" Link="Kit\Retainer\RetainerWalk.cs" />
<Compile Include="..\..\XivHubPluginKit\Retainer\RetainerMarket.cs" Link="Kit\Retainer\RetainerMarket.cs" />
<Compile Include="..\..\XivHubPluginKit\Retainer\AutoRetainerSuppress.cs" Link="Kit\Retainer\AutoRetainerSuppress.cs" />
<Compile Include="..\..\XivHubPluginKit\Board\BoardObservation.cs" Link="Kit\Board\BoardObservation.cs" />
<Compile Include="..\..\XivHubPluginKit\Board\MarketBoardListener.cs" Link="Kit\Board\MarketBoardListener.cs" />
<Compile Include="..\..\XivHubPluginKit\Board\LivePriceProbe.cs" Link="Kit\Board\LivePriceProbe.cs" />
<Compile Include="..\..\XivHubPluginKit\Pinch\PinchModels.cs" Link="Kit\Pinch\PinchModels.cs" />
<Compile Include="..\..\XivHubPluginKit\Pinch\PinchDecision.cs" Link="Kit\Pinch\PinchDecision.cs" />
<Compile Include="..\..\XivHubPluginKit\Pinch\PinchPlanning.cs" Link="Kit\Pinch\PinchPlanning.cs" />
<Compile Include="..\..\XivHubPluginKit\Pinch\PinchEngine.cs" Link="Kit\Pinch\PinchEngine.cs" />
```

```csharp
// after ECommonsMain.Init and KitServices.Init
Listener = new MarketBoardListener(MarketBoard, GameInterop, log);
Engine = new PinchEngine(
    Listener,
    log,                                  // the plugin's TeeLog when it has a dev log
    "MyPlugin",                           // EzThrottler names MyPluginGenericThrottle, MyPluginMBThrottle
    settings: () => new PinchSettings(C.PerItemDelayMs, C.MarketBoardDelayMs,
                                      C.SkipIfNoCompetitor, C.AnnounceEachReprice),
    profitFloor: id => VendorPrice.Floor(id),
    extraOwnCids: null,                   // retainers counted as ours beyond the session roster
    onBoardResolved: null);               // (itemId, hq) once per finished board lookup

// /mycommand with a sell list open; a null plan delegate prices every row live
var result = await Engine.RunOpenAsync(plan: null, dryRun: false, ct);
if (result is { } r) Chat.Print($"{r.Name}: {r.Reprices} reprice(s) ({r.Rows} rows)");

// Auto button while RetainerList is open; null means no session ran
if (await Engine.RunAllAsync(plan: null, ct) is { } s) Chat.Print($"{s.Retainers} retainers, {s.Reprices} reprices");

// Dispose():
Engine.Dispose();
Listener.Dispose();
```

Rules:

- Pass the plugin's tee as the log. The engine and its probe log through the `IPluginLog` they are
  given, never `KitServices.Log`, so a plain log leaves every Pinch line out of the dev log.
- No `PlanFn` means every row is a live lookup (`AllLive`, uncapped). A `PlanFn` fills
  `RetainerPlan.Live` for rows the board decides; it may call `AllLive` and adjust the result.
- A null plan result skips the retainer: `RunOpenAsync` returns zero counts, `RunAllAsync` backs out
  to the retainer list and moves on uncounted, `RunForOpenRetainerAsync` closes the sell list.
- `_rowsRemaining` counts writes still expected (`LiveRows`), not rows visited. A finished lookup
  that changed nothing must not spend it, or a later row that needs a write is skipped: the sell
  list is drawn in the game's category order, not ours.
- `RunForOpenRetainerAsync` must not suppress AutoRetainer: suppressing mid-cycle switches multi mode
  off under the rotation. The caller owns any per-retainer interval.
- Every entry point holds one run flag from start to finish, and `IsBusy` reads it. A second run
  while one is going returns null; the task queue alone goes idle between batches, and a run started
  in that gap would cancel the first and release AutoRetainer's suppression under it.
- One board lookup at a time per plugin: the engine's probe shares the plugin's
  `MarketBoardListener`, which awaits one request at a time.
- Every UI step runs on the framework thread through the engine's `TaskManager`; the entry points
  read addon memory only through `Svc.Framework.RunOnFrameworkThread`. `CanPinchNow` is framework
  thread only.
- A dry run opens nothing and returns `PinchRetainerResult.DryRun`; the caller prints its own
  dry-run lines from it.

## UI

Every XIV Hub plugin draws in one theme with a palette taken from xivhub.net. `UI/THEME.md` has the
wiring, the colour rules and how to extend the theme. In short: link `HubColors`, `HubStyle`,
`HubThemeConfig` and `HubThemeEditor`, then

```csharp
_theme = new HubThemeConfigService(PluginInterface.GetPluginConfigDirectory(), (msg, ex) => Log.Warning(ex, msg));
HubStyle.Init(_theme);

// around the whole WindowSystem.Draw()
HubStyle.Push();
try { _windowSystem.Draw(); } finally { HubStyle.Pop(); }

// in the config window
HubThemeEditor.Draw(_theme);
```

The theme is stored once for all plugins, in `XivHub/ui-theme.json` beside the plugin config
directories. `HubText`, `HubTable` and `HubTabs` replace raw ImGui calls that get wrapping, column
sizing or tab overflow wrong; `HubWindow.FitHeight` caps a window at its content height.
`tools/theme-lint.sh <plugin dir>` finds the raw calls these replace and exits 1 if there are any.

## Tests

```bash
dotnet test XivHubPluginKit.Tests/XivHubPluginKit.Tests.csproj   # DevTelemetry, ListingsGate, PinchDecision, PinchPlanning (.NET 10 SDK)
python3 test_devlog_server.py                                     # retry dedupe on /log
python3 test_devlog_records.py                                    # /records
```

The Python tests start their own server on ports 9901 and 9902. Everything else in the kit touches
the game and is tested by building and running a plugin that uses it.

## License

AGPL-3.0; see `LICENSE`.
