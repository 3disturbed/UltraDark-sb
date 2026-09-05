# Tutorial 18 — Shipping Your Game

**You will build:** a packaged, distributable build for Windows, Linux and macOS,
plus the CI to produce it. **Time:** ~40 minutes.

Builds on everything so far.

---

## 1. What the pipeline does and does not do

`ExportPipeline` **stages content and metadata**. It does not invoke
`dotnet publish` — compiling and copying your game binary is a step you add.

Its eight steps:

| # | Step | Fatal on failure? |
|---|---|---|
| 1 | Validate configuration | yes |
| 2 | Create the output directory structure | yes |
| 3 | Cook assets | no — logged and continued |
| 4 | Copy `Scripts/` | no |
| 5 | Copy `Scenes/` | no |
| 6 | Write `ProjectSettings.json` | no |
| 7 | Write `PlatformDefines.json` | no |
| 8 | Write Steam `app_build.vdf` (Steam platforms only) | no |

Output lands in `Path.Combine(OutputDirectory, Platform.ToString())` — so
`OutputDirectory = "dist"` on `Windows_x64` gives `dist/Windows_x64/`.

## 2. Configure the build

```csharp
using SexyBiscuit.Engine.Build;

var config = PlatformConfig.Default(BuildPlatform.Windows_x64);
config.AppName         = "Biscuit Blaster";
config.Version         = "1.0.0";
config.BundleId        = "com.mystudio.biscuitblaster";
config.Configuration   = BuildConfiguration.Release;     // Default() returns Debug
config.OutputDirectory = "dist";
config.StartScene      = "Scenes/MainMenu.scene";
config.Scenes          = new List<string>
{
    "Scenes/MainMenu.scene",
    "Scenes/Level1.scene",
    "Scenes/Level2.scene",
};
config.IncludeDebugOverlay = false;
config.MinifyScripts       = true;
config.CookAssets          = true;

config.Save("BuildSettings.json");
```

`PlatformConfig.Default` returns a **Debug** configuration whatever the
platform, so set `Configuration` explicitly.

Validation fails the build if `AppName`, `Version`, `OutputDirectory` or
`StartScene` is empty; if Android has no `AndroidKeystorePath`; if iOS has no
`IOSTeamId`; or if the output directory is not writable.

## 3. A build CLI

`ExportPipeline.RunCli(args)` is the whole tool.

```bash
dotnet new console -o Tools/Build
cd Tools/Build
dotnet add reference ../../SexyBiscuit.Engine/SexyBiscuit.Engine.csproj
cd ../..
```

`Tools/Build/Program.cs`:

```csharp
using SexyBiscuit.Engine.Build;

internal static class Program
{
    private static void Main(string[] args) => ExportPipeline.RunCli(args);
}
```

```bash
dotnet run --project Tools/Build -- --platform windows-x64 --config release --output ./dist
dotnet run --project Tools/Build -- --file BuildSettings.json
```

| Flag | Values |
|---|---|
| `--platform` | `windows-x64`, `windows-x86`, `linux-x64`, `macos-x64`, `macos-arm64`, `android`, `ios`, `steam-windows`, `steam-linux`, `steam-macos` |
| `--config` | `debug`, `development`, `release` |
| `--output` | output root, default `dist` |
| `--keystore` | Android keystore path |
| `--depot` | Steam depot id |
| `--file` | load a saved `PlatformConfig`; other flags are ignored |

Exit code is `0` on success and `1` on failure, so it drops into CI.

> **Two things the root `README.md` gets wrong.** There is no `sbengine`
> executable and no `--all` flag — build the wrapper above. And `RunCli`'s parse
> loop is `for (i = 0; i < args.Length - 1; i++)`, so a flag in the final
> argument position is never read. Always pass flag/value pairs.

## 4. Asset cooking

```csharp
var cooker = new AssetCooker { IsIncrementalCook = true };
CookResult r = cooker.Cook("Assets", "dist/Windows_x64/Assets", config);
Console.WriteLine($"{r.FilesProcessed} processed, {r.FilesSkipped} skipped, {r.ErrorCount} errors");
```

| Extension | Treatment |
|---|---|
| `.png`, `.jpg`, `.bmp` | texture processing |
| `.ogg`, `.mp3`, `.wav` | audio processing |
| `.js` | script processing; minified when `MinifyScripts` |
| `.json`, `.scene`, `.prefab`, `.ttf`, `.otf` | copied verbatim |
| anything else | copied verbatim, with a log note |

Incremental cooking hashes each source file and skips unchanged ones on the next
run. Set `IsIncrementalCook = false` to force a full rebuild.

**The runtime loads raw assets**, so cooked output must stay in a format
`Texture2D.FromStream` and `SoundEffect.FromStream` can read. Keep audio as
16-bit PCM WAV unless you also add a decoder.

## 5. The complete build script

`build.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

PLATFORM=${1:-windows-x64}
RID=${2:-win-x64}

case "$PLATFORM" in
  windows-x64) DIR=Windows_x64 ;;
  linux-x64)   DIR=Linux_x64 ;;
  macos-x64)   DIR=macOS_x64 ;;
  macos-arm64) DIR=macOS_ARM64 ;;
  *) echo "unknown platform: $PLATFORM" >&2; exit 1 ;;
esac

OUT="dist/$DIR"

echo "==> Staging content"
dotnet run --project Tools/Build -- \
  --platform "$PLATFORM" --config release --output ./dist

echo "==> Publishing binary"
dotnet publish MyGame/MyGame.csproj \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  -o "$OUT"

echo "==> Done: $OUT"
ls -la "$OUT"
```

```bash
chmod +x build.sh
./build.sh windows-x64 win-x64
./build.sh linux-x64   linux-x64
./build.sh macos-arm64 osx-arm64
```

| `--platform` | .NET RID |
|---|---|
| `windows-x64` | `win-x64` |
| `linux-x64` | `linux-x64` |
| `macos-x64` | `osx-x64` |
| `macos-arm64` | `osx-arm64` |

`MonoGame.Framework.DesktopGL` ships **native SDL and OpenAL binaries per RID**,
so always publish with an explicit `-r`, and test the output on a machine
without the .NET SDK installed. This is the most common shipping failure.

## 6. Release configuration

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <DefineConstants>RELEASE</DefineConstants>
  <Optimize>true</Optimize>
  <DebugType>none</DebugType>
  <TieredPGO>true</TieredPGO>
</PropertyGroup>
```

**Remove `STEAMWORKS` if you are not shipping on Steam.** It is currently defined
in **all three** configurations of `SexyBiscuit.Engine.csproj`, so a non-Steam
build still compiles the real Steamworks.NET calls.

What `Release` changes for free:

- `ScriptHotReload` compiles to no-op stubs, so leaving the calls in costs
  nothing.
- `AssetManager` creates no `FileSystemWatcher`s.
- `Debug.WriteLine` compiles out — use `Trace.WriteLine` for anything you want
  in a shipping log.

Guard your debug tooling:

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);

#if DEBUG || DEVELOPMENT
    Gizmos.Update(Time.DeltaTime);
    DebugOverlay.Update(Time.DeltaTime);
    MemoryViewer.Update();
#endif
}
```

## 7. Save locations

Writing next to the executable fails on a read-only install directory. Point
persistence at a per-user directory —
[Tutorial 11 §2](11-saving-and-loading.md#2-a-per-user-data-directory):

```csharp
protected override void OnEngineReady()
{
    UserData.Install();       // SaveManager.SaveDirectory + PlayerPrefs.PrefsPath
    // …
}
```

## 8. Logging in release

`Debug.WriteLine` is gone in `Release`. Add a trace listener so shipped builds
still produce a log you can ask a player for:

```csharp
using System.Diagnostics;

public sealed class FileTraceListener : TraceListener
{
    private readonly StreamWriter _writer;

    public FileTraceListener(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public override void Write(string? message)     => _writer.Write(message);
    public override void WriteLine(string? message) => _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
```

```csharp
private static void Main()
{
    Trace.Listeners.Add(new FileTraceListener(Path.Combine(UserData.Logs, "game.log")));
    Trace.WriteLine($"start v{typeof(Program).Assembly.GetName().Version}");

    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        Trace.WriteLine($"FATAL: {e.ExceptionObject}");
        Trace.Flush();
    };

    using var game = new Game();
    game.Run();
}
```

Rotate the file or truncate on start — an append-only log from a game someone
plays daily will eventually be enormous.

## 9. Steam

For Steam platforms the pipeline writes `app_build.vdf` using `SteamAppId`,
`SteamDepotId` and `SteamBranch`. Upload it yourself:

```bash
dotnet run --project Tools/Build -- --platform steam-windows --depot 123456 --config release
dotnet publish MyGame -c Release -r win-x64 --self-contained true -o dist/Steam_Windows

steamcmd +login "$STEAM_USER" \
         +run_app_build "$(pwd)/dist/Steam_Windows/app_build.vdf" \
         +quit
```

The design document describes an "Upload to Steam" button that runs `steamcmd`
automatically; the pipeline generates the VDF but does not run anything.

Checklist:

- `steam_appid.txt` beside the executable **during development only** — do not
  ship it; a shipping build gets its app id from the client.
- `SteamManager.Instance?.Update()` in your loop, or no callback ever fires.
- Achievement and stat ids must match App Admin exactly; typos fail silently.
- Enable Cloud in App Admin and declare the file paths before relying on it.
- Handle `SteamManager.IsOverlayActive` by pausing — required for Deck
  verification, and good manners.

## 10. Mobile

Android and iOS are **validated but not built**. The pipeline checks for a
keystore path and a team id, then stages content the same way as desktop. There
is no manifest generation, no APK/AAB packaging and no Xcode project generation
in the source — those parts of the root `README.md` describe intent.

Shipping to mobile means adding MonoGame's Android/iOS project heads and your
own packaging step. Budget real time for it.

## 11. CI

`.github/workflows/build.yml`:

```yaml
name: build

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  desktop:
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: windows-latest
            platform: windows-x64
            rid: win-x64
            dir: Windows_x64
          - os: ubuntu-latest
            platform: linux-x64
            rid: linux-x64
            dir: Linux_x64
          - os: macos-latest
            platform: macos-arm64
            rid: osx-arm64
            dir: macOS_ARM64

    runs-on: ${{ matrix.os }}

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      # Every project targets plain net8.0, so the whole solution builds on
      # all three runners — the editor included.
      - run: dotnet build SexyBiscuit.sln -c Release
      - run: dotnet build MyGame -c Release

      - run: dotnet test SexyBiscuit.Tests -c Release
        continue-on-error: true

      - name: Stage content
        run: dotnet run --project Tools/Build -- --platform ${{ matrix.platform }} --config release --output ./dist

      - name: Publish binary
        run: >
          dotnet publish MyGame/MyGame.csproj -c Release
          -r ${{ matrix.rid }} --self-contained true
          -p:PublishSingleFile=true -o dist/${{ matrix.dir }}

      - uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.platform }}
          path: dist/${{ matrix.dir }}
```

## 12. Testing the build

Do all of this before you release, on a machine that is **not** your dev box:

- [ ] No .NET SDK installed → the self-contained build runs anyway.
- [ ] Launch from a path with a space in it.
- [ ] Launch from a read-only directory → saves still work.
- [ ] Delete the save directory → the first run creates it.
- [ ] Corrupt a save file → the game recovers rather than crashing.
- [ ] Unplug the gamepad mid-game → input falls back to keyboard.
- [ ] Alt-tab, minimise, resize.
- [ ] Set a non-native resolution and toggle fullscreen.
- [ ] Watch memory over 30 minutes → no unbounded growth.
- [ ] Multiplayer across two machines, not two windows on one.

## 13. Pre-ship checklist

**Build**

- [ ] `Configuration = Release`; `DEBUG`/`DEVELOPMENT` symbols gone.
- [ ] `STEAMWORKS` removed unless shipping on Steam.
- [ ] `IncludeDebugOverlay = false`; overlays and gizmos guarded.
- [ ] Published with an explicit `-r <rid>`, self-contained.
- [ ] Content folders copied: `Assets`, `Scripts`, `Scenes`.

**Runtime**

- [ ] Saves and prefs in a per-user directory.
- [ ] Crash-safe save writes with a `.bak`.
- [ ] A `Trace` log written to the user directory.
- [ ] Audio is 16-bit PCM WAV.
- [ ] Networking ticked on unscaled time; script hot reload pumped in dev.
- [ ] `SteamManager.Instance?.Update()` if using Steam.

**Content**

- [ ] Every scene in `PlatformConfig.Scenes`.
- [ ] `StartScene` points at a real file.
- [ ] No `Debug.WriteLine` spam in hot paths.
- [ ] Version number bumped.

---

## Checkpoint

You have:

- A build CLI and a one-command build script
- Correct release configuration for the engine's compilation symbols
- Per-user saves and a shipping log
- CI producing artifacts for three platforms
- A test pass that catches the failures dev machines hide

---

**Next:** [Tutorial 19 — Build a Complete Game](19-capstone-twin-stick.md)
