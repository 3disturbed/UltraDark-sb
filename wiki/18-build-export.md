# 18. Build & Export

How a project becomes something a player downloads: a web build that installs to a phone, a
self-contained desktop binary, an archive per target, a one-line-per-target report, and an
upload. Everything here is `SexyBiscuit.Engine/Build/`, driven by the `sbengine` CLI
(`SexyBiscuit.Build/`) or the editor's Build Settings panel.

## In one command

```bash
dotnet build SexyBiscuit.Build/SexyBiscuit.Build.csproj -c Release      # once
dotnet run --no-build --project SexyBiscuit.Build -c Release -- --project Games/Foo --all --upload
```

`--all` is the web build plus `win-x64`, `osx-arm64` and `linux-x64`. The output is
`Games/Foo/dist/<Platform>/` per target, an archive beside each, `dist/build-report.json`, and
on the console one line per target:

```
Foo 1.2.0 (Release) @ 3f2a9c1d
web        ok        2.1s  foo-1.2.0-web.zip (412 KB)  -> https://darksgames.app/builds/91
win-x64    ok      184.3s  foo-1.2.0-win-x64.zip (61.0 MB)  -> https://darksgames.app/builds/92
osx-arm64  ok      171.9s  foo-1.2.0-osx-arm64.tar.gz (58.4 MB)  -> https://darksgames.app/builds/93
linux-x64  ok      166.0s  foo-1.2.0-linux-x64.tar.gz (60.2 MB)  -> https://darksgames.app/builds/94
```

The exit code is 0 only when every target succeeded. `--help` lists every flag; the ones that
matter day to day:

| Flag | Meaning |
|---|---|
| `--project <dir>` | the game folder (default: the current directory) |
| `--platform <name>` | `web`, `win-x64`, `win-x86`, `linux-x64`, `osx-x64`, `osx-arm64`; repeatable or comma-separated |
| `--all` | web + win-x64 + osx-arm64 + linux-x64 |
| `--config <c>` | `debug`, `development` or `release` (default: the settings file's, else release) |
| `--version <v>` | overrides the version for this run; a CI tag supplies it |
| `--no-publish` | stage content only — what the editor's Build button does |
| `--no-zip` | leave the platform folders unarchived |
| `--upload` | send every archive to the upload target |
| `--report <path>` | write the report somewhere else too |
| `--quiet` | the summary only |

## What a build is made of

The pipeline runs eleven steps per target and collects every error rather than stopping:

| Step | Does | Notes |
|---|---|---|
| 1 Validate | app name, version, output folder, start scene, project root, platform-specific fields | a failure here is fatal |
| 2 Directories | `Assets/ Scripts/ Scenes/ Logs/` under the platform folder | |
| 3 Cook assets | `AssetCooker` over `<project>/Assets` | errors are logged, the build continues |
| 4–5 Copy | `Scripts/` and `Scenes/` | |
| 6 Settings | the project's own `ProjectSettings.json` with the build's metadata added | window size, vsync and the like survive |
| 7 Defines | `PlatformDefines.json` (`PLATFORM_WEB`, `ARCH_ARM64`, `BUILD_RELEASE`, …) | |
| 8 Steam | `app_build.vdf` for the Steam platforms | |
| 9 Web | stages `html5/src` and `html5/runtime` as `engine/`, writes the page, manifest, icons and service worker | Web only |
| 10 Publish | `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=true` | desktop only |
| 11 Package | `.zip` for Windows and the web, `.tar.gz` for Linux and macOS so the binary keeps `+x` | |

Every path resolves against the project root, never the process working directory. (It used
to; an export from the editor staged the editor's own folder.) The platform's folder is added
to `outputDirectory` once — a settings file that saved `dist\Windows_x64` is read as that folder.

### Publishing without C#

A template, and most prototypes, has no csproj. The publisher generates one under
`<project>/.sexybiscuit/player/` — a `Program.cs` that sets `ProjectPaths.Root` to the folder
the binary is in and boots the engine from the `ProjectSettings.json` beside it — and publishes
that. It lives below the scratch folder so the editor never mistakes it for game code. A project
with its own csproj publishes that instead; the generated csproj already lists the desktop
runtime identifiers.

The engine is referenced as source for a publish (`-p:SexyBiscuitEngineProject=…`), so it
compiles for the target runtime; the editor-written `SexyBiscuit.props`, which pins a game to
the editor's own Debug engine binary, is overridden for the same reason. Nothing is trimmed:
Jint and the reflection-based serialiser need the whole engine, and the native libraries (SDL,
OpenAL) are published beside the single-file binary rather than inside it because MonoGame's
loader looks there. Publishing therefore needs the engine checkout (`SEXYBISCUIT_REPO`, or run
from inside it) and the .NET SDK.

### The web build

`BuildPlatform.Web` produces a static site: `index.html`, `engine/`, the project's files, and
with `webInstallable` (the default) a `manifest.webmanifest`, `icon-192.png`, `icon-512.png`
and `sw.js` that precaches every file, so the build installs to a phone's home screen from the
site and runs offline. The page, manifest and worker come from the templates under
`html5/runtime/export/`, which `html5/tools/export.js` fills with the same values — the two
exporters produce one build, and tests on both sides pin the placeholder sets. It will not run
from `file://`; a service worker needs HTTPS or localhost. `webIconPath` names a PNG to use for
the icons instead of a flat square in the theme colour.

## AssetCooker

Step 3 copies every file under `Assets/` and re-processes only the ones whose source hash
changed since the last build (`IsIncrementalCook`). Textures are compressed on Windows
targets when `texconv.exe` is on the PATH and copied otherwise; audio is encoded with
`oggenc` when it is present and copied otherwise; scripts are minified when `minifyScripts`
is set. Cooker errors are logged and the build continues, so a bad asset shows up in the
report rather than stopping the other targets.

## BuildSettings.json

`PlatformConfig`, saved as `<project>/BuildSettings.json` (camelCase keys). `sbengine` reads it
when it exists and falls back to `ProjectSettings.json` for the name, version and start scene,
listing every scene under `Scenes/` when the file names none.

```json
{
  "configuration": "release",
  "appName": "Foo",
  "version": "1.2.0",
  "bundleId": "com.studio.foo",
  "outputDirectory": "dist",
  "startScene": "Scenes/Main",
  "webInstallable": true,
  "webIconPath": "Assets/icon.png",
  "upload": {
    "url": "https://darksgames.app/api/builds",
    "tokenVariable": "SB_UPLOAD_TOKEN",
    "channel": "dev",
    "fields": { "game": "title" }
  },
  "cookAssets": true,
  "minifyScripts": true
}
```

| Field | Notes |
|---|---|
| `platform` | overridden by `--platform`; irrelevant with `--all` |
| `outputDirectory` | relative to the project; the platform folder goes underneath |
| `webInstallable`, `webIconPath` | the installable web build (above) |
| `upload.url` | the endpoint; the `SB_UPLOAD_URL` environment variable overrides it |
| `upload.tokenVariable` | the environment variable holding the bearer token — never the token |
| `upload.channel` | sent with every upload; `dev` by default |
| `upload.fields` | renames the contract's field names to what the site expects |
| `steamAppId`, `steamDepotId`, `steamBranch` | the Steam VDF |
| `androidKeystorePath`, `iosTeamId`, … | validated, not built — see Mobile |
| `includeDebugOverlay`, `minifyScripts`, `cookAssets` | build options |

`projectRoot` is never saved: the pipeline sets it from where it found the file.

## Uploading development builds

`--upload` sends every archive to the target `BuildSettings.json` names — today an HTTP
endpoint with a bearer token; an `s3://` or `sftp://` target slots into
`Build/Upload/UploadTargets.cs` as a new case. One multipart POST per artifact carries the
file and nine metadata fields: `game`, `version`, `platform`, `rid`, `configuration`,
`gitSha`, `channel`, `builtUtc`, `sha256`. A 5xx is retried once; a 4xx is final. The reply's
`Location` header or a JSON `url` field becomes the link in the report. `html5/tools/upload.js`
sends exactly the same request, so a build uploaded from the prototype phase and one from a
native release look the same to the site. The token is read from the environment at upload
time and never written anywhere.

## The report

`dist/build-report.json` after every run, and `--report` for a second copy:

```json
{
  "appName": "Foo", "version": "1.2.0", "configuration": "Release", "gitSha": "3f2a…",
  "builtUtc": "2026-09-06T12:00:00Z",
  "targets": [
    { "platform": "Web", "success": true, "seconds": 2.1, "outputPath": "…/dist/Web",
      "archive": "…/dist/foo-1.2.0-web.zip", "archiveBytes": 421888, "errors": [],
      "uploadUrl": "https://darksgames.app/builds/91", "uploadStatus": 201 }
  ]
}
```

`BuildReport.ToSummaryLines()` is what the CLI prints and what the release workflow puts in
its step summary — one line per target, then the errors of any that failed. An agent reads
that instead of a build log.

## In CI

`.github/workflows/release.yml` builds one game on a matrix (ubuntu: web and linux-x64;
windows: win-x64; macos: osx-arm64), uploads the archives as workflow artifacts, sends them
to the upload target when `upload` is ticked (the `SB_UPLOAD_URL` and `SB_UPLOAD_TOKEN`
repository secrets), and prints the combined summary. Run it with

```bash
gh workflow run release.yml -f game=Games/Foo -f upload=true
gh run watch --exit-status
```

or by pushing a tag `release/foo-v1.2.0`, which also supplies the version. Because every game
repo is a copy of the engine repo, the workflow ships with it. `ci.yml` builds the CLI and
stages a template's web build on every push.

## The editor

The Build Settings panel stages the selected platform into the open project's `dist/`
(`--no-publish` in CLI terms); **Build & Run** publishes as well and launches the binary. The
panel's config file is `BuildSettings.json` in the project.

## Steam

The Steam platforms (`steam-windows`, `steam-linux`, `steam-macos`) are the desktop builds plus
an `app_build.vdf` in the platform folder, ready for

```bash
steamcmd +login <user> +run_app_build <path>/app_build.vdf +quit
```

The pipeline does not run `steamcmd`. See [19. Steam](19-steam.md).

## Mobile

The installable web build is the mobile development build: it installs from the site on
Android and iPhone, needs no toolchain, and runs offline. `Android` and `iOS` exist in
`BuildPlatform` as validation gates only — a keystore and a team id are checked, content is
staged, and no APK or IPA is produced. A native MonoGame Android/iOS target is a later engine
milestone (`dotnet workload install android ios`, the `MonoGame.Framework.Android` / `iOS`
packages, per-game activity and app delegate, signing); until then, ship the web build.

## Pre-ship checklist

- [ ] `npm run validate -- ../Games/Foo --strict` prints OK, and both test suites are green
- [ ] `BuildSettings.json` has the right `appName`, `version` and `startScene`
- [ ] `sbengine --project Games/Foo --all` reports `ok` on every line
- [ ] the web build opens from the site on a phone and installs
- [ ] the desktop binary starts from a clean folder (unsigned macOS apps need a right-click Open the first time)
- [ ] the upload URLs in the report open

## Next

- [26. The HTML5 Port](26-html5.md) — the web build's runtime and the node exporter
- [27. The Game Factory Workflow](27-game-factory-workflow.md) — where this fits
- [19. Steam](19-steam.md)
