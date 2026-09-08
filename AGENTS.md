# SexyBiscuit — the agent workflow

This is the whole pipeline, end to end: **prototype a game in HTML5, playtest it in a browser,
and when it is fun, build it natively with MonoGame and publish those builds to DarksGames.**

Read this before starting work. `CLAUDE.md` is the short orientation; the per-cookie `AGENT.md`
files under `CookieJar/` are something else entirely — they describe one reusable module each.

```
brief ─► new game from a template ─► edit Scripts/*.js + Scenes/*.scene ─► npm run validate
                                              ▲                                    │
                                              │                                    ▼
                                     team feedback (text) ◄──── serve --watch / web build
                                              │
                              when it is fun  ▼
                          sbengine --all ─► native players ─► publish to DarksGames
```

Three phases, three different machines' worth of assumptions:

| Phase | Where it runs | What it needs |
|---|---|---|
| 1. Prototype | remote, headless | node 22+, nothing else. No .NET, no display, no editor |
| 2. Native build | any desktop OS, or CI | a .NET SDK targeting net8.0, plus the `android` workload, a JDK and an Android SDK for the APK |
| 3. Publish | anywhere with the token | `DG_BUILD_TOKEN` |

---

## The one rule that makes this work

Game logic is **JavaScript** in `Scripts/*.js`, written against the **shared scripting
contract** in `html5/src/scripting/bridge-api.json`. The C# engine's Jint bridge and the browser
bridge implement that contract member for member, and tests on both sides hold them to it.

That is why phase 2 is a *build* and not a rewrite: a script that runs in the browser runs
natively, unchanged. Never "port" JavaScript logic into C# as part of shipping. A C# layer is
added only for a documented reason — an engine feature the browser lacks, or native performance —
and even then the JavaScript stays the source for the web build.

If the validator says a script is outside the contract, fix the script. Do not widen the bridge on
one side only; both bridges and both test suites move together or not at all.

---

## Working where

Each folder of the repository has a `CLAUDE.md` with its gate, loaded when you work there, and
`.claude/rules/` names the files that are twins across the two engines. On the fleet box (the
Linux machine where `/srv/darksgames` exists, which is production) read `ops/fleet-box.md` before
any build or deploy; nothing in it applies on a development Mac.

---

## Phase 1 — prototype in HTML5

Headless and node-only. Assume no display and no .NET.

Games live at `Games/<Name>/` in a repository seeded from this one. The `/new-game` skill
(`.claude/skills/new-game/SKILL.md`) does the setup: clone the engine, add the game repo as
`origin` and the engine as `upstream`, copy a template from `Templates/`, write `BRIEF.md`,
validate, commit, push.

The edit loop, from `html5/`:

```bash
npm run validate -- ../Games/<Name>    # every scene loads, every script compiles and stays in the contract
npm test                               # both engines, including the template smoke tests
node tools/serve.js --watch            # live reload over SSE; prints a LAN address
```

`serve.js` prints a URL the team can open on a phone on the same network:

```
http://<lan-ip>:8080/html5/runtime/?project=/Games/<Name>/&scene=Scenes/Main
```

For a build the team keeps, and for the Game Card on the server:

```bash
node tools/export.js ../Games/<Name> --pwa    # dist/Web/ plus <slug>-<version>-web.zip
node tools/export.js ../Games/<Name> --pwa --dg <slug>   # …with accounts and friends
```

The export is a static site with a manifest and a service worker, so it installs to a phone's home
screen and runs offline.

`--dg <slug>` (or `darksGames` in `ProjectSettings.json`) puts the account and social layer in the
page: the two hub SDKs, identity, presence, friends and Join. A build with no slug loads nothing
from the hub. `wiki/29-darksgames.md` is the reference; the slug has to match the catalogue entry,
because it is also the token audience.

**The web build is never published to DarksGames.** That API serves downloadable native builds.
HTML5 development builds go on the server as a Game Card instead.

Rules for this phase, and the reason for each:

- **A game has a real UI tree now.** `UI.build({...})` takes a whole screen in one call: nodes
  that contain nodes, laid out in rows, columns and grids, sized `auto`, `grow` or `"*"`, and
  navigable with a pad or a TV remote because any button is focusable. Text works on both engines.
  Do not build another HUD out of world-space sprites over the player's head, and do not hand-place
  rows by adding up glyph heights; reach for the `hud-kit` cookie, and `screen-effects` for shake,
  flash, fade and hit-stop. A UI that genuinely belongs in the scene -- a terminal on a wall, a
  sign -- is the same tree with `UI.space = "world"`, and a marker that tracks something is a node
  with `worldFollow`; neither needs sprites. `wiki/11-scripting.md` is the reference.
- **Do not read the engine source to write a game.** Read `wiki/11-scripting.md` and the
  template's own scripts. The engine is over 50,000 lines of C#; putting it in context is the
  single most expensive thing a session can do.
- **The validator and the tests are the checker, not screenshots.** A validator line costs about
  fifty tokens; a screenshot costs several hundred, and a screenshot loop costs many turns.
- **One game per session, opening with the brief**, not with a scene dump.
- Feedback comes back as text — an issue, or `Games/<Name>/FEEDBACK.md` — and the next round is a
  fresh session.

---

## Phase 1 field notes — where a session actually goes

Written after building Jake01 from the Survival Crafting template in one sitting. The traps that
pass every check and are still wrong, the cross-script marshalling rule and the smaller costs are
in `wiki/21-gotchas.md` under *Prototype field notes*; read that page once per game.

### The order that works

1. **Read the brief, pick the closest template, then look in `CookieJar/` before writing a line.**
   Most of the jar is `"engines": ["js"]` now, and a cookie summary costs about thirty tokens
   against thousands to derive the same module again. Filter on the engine: a `csharp` cookie is
   no use in phase 1.
2. **Read `wiki/11-scripting.md` and the template's own scripts. Nothing else.** Then read
   `bridge-api.json` for the member list. That is the whole reading budget; the engine source is
   50,000 lines and putting it in context is the single most expensive thing a session can do.
3. **Build the world in a script, not in the `.scene` file.** Jake01's scene is seven actors and
   a `CityBuilder.js` that lays out 230 more. The layout becomes a dozen numbers at the top of a
   file, which is what a prototype needs, and the diff stays readable.
4. **Write the game's own harnesses before tuning anything.** They are what let you change a
   number and know in nine seconds that nothing broke.
5. `validate --strict`, `npm test`, then your harnesses. Then export, then hand it over.

### Two patterns that keep a prototype cheap

**One ledger beats many scripts.** Jake01 has 52 searchable containers and no container script:
the builder keeps parallel arrays keyed by actor id, and the survivor finds a container with
`Physics.overlapCircle` and asks by `id`. Fifty-two actors instead of fifty-two script engines,
and all the loot logic in one file.

**Face the way you move.** Mouse aiming needs a screen-to-world conversion, which needs the
viewport, which does not exist — and it is unplayable on a phone anyway. Rotating toward the
movement direction costs one `Math.atan2`, works with `Input.joystickX/joystickY` for touch, and
is resolution-independent. If the sprite is square the rotation is invisible: make it oblong, or
give it a small second actor as a nose.

---

## Phase 2 — the native MonoGame build

Runs on a Mac (or in CI) with the .NET 8 SDK. `/ship-game`
(`.claude/skills/ship-game/SKILL.md`) is this phase as a skill.

**Gate first.** Fix the game, not the gate:

```bash
cd html5 && npm run validate -- ../Games/<Name> --strict && npm test
cd .. && dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj
```

**Settings.** `Games/<Name>/BuildSettings.json` carries `appName`, `version`, `startScene` and an
`upload` section (`wiki/18-build-export.md` documents every key). Bump `version` for a genuinely
new build — it is part of a build's identity on the site.

It is **optional**, and no template ships one: with no file at all `sbengine` uses the folder name,
`1.0.0`, and — the part that bites — **`Debug`**. A release build needs `--config release` on the
command line or the key in the file. A 34 MB Debug archive published as an alpha is the failure
mode here, and nothing warns you.

**Give every game one on the first publish, or the version never moves.** With no file the version
is `1.0.0` on every run for ever, so each publish replaces the last at the same triple and the
downloads page looks identical after a fix as it did before it — the tester cannot tell a new
build from the one they already have, and neither can you. Two lines are enough to fix that
permanently:

```json
{
  "appName": "Jake01",
  "version": "1.0.1",
  "startScene": "Scenes/City",
  "upload": { "appSlug": "jake01", "channel": "alpha" }
}
```

Keys are camelCase; `PlatformConfig` is what parses them.

**It does not need a Mac.** All four targets cross-compile from one Linux box in about eighteen
seconds with the .NET 10 SDK targeting net8.0; `osx-arm64` included. The table above used to say
otherwise.

**Build every target with the `sbengine` CLI:**

```bash
dotnet build SexyBiscuit.Build/SexyBiscuit.Build.csproj -c Release
dotnet run --no-build --project SexyBiscuit.Build -c Release -- \
  --project "Games/<Name>" --all --quiet
```

`--all` is web, win-x64, osx-arm64 and linux-x64. `--platform <name>` picks one; it accepts RIDs
and `BuildSettings` platform names.

### Android: every publish carries an APK

`--all` builds one, signed with the SDK's debug key, and it is the build most testers take. How
the APK is produced, what is left out of it and why is `wiki/18-build-export.md`, under *Android*.
Read the summary, not the log: one line per target, and the error lines under a failed one.

---

## Phase 3 — publish to DarksGames

### The API key

The token is the one secret in this pipeline. It is read at publish time from the first of:

1. the variable named by `upload.tokenVariable` in `BuildSettings.json` (`DG_BUILD_TOKEN` by default)
2. `DG_BUILD_TOKEN`
3. `SB_UPLOAD_TOKEN` (the older name, still honoured)
4. the first line of `upload.tokenFile` — `~/.sexybiscuit/dg-token` by default, mode 600

It is **never** written to a file by the engine, never logged, never put in a build report, and
never committed. `BuildSettings.json` stores the *name* of the variable and the *path* of the
file, so it stays committable. The editor's Publish tab reports only whether a token was found and
where. A token file readable by other users earns a warning.

In CI it is the `DG_BUILD_TOKEN` repository secret.

A token is scoped to one app slug and minted at Admin → Builds; the details are `ops/fleet-box.md`.

### What publishes

**Native builds only.** Windows, macOS and Linux archives. The web build is skipped with a note
rather than reported as a failure, because it is not a downloadable game build.

### Publishing

Add `--upload` to the build command, or publish archives that already exist:

```bash
dotnet run --no-build --project SexyBiscuit.Build -c Release -- \
  --project "Games/<Name>" --all --upload --channel alpha --quiet
```

| Flag | Meaning |
|---|---|
| `--upload` | publish every native archive |
| `--app-slug <slug>` | the game's slug in the catalogue; defaults to one derived from `appName` |
| `--channel alpha\|beta\|demo` | `alpha` for nightlies, `beta` for playtest candidates, `demo` for public |
| `--notes <text>` | release notes on the download card |
| `--requirements <text>` | what a player needs; blank gets a per-platform default |
| `--hidden` | upload as a **closed** build: listed and downloadable only for a playtester |
| `--no-replace` | make a version collision an error instead of replacing |

From the editor: the **Publish** tab in Build Settings, or the `publish_build` MCP tool. Prefer
`publish_build` over re-running `export_build` — it sends archives already on disk, so a failed
publish never costs another twenty-minute desktop build.

In CI: `gh workflow run release.yml -f game=Games/<Name> -f upload=true`, then
`gh run watch --exit-status`.

### The thing that bites: identity and replacement

A build's identity is the triple **appSlug + version + platform**. Publishing the same triple
again **replaces that build in place and keeps its id**, so a download link already handed to a
playtester keeps working. That is the default and it is usually what you want.

- Bump `version` for a genuinely new build.
- Keep `version` to re-cut the current one.
- **On Android, keeping it is not a free choice.** `ApplicationVersion` — the integer Android
  orders upgrades by — is derived from the version string (1.0.1 becomes 10001). Re-cutting at the
  same version ships the same code, and a phone that already has the app can treat the new APK as
  something it is already running and decline to install it. A tester then keeps playing the old
  build while looking at a page that says it was updated. **Any Android re-cut worth downloading
  gets a version bump.**
- **A bumped version leaves the old one listed.** Replacement only happens within a triple, so
  1.0.1 sits alongside 1.0.0 rather than replacing it, and a tester can still pick the broken one.
  Remove the superseded build from **Admin → Builds** (`DELETE /admin/builds/:id`) once the new one
  is up.
- Two targets that map to the same platform — both macOS architectures, say — share a triple and
  would silently replace each other. The publisher says so by name before sending anything; give
  one a different version, or set `upload.platformMap`.

Build targets map onto the API's five platform names: Windows and Steam Windows to `windows`, both
macOS architectures to `macos`, Linux to `linux`, Android to `android`, iOS to `other`. Web maps
to nothing and is not published.

### Limits, checked before a byte is sent

1 GB per file; 6144 bytes of encoded metadata (long release notes are trimmed to fit); 30
publishes per hour per IP; and an extension allowlist that our `.zip` and `.tar.gz` archives
already satisfy. The allowlist is a security control, not tidiness — builds are served back from
the site's own origin. Never work around it; wrap the artifact in a `.zip`.

Only two failures are retried, once each: a rate limit (429, honouring `Retry-After`) and a broken
stream (400 `upload_failed`). Everything else — 401, 403, 409, 413, 415, 422, 431 — is
deterministic; fix the request. The response's checksum is compared against the local SHA-256, and
a mismatch re-publishes the same triple once.

### Verifying

The publish result carries an absolute `url`, clickable straight out of a log. The public
catalogue needs no auth:

```bash
curl -s https://darksgames.app/api/v1/builds
```

A build published with `--hidden` will not appear there. The human page is
`https://darksgames.app/downloads`.

**`--hidden` means closed testing, not invisible.** As of 2026-09-06 the builds API widens both
the list and the download for a caller whose access token carries `playtester: true`, so a hidden
build behaves exactly like the catalogue's `playtest: true` games: absent for the public, present
and downloadable for the testers, and tagged PLAYTEST on `/downloads`.

That makes `--hidden` the right flag for a game in closed testing — its native builds are gated the
same way its web build is. Two things follow:

- **A signed-out `curl` of `/api/v1/builds` is unchanged**, and the download answers
  `404 not_found` with the same wording it uses for a build that does not exist. Confirming that a
  closed build exists at an id would be a leak of its own.
- The response now depends on the `Authorization` header, so it carries `Vary: Authorization` and
  goes out `private, no-store` for a playtester. A shared cache that ignored this would serve one
  tester's list to the public.
- **A download link cannot carry that header.** Clicking `<a href download>` is a navigation, and a
  navigation sends no headers, so the first cut of this let a playtester see a closed build and
  then saved the 404 JSON into their downloads folder as the file. The site asks
  `POST /api/v1/builds/:id/ticket` for a URL a navigation can follow — bound to one build, good for
  two minutes. Any other client publishing or fetching closed builds needs the same two steps.

### Reporting back

Four lines: the version and commit, the URLs, the test totals, and the session's usage line
(`node html5/tools/usage.js --latest`).

---

## Phase 3b — closed testing on DarksGames

A web build goes on the site as a Game Card, gated to playtesters while a prototype is judged:
`node html5/tools/export.js Games/<Name> --pwa --dg <slug> --gated` writes the gated layout and a
`DEPLOY.txt`. The server-side recipe runs as root on the fleet box and lives in `ops/fleet-box.md`.

---

## Reuse before writing: the CookieJar

Before writing a common mechanic, look in `CookieJar/`: a cookie summary costs about thirty tokens;
deriving the module again costs thousands, in every game that needs it. Check the `engines` field
first (`js` for phase 1). `CookieJar/README.md` lists every cookie; `CookieJar/CLAUDE.md` says how to
validate one and how to bake one back. See `wiki/28-the-cookiejar.md`.

---

## The one thing no gate can see

Every check here reads text; none can see a picture. **Look at one frame before you believe a
build.** Two defects shipped through a fully green board on 2026-09-06, and the headless recipe for
catching them, the bare-collider rule, the `layerDepth` direction and the 2.5D pivot are in
`wiki/21-gotchas.md` under *Shipped through a green board*.

---

## Gates that must stay green

Each folder's `CLAUDE.md` carries its gate; the root `CLAUDE.md` has the table. Before a commit, run
the gate of the folder you changed; before a ship, all of them:

```bash
dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c Debug -warnaserror
dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj
cd html5 && npm test && npm run lint && npm run validate
```

Both engines' action maps, euler conventions, scripting contract and hook lists are pinned by tests
that read the other side's source, so they fail if the two drift apart. A change to the scene format
or to a component's serialised properties lands on both sides in one commit. After a push,
`gh run list --workflow ci.yml --branch main --limit 1` must say `success`; never start the next
task on a red main.

---

## Where the detail lives

| Topic | Page |
|---|---|
| The scripting contract both engines implement | `wiki/11-scripting.md` |
| Building, packaging, publishing, Android, CI | `wiki/18-build-export.md` |
| The editor's MCP tools | `wiki/25-ai-assistant-mcp.md` |
| The HTML5 port | `wiki/26-html5.md`, `html5/README.md` |
| This workflow, and why it is shaped for low token use | `wiki/27-game-factory-workflow.md` |
| The CookieJar | `wiki/28-the-cookiejar.md` |
| Darks Games accounts, social and closed testing | `wiki/29-darksgames.md` |
| What shipped through a green board, and the prototype field notes | `wiki/21-gotchas.md` |
| The fleet box: paths, tokens, deploying | `ops/fleet-box.md` |
