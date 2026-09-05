# 18. Build & Export

Namespace: `SexyBiscuit.Engine.Build`

Three pieces:

| Type | Role |
|---|---|
| `PlatformConfig` | the build description, saved as JSON |
| `AssetCooker` | incremental asset processing |
| `ExportPipeline` | the eight-step build, plus a CLI |

> The export pipeline **stages content and metadata**. It does not invoke
> `dotnet publish` — compiling and copying your game binary is a step you add.
> See [Wiring it to a real build](#wiring-it-to-a-real-build).

---

## PlatformConfig

```csharp
var config = PlatformConfig.Default(BuildPlatform.Windows_x64);
config.AppName         = "Biscuit Chronicles";
config.Version         = "1.2.0";
config.BundleId        = "com.mystudio.biscuit";
config.Configuration   = BuildConfiguration.Release;
config.OutputDirectory = "dist";
config.StartScene      = "Scenes/MainMenu.scene";
config.Scenes          = new List<string> { "Scenes/MainMenu.scene", "Scenes/Overworld.scene" };

config.Save("BuildSettings.json");
var loaded = PlatformConfig.Load("BuildSettings.json");
```

### Platforms

```csharp
public enum BuildPlatform
{
    Windows_x64, Windows_x86,
    Linux_x64,
    macOS_x64, macOS_ARM64,
    Android, iOS,
    Steam_Windows, Steam_Linux, Steam_macOS,
}

public enum BuildConfiguration { Debug, Development, Release }
```

### Fields

| Field | Default | Notes |
|---|---|---|
| `AppName` | `"MyGame"` | required |
| `Version` | `"1.0.0"` | required |
| `BundleId` | `"com.studio.mygame"` | |
| `OutputDirectory` | `dist/<Platform>` | output goes to `OutputDirectory/<Platform>/` |
| `StartScene` | `"Scenes/Main.scene"` | required |
| `Scenes` | `["Scenes/Main.scene"]` | scene manifest |
| `SteamAppId` / `SteamDepotId` | `480` / `481` on Steam platforms | 480 is Steam's Spacewar test app |
| `SteamBranch` | `"default"` | |
| `AndroidKeystorePath` / `Password` | `"keystore.jks"` on Android | path is required for Android |
| `IOSTeamId` / `IOSProvisioningProfile` | placeholders on iOS | team id is required for iOS |
| `IncludeDebugOverlay` | `!isRelease` | written into the staged `ProjectSettings.json` |
| `MinifyScripts` | `isRelease` | strips comments and whitespace from `.js` |
| `CookAssets` | `true` | |

`PlatformConfig.Default` returns a **Debug** configuration regardless of
platform, so set `Configuration` explicitly for a shipping build.

A worked example lives at `SexyBiscuit.Editor/BuildConfig.json`.

---

## The export pipeline

```csharp
var pipeline = new ExportPipeline();
ExportResult result = pipeline.Export(config);

Console.WriteLine(result.Success ? "OK" : "FAILED");
Console.WriteLine($"{result.OutputPath} in {result.Duration.TotalSeconds:F2}s");
foreach (var line in result.Log)    Console.WriteLine(line);
foreach (var err  in result.Errors) Console.Error.WriteLine(err);
```

### Steps

| # | Step | Fatal on failure? |
|---|---|---|
| 1 | Validate configuration | yes |
| 2 | Create the output directory structure | yes |
| 3 | Cook assets (`AssetCooker`) | no — logged and continued |
| 4 | Copy `Scripts/` → `<out>/Scripts` | no |
| 5 | Copy `Scenes/` → `<out>/Scenes` | no |
| 6 | Write `ProjectSettings.json` | no |
| 7 | Write `PlatformDefines.json` | no |
| 8 | Write Steam `app_build.vdf` (Steam platforms only) | no |

Output lands in `Path.Combine(config.OutputDirectory, config.Platform.ToString())`
— so `OutputDirectory = "dist"` with `Windows_x64` gives `dist/Windows_x64/`.

### Validation

Step 1 fails the build if `AppName`, `Version`, `OutputDirectory` or
`StartScene` is empty; if the platform is `Android` and `AndroidKeystorePath` is
empty; if the platform is `iOS` and `IOSTeamId` is empty; or if the output
directory is not writable (checked by writing and deleting a probe file).

### Staged `ProjectSettings.json`

```json
{
  "appName": "Biscuit Chronicles",
  "version": "1.2.0",
  "bundleId": "com.mystudio.biscuit",
  "platform": "Windows_x64",
  "buildConfiguration": "Release",
  "startScene": "Scenes/MainMenu.scene",
  "scenes": ["Scenes/MainMenu.scene", "Scenes/Overworld.scene"],
  "steamAppId": 480,
  "includeDebugOverlay": false
}
```

Nothing in the engine reads this file at runtime — `EngineConfig` is constructed
in code. If you want data-driven startup, read it yourself:

```csharp
static EngineConfig LoadSettings(string path)
{
    if (!File.Exists(path)) return new EngineConfig();
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;

    return new EngineConfig
    {
        WindowTitle = root.TryGetProperty("appName",    out var n) ? n.GetString()! : "Game",
        StartScene  = root.TryGetProperty("startScene", out var s) ? s.GetString()! : "",
    };
}
```

---

## AssetCooker

```csharp
var cooker = new AssetCooker { IsIncrementalCook = true };
CookResult r = cooker.Cook("Assets", "dist/Windows_x64/Assets", config);
Console.WriteLine($"{r.FilesProcessed} processed, {r.FilesSkipped} skipped, {r.ErrorCount} errors");
foreach (var line in cooker.GetLog()) Console.WriteLine(line);
```

### Handling by extension — Verified

| Extension | Treatment |
|---|---|
| `.png`, `.jpg`, `.bmp` | texture processing |
| `.ogg`, `.mp3`, `.wav` | audio processing |
| `.js` | script processing (minified when `MinifyScripts`) |
| `.json`, `.scene`, `.prefab`, `.ttf`, `.otf` | copied verbatim |
| anything else | copied verbatim, with a log note |

### Incremental cooking

With `IsIncrementalCook = true` (the default) each source file's hash is
recorded after a successful cook. Unchanged files are skipped on the next run
and logged as `SKIP (unchanged)`. Delete the hash cache or set
`IsIncrementalCook = false` to force a full rebuild.

Remember that the runtime loads **raw** assets, so cooked output must remain in
a format `Texture2D.FromStream` and `SoundEffect.FromStream` can read. Keep
audio as 16-bit PCM WAV unless you also add a decoder — see
[13. Assets](13-assets.md#supported-types--verified).

---

## The CLI

```csharp
ExportPipeline.RunCli(args);
```

Wire it into a small build tool:

```csharp
// Tools/Build/Program.cs
using SexyBiscuit.Engine.Build;

internal static class Program
{
    private static void Main(string[] args) => ExportPipeline.RunCli(args);
}
```

```bash
dotnet run --project Tools/Build -- --platform windows-x64 --config release --output ./dist
dotnet run --project Tools/Build -- --platform linux-x64   --config release
dotnet run --project Tools/Build -- --platform macos-arm64 --config release
dotnet run --project Tools/Build -- --platform android --keystore ./release.keystore --config release
dotnet run --project Tools/Build -- --platform steam-windows --depot 123456 --config release
dotnet run --project Tools/Build -- --file BuildSettings.json
```

### Arguments

| Flag | Values |
|---|---|
| `--platform` | `windows-x64`, `windows-x86`, `linux-x64`, `macos-x64`, `macos-arm64`, `android`, `ios`, `steam-windows`, `steam-linux`, `steam-macos` (underscores also accepted) |
| `--config` | `debug`, `development`, `release` |
| `--output` | output root, default `dist` |
| `--keystore` | Android keystore path |
| `--depot` | Steam depot id |
| `--file` | load a saved `PlatformConfig` JSON; other flags are then ignored |

Exit code is `0` on success and `1` on failure, so it drops straight into CI.
An unknown platform or configuration throws `ArgumentException`.

> The root `README.md` shows an `sbengine build …` command and a `--all` flag.
> Neither exists — there is no `sbengine` executable in the solution, and
> `RunCli` does not parse `--all`. Build the small wrapper above instead.
> Note also that `RunCli`'s loop is `for (i = 0; i < args.Length - 1; i++)`, so
> a flag in the final argument position is never read — always pass flag/value
> pairs, never a trailing bare flag.

---

## Wiring it to a real build

The pipeline stages content; you still need the executable. A complete script:

```bash
#!/usr/bin/env bash
set -euo pipefail

RID=${1:-win-x64}
PLATFORM=${2:-windows-x64}
OUT="dist/$(echo "$PLATFORM" | tr 'a-z-' 'A-Z_')"

# 1. Stage content and metadata.
dotnet run --project Tools/Build -- --platform "$PLATFORM" --config release --output ./dist

# 2. Publish the game binary into the same folder.
dotnet publish MyGame/MyGame.csproj \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o "$OUT"

echo "Build complete: $OUT"
```

Matching runtime identifiers:

| `BuildPlatform` | `--platform` | .NET RID |
|---|---|---|
| `Windows_x64` | `windows-x64` | `win-x64` |
| `Linux_x64` | `linux-x64` | `linux-x64` |
| `macOS_x64` | `macos-x64` | `osx-x64` |
| `macOS_ARM64` | `macos-arm64` | `osx-arm64` |

`MonoGame.Framework.DesktopGL` ships native SDL and OpenAL binaries per RID —
always publish with an explicit `-r`, and test the output on a machine without
the .NET SDK installed.

### Steam

For Steam platforms the pipeline writes `app_build.vdf` into the output
directory using `SteamAppId`, `SteamDepotId` and `SteamBranch`. Upload it with
`steamcmd` yourself:

```bash
steamcmd +login "$STEAM_USER" +run_app_build "$(pwd)/dist/Steam_Windows/app_build.vdf" +quit
```

The design document describes an "Upload to Steam" button that shells out to
`steamcmd` automatically; the pipeline generates the VDF but does not run
anything.

### Mobile

Android and iOS are **validated but not built**. The pipeline checks for a
keystore path and a team id, then stages content the same way as desktop. There
is no `AndroidManifest.xml` generation, no `.apk`/`.aab` packaging and no Xcode
project generation in the source — those parts of the root `README.md` describe
intent. Shipping to mobile means adding MonoGame's Android/iOS project heads and
your own packaging step.

---

## A GitHub Actions job

```yaml
name: build
on: [push]

jobs:
  desktop:
    strategy:
      matrix:
        include:
          - os: windows-latest
            platform: windows-x64
            rid: win-x64
          - os: ubuntu-latest
            platform: linux-x64
            rid: linux-x64
          - os: macos-latest
            platform: macos-arm64
            rid: osx-arm64
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet build SexyBiscuit.sln -c Release
      - run: dotnet run --project Tools/Build -- --platform ${{ matrix.platform }} --config release --output ./dist
      - run: dotnet publish SexyBiscuit.Demo -c Release -r ${{ matrix.rid }} --self-contained true -o dist/out
      - uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.platform }}
          path: dist
```

Every project targets plain `net8.0`, so `dotnet build SexyBiscuit.sln` works on
all three runners — the editor included.

---

## Pre-ship checklist

- [ ] `Configuration = Release`; confirm `DEBUG`/`DEVELOPMENT` symbols are gone.
- [ ] `IncludeDebugOverlay = false`; overlays and gizmos guarded or removed.
- [ ] `STEAMWORKS` is defined in **all three** configurations of
      `SexyBiscuit.Engine.csproj` — remove it if you are not shipping on Steam,
      or ensure `SteamManager.Init` is only called when appropriate.
- [ ] Saves and prefs point at a
      [per-user directory](14-save-system.md#where-saves-should-live).
- [ ] Every content folder is copied to output (`Assets`, `Scripts`, `Scenes`).
- [ ] Audio is 16-bit PCM WAV, or you added a decoder.
- [ ] Published with an explicit `-r <rid>` and tested without the SDK present.
- [ ] Hot-reload calls left in — they compile to no-ops in Release.

---

## Next

- [19. Steam](19-steam.md)
- [Tutorial 18: Shipping Your Game](../tutorials/18-shipping.md)
