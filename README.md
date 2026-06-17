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
