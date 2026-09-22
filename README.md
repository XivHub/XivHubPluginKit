# XivHubPluginKit

Small shared helpers for my Dalamud plugins. Reused by **linked source** (no NuGet, no DLL bundling):
each plugin compiles the file directly.

## DevTelemetry — live log/telemetry to a local server (dev only)

`DevTelemetry.cs` buffers log lines + periodic state snapshots from a plugin and POSTs them to the
mini server (`devlog_server.py`) on the local network, so plugin behaviour can be read in real time.
It is **inert unless enabled and given a URL**, so it's safe to ship in Release builds (dormant for
normal users).

### Add to a plugin

1. Link the source in the plugin `.csproj` (sibling repos under `~/dev/`):

   ```xml
   <ItemGroup>
     <Compile Include="..\..\XivHubPluginKit\DevTelemetry.cs" Link="Dev\DevTelemetry.cs" />
   </ItemGroup>
   ```

2. Add two config fields (`bool DevLog`, `string DevLogUrl`) and a Developer section in the config UI.

3. Wire it up (in your `Plugin`):

   ```csharp
   using XivHubPluginKit;
   public static DevTelemetry Telemetry { get; private set; } = null!;

   // ctor, after config load:
   Telemetry = new DevTelemetry("MyPlugin", () => C.DevLog, () => C.DevLogUrl);
   // Dispose():
   Telemetry.Dispose();
   ```

4. Emit events / snapshots:

   ```csharp
   Telemetry.Log("did a thing");
   // each frame (builds the string on the framework thread, self-throttled):
   Telemetry.Snapshot(() => $"state={state} pos={pos} ...");
   ```

### Run the server (on this box)

```bash
python3 ~/dev/XivHubPluginKit/devlog_server.py
# listens on 0.0.0.0:9999, appends to ~/.cache/zhyra-devlog/live.log, echoes to stdout
```

Point the plugin's dev-log URL at `http://<this-box-LAN-ip>:9999/log`. Read the stream with
`tail -f ~/.cache/zhyra-devlog/live.log`.

A batch is appended before the response goes out, so a client that scores a stored POST as failed
re-sends it. Each batch therefore carries `X-Devlog-Session` (one id per plugin run) and
`X-Devlog-Offset` (where the batch starts in that run's line stream), and the server writes only
the part past what it already holds. A client that sends neither is written verbatim.
`python3 test_devlog_server.py` covers the retry cases.

### Several machines, one log

Every line is prefixed with whoever sent it, so two people on the same LAN can point at the same
server and stay apart:

```
192.168.88.248 12:00:00.000 [Gardener] tended bed 1
gf-pc          12:00:01.000 [Gardener] tended bed 2
```

The name is the sender's hostname by reverse DNS, cached per address, falling back to the address
itself. Add `?client=<name>` to the URL to label a machine explicitly, which also names its uploaded
artefacts (`20260905-191310-gf-pc-Gardener-dump.txt`). Grep one machine out with
`grep '^gf-pc ' live.log`.

### Whole artefacts: `POST /file`

A stream of log lines is the wrong shape for a one-off capture — an addon's `AtkValues`, a memory
dump, a struct decode. Those interleave with everything else logging and have to be sliced back out
by hand. `POST /file` stores one instead:

```bash
curl -X POST --data-binary @dump.txt \
  "http://<box>:9999/file?plugin=Gardener&name=selectstring&ext=txt"
# -> /home/edgar/.cache/zhyra-devlog/dumps/20260905-123312-Gardener-selectstring.txt
```

The response body is the path it landed at. Pass `plugin` so artefacts are attributable the way log
lines already are — `DevTelemetry` tags every line with its source, and several plugins share this
server. `plugin`, `name` and `ext` are reduced to `[A-Za-z0-9._-]`, so a caller cannot escape the
dump directory; the stamped prefix keeps repeat captures of the same thing side by side instead of
overwriting. Capped at `MAX_DUMP_BYTES` (8 MB), overridable, as is `DUMPDIR`.

Reading an addon through Dalamud's inspector means retyping from screenshots, which is why this
exists: a plugin builds the report and ships it here, and it is a file on the dev box.

> Local-network, no auth, plain HTTP. Dev only — do not expose to the internet.

### From a plugin

`DevTelemetry.UploadFileAsync` posts to `/file` without going through the curl round trip:

```csharp
var path = await Telemetry.UploadFileAsync("capture", "json", bytes);
```

- The URL is derived from the configured `/log` URL's origin — same host and port, `/file` instead
  of `/log` — and its query string carries over, so `client=` still attributes the upload the same
  way it attributes log lines.
- It needs only a devlog URL, not the `enabled` toggle: unlike `Log`/`Snapshot`, it is only ever
  called from an explicit user action, so there is nothing to gate.
- The server caps bodies at `MAX_DUMP_BYTES` (8 MB by default), so callers should check `bytes.Length`
  before calling rather than rely on the resulting `HttpRequestException`.

## PluginPresence — cached "is that plugin loaded?"

`DalamudReflector.TryGetDalamudPlugin` with `ignoreCache` walks Dalamud's entire installed-plugin
list through reflection on every call, so an `Installed` property built on it must never sit in a
per-frame path. `PluginPresence.IsInstalled(internalName)` memoizes the answer for 2s, which is
still fast enough to notice a plugin the user loads or unloads mid-session.

```csharp
public static bool Installed => PluginPresence.IsInstalled("vnavmesh");
```

## Game/ — automation helpers

Needs ECommons initialised in the consuming plugin. `FlightHelper` also needs
`KitServices.Init(...)` for the sheet read; the rest are standalone.

| File | What it does |
| --- | --- |
| `Game/LineOfSight.cs` | `Clear(target)` — the game's own collision raycast (`BGCollisionModule->RaycastMaterialFilter`) between player and target. Ranged combat and interact checks need this; distance alone is not enough. |
| `Game/FlightHelper.cs` | `FlyingUnlocked(territoryId)` via the territory's completed `AetherCurrentCompFlgSet`. Unlike `Control.CanFly` it does **not** require being mounted, so it can decide to fly *before* mounting. |
| `Game/MountHelper.cs` | `Mount()` / `Dismount()` (both General Action 9, Mount Roulette) and `Jump()` (General Action 2, useful to unstick on geometry). |
| `Game/SprintHelper.cs` | `TrySprint()` — General Action 4, gated on `GetActionStatus == 0`, self-throttled to 2s. |
| `Game/ScreenshotLatch.cs` | `TryRelease()` — clears a `ScreenShot.ScreenShotRequested` the game itself will never drain, through the game's own completion thunk rather than writing the flag directly. Needs `Resolve(ISigScanner)` once at startup; unlike the ECommons-dependent helpers above, it needs nothing else. |

```xml
<Compile Include="..\..\XivHubPluginKit\PluginPresence.cs" Link="Kit\PluginPresence.cs" />
<Compile Include="..\..\XivHubPluginKit\Game\LineOfSight.cs" Link="Kit\Game\LineOfSight.cs" />
```

## Shop/ — buying from an NPC gil shop

Buys a list of items from the vendor the player is standing next to, then leaves the
conversation cleanly. Moved from FFMarketConnector's restock buy leg, which is tested in game.
Needs ECommons initialised and `KitServices.Init(...)` called before first use, since the bag
reads go through `InventoryScan` and `ItemSheet`.

| File | What it does |
| --- | --- |
| `Shop/ShopBuyer.cs` | `RunStopAsync(ShopVisit, purchases, gilReserve, ct)` buys each `ShopPurchase` at the nearest matching NPC within 6 yalms and returns one `BuyResult` per item (requested, bought, observed unit price, failure reason). `FindVendor(npcIds)` (framework thread) and `IsAtVendorAsync(visit)` gate a UI on being in range. Timing comes from a `Func<ShopBuyerSettings>`, read on every use; the optional `onBought(visit, purchase, qty, unitPrice)` fires once per item that bought anything. `AbortAll()` drops queued steps. |
| `Shop/ConversationUnwinder.cs` | `Start(steps, log)` unwinds whatever conversation addons are still up after a Stop, one per frame, within an 8 s budget. `ShopSteps` covers a vendor purchase: `SelectYesno`, `Shop`, `ShopExchangeCurrency`, `SelectIconString`, `SelectString`. `LeaveSelectString` and `LeaveSelectIconString` are the menu exits. |
| `Shop/TalkSkipper.cs` | `Register()` / `Unregister()` clicks through every `Talk` bubble while registered. |
| `Shop/Humanizer.cs` | `PauseAsync(minMs, maxMs, ct, repeat)` waits a random interval between whole actions; `repeat` halves the range. Never throws on cancellation. |
| `Inventory/MainBags.cs` | Gil, free slots and per-item counts across `Inventory1`–`Inventory4`, the only containers a purchase lands in. Framework thread. |

Rules the buyer encodes, each from a failure seen in game:

- **Stack size decides the click size.** A stackable item takes the whole quantity in one click.
  An item with stack size 1 (every furnishing) has no quantity field and is bought one unit per
  click; asking for more buys nothing and leaves the shop unable to complete anything after it.
- **Menus can be `SelectIconString`.** The estate servant draws its menu as `SelectIconString`,
  most vendors as `SelectString`; both are read as "the menu". `ShopPurchase.Menu` is the English
  entry label and must match what the game draws exactly.
- **Leave a menu through its last entry ("Nothing"), never the cancel callback or `Close(true)`.**
  Cancelling hides the window without ending the event, and the player is left stuck with the NPC
  selected until a client restart.
- **Close `Shop` until it is gone.** The cancel callback is fired up to three times, then the
  `Shop` agent is hidden. Each menu category is its own conversation: close fully, talk again.
- **Confirm by settled bag count.** A purchase counts only once the main-bag count has stopped
  moving (up to 2.5 s); reading it the same frame made a good buy look failed and got it bought
  twice.
- **Check gil and bags before every click.** Buying stops at `gilReserve` and when the main bags
  cannot take another unit, since both can change while the plan is on screen.
- **English client labels.** Menu entries are matched by their English text.

```xml
<Compile Include="..\..\XivHubPluginKit\KitServices.cs" Link="Kit\KitServices.cs" />
<Compile Include="..\..\XivHubPluginKit\Inventory\ItemSheet.cs" Link="Kit\Inventory\ItemSheet.cs" />
<Compile Include="..\..\XivHubPluginKit\Inventory\SlotView.cs" Link="Kit\Inventory\SlotView.cs" />
<Compile Include="..\..\XivHubPluginKit\Inventory\InventoryScan.cs" Link="Kit\Inventory\InventoryScan.cs" />
<Compile Include="..\..\XivHubPluginKit\Inventory\MainBags.cs" Link="Kit\Inventory\MainBags.cs" />
<Compile Include="..\..\XivHubPluginKit\Shop\ShopBuyer.cs" Link="Kit\Shop\ShopBuyer.cs" />
<Compile Include="..\..\XivHubPluginKit\Shop\TalkSkipper.cs" Link="Kit\Shop\TalkSkipper.cs" />
<Compile Include="..\..\XivHubPluginKit\Shop\Humanizer.cs" Link="Kit\Shop\Humanizer.cs" />
<Compile Include="..\..\XivHubPluginKit\Shop\ConversationUnwinder.cs" Link="Kit\Shop\ConversationUnwinder.cs" />
```

```csharp
// Plugin ctor, after ECommonsMain.Init:
KitServices.Init(DataManager, Log, ChatGui, "[MyPlugin]");
```
