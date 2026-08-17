# ZhyraPluginKit

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
     <Compile Include="..\..\ZhyraPluginKit\DevTelemetry.cs" Link="Dev\DevTelemetry.cs" />
   </ItemGroup>
   ```

2. Add two config fields (`bool DevLog`, `string DevLogUrl`) and a Developer section in the config UI.

3. Wire it up (in your `Plugin`):

   ```csharp
   using ZhyraPluginKit;
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
python3 ~/dev/ZhyraPluginKit/devlog_server.py
# listens on 0.0.0.0:9999, appends to ~/.cache/zhyra-devlog/live.log, echoes to stdout
```

Point the plugin's dev-log URL at `http://<this-box-LAN-ip>:9999/log`. Read the stream with
`tail -f ~/.cache/zhyra-devlog/live.log`.

> Local-network, no auth, plain HTTP. Dev only — do not expose to the internet.

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

```xml
<Compile Include="..\..\ZhyraPluginKit\PluginPresence.cs" Link="Kit\PluginPresence.cs" />
<Compile Include="..\..\ZhyraPluginKit\Game\LineOfSight.cs" Link="Kit\Game\LineOfSight.cs" />
```
