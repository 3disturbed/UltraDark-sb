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
    "appSlug": "foo",
    "channel": "alpha",
    "requirements": "Windows 10+, 4 GB RAM",
    "replace": true,
    "publish": true
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
| `upload.appSlug` | the game's slug in the catalogue; blank derives one from `appName` |
| `upload.channel` | `alpha` (default), `beta` or `demo` |
| `upload.notes`, `upload.requirements` | the text on the download card |
| `upload.replace` | re-publishing a version overwrites it in place; `false` makes that an error |
| `upload.publish` | `false` uploads the build hidden |
| `upload.platformMap` | overrides the platform sent for a target, e.g. `{ "macOS_x64": "other" }` |
| `upload.url` | only to point at something other than DarksGames; `DG_BUILD_URL` overrides |
| `upload.tokenVariable`, `upload.tokenFile` | where to find the token — never the token |
| `steamAppId`, `steamDepotId`, `steamBranch` | the Steam VDF |
| `androidKeystorePath`, `iosTeamId`, … | validated, not built — see Mobile |
| `includeDebugOverlay`, `minifyScripts`, `cookAssets` | build options |

`projectRoot` is never saved: the pipeline sets it from where it found the file.

## Publishing development builds

`--upload` publishes every **native** archive to DarksGames. A web build is not a downloadable
game build, so it is skipped with a note rather than reported as a failure; the HTML5 build is
hosted on the server as a Game Card instead.

The request is one `POST https://darksgames.app/api/v1/builds/publish` per archive whose **body
is the file's raw bytes** — not multipart, not base64 — with the metadata in a base64url
`X-Build-Meta` header, because the body is already the binary. `Build/Upload/DarksGamesUploadTarget.cs`
sends it; `html5/tools/upload.js` sends exactly the same request, so a build published from
either side looks the same to the site.

| Meta field | Value |
|---|---|
| `appSlug` | `upload.appSlug`, else a slug derived from `appName`. `^[a-z0-9-]+$`, max 64 |
| `title`, `version` | the app name and the build's version |
| `fileName` | the archive's name, sanitised to `[A-Za-z0-9._-]` |
| `channel` | `alpha`, `beta` or `demo`; older spellings such as `dev` are mapped, not rejected |
| `platform` | `windows`, `macos`, `linux`, `android` or `other` — every desktop RID maps onto one |
| `notes` | the project's notes plus a line naming the runtime, configuration, commit and time |
| `requirements` | the project's, or a sensible per-platform default |
| `replace`, `publish` | overwrite an existing version in place; upload hidden |

A build's identity is the triple **appSlug + version + platform**: publishing it again replaces
that build in place and keeps its id, so a link already given to a playtester keeps working.
Bump the version for a genuinely new build. Two targets that map to the same platform — both
macOS architectures, say — would replace each other, so the publisher says so by name before
sending anything; `upload.platformMap` separates them.

Checked before a byte is sent: the file exists and is not empty, it is at most 1 GB, its
extension is on the allowlist (archives only — a build is served back from the site's own
origin), the slug matches the pattern, and the target is publishable. The encoded header is
trimmed to 6144 bytes by giving characters back from `notes`.

Only two failures are retried, once each, as the API asks: a rate limit (429, honouring
`Retry-After`) and a broken stream (400 `upload_failed`). Everything else — 401, 403, 409, 413,
415, 422, 431 — is deterministic and reported as `code: message`. The response's `checksum` is
compared with the local SHA-256, and a mismatch re-publishes the same triple once.

### The token

Read at publish time from the first of: the variable named by `upload.tokenVariable`
(`DG_BUILD_TOKEN` by default), `DG_BUILD_TOKEN`, `SB_UPLOAD_TOKEN`, then the first line of
`upload.tokenFile` (`~/.sexybiscuit/dg-token` by default). It is never written to a file by the
engine, never logged, and never put in a build report; a token file readable by other users
earns a warning. The editor's Publish tab says only whether one was found and where.

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
to DarksGames when `upload` is ticked (the `DG_BUILD_TOKEN` repository secret), and prints the
combined summary. Run it with

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

---

## Android: every publish carries an APK


`--all` builds one, and it is the build most testers will actually take — most people who will
try a prototype have a phone in their hand and no desktop open.

```
web        ok         0.1s  jake01-1.0.0-web.zip (188 KB)
win-x64    ok         8.3s  jake01-1.0.0-win-x64.zip (33.7 MB)
osx-arm64  ok         6.2s  jake01-1.0.0-osx-arm64.tar.gz (31.3 MB)
linux-x64  ok         6.1s  jake01-1.0.0-linux-x64.tar.gz (34.0 MB)
android    ok        68.1s  jake01-1.0.0-android.apk (35.6 MB)
```

The APK is the artifact, not a zip around one: a phone can install what it downloads. It is
signed with the SDK's debug key, which is correct for a build installed by hand — name a
keystore in `BuildSettings.json` only when signing for Play.

**How it works, because it is not a RID.** There is no `dotnet publish -r android`, so Android
does not go through `DesktopPublisher`. `AndroidPublisher` writes a head project under
`.sexybiscuit/android/` — an Activity, a manifest, launcher icons, and the staged game as
`AndroidAsset` — and publishes that. The engine grows a second target framework only when asked
(`-p:SexyBiscuitAndroid=true`), so a machine with no `android` workload still builds the engine,
runs the tests and opens the editor exactly as before.

**The generated Activity unpacks the game on first run.** Assets inside an APK are not files, and
the engine reads its project with ordinary file IO. Rather than thread a stream provider through
all of it for one platform, the Activity copies the packaged project into app-private storage
once per version and points `ProjectPaths.Root` there.

What is not in the Android build, and why: **AssimpNet** (native libassimp, desktop-only, so 3D
model import says so and returns), **Steamworks.NET** (desktop SDK; every `Steam/*.cs` was already
behind `#if STEAMWORKS`, so not defining it removes the integration with no code change),
**NAudio** (Windows-first; MP3 falls back with a message, and `.ogg`/`.wav` — what the templates
ship — decode as usual), and the C# **game-assembly loader** (`AssemblyDependencyResolver` is
unsupported on Android; an Android game's logic is JavaScript). Android uses the **same MonoGame
version** as desktop, 3.8.1.303, so there is no API drift between the two builds.

**iOS is still not buildable** and still fails loudly rather than shipping an archive with no
application in it.

**Read the summary, not the log.** The CLI prints one line per target and writes
`build-report.json` beside the output. Open a log only for a target that failed; the error lines
are printed under it. A failing publish is nearly always the engine checkout or the SDK — check
`SEXYBISCUIT_REPO` and `dotnet --version`.


---
