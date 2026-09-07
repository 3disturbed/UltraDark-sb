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

## Working on this box

This machine is the one the fleet runs on, and **it is production**. Everything below is already
installed and configured — do not set any of it up again, and do not `apt-get` or
`dotnet workload install` your way around a path you have not checked.

| What | Where |
|---|---|
| .NET SDK | `/usr/lib/dotnet` — export `DOTNET_ROOT=/usr/lib/dotnet` and add it to `PATH` |
| JDK (Android) | OpenJDK 17; `JAVA_HOME=$(dirname $(dirname $(readlink -f $(which java))))` |
| Android SDK | `/opt/android-sdk` — export `ANDROID_HOME=/opt/android-sdk` |
| `android` workload | installed for .NET 10 |
| Engine checkout | this repository; `SEXYBISCUIT_REPO` if a build cannot find it |

Two environment traps that cost real time:

- **`/tmp` is a 3.8 GB tmpfs**, i.e. RAM, and it is not empty. A `dotnet workload install` or a
  large restore through it dies with "No space left on device" while `df /` cheerfully reports
  50 GB free. Export `TMPDIR=/var/tmp/dotnet-workload` for anything that unpacks a lot. The box
  has been OOM-killed before; filling `/tmp` is one of the ways to do it again.
- **An Android build wants all three variables at once.** Missing one gives a different error each
  time — `XA5300` for the SDK, a JDK complaint, or a workload message — so set the three together
  and `AndroidPublisher` will name whichever is missing if you forget.

```bash
export DOTNET_ROOT=/usr/lib/dotnet PATH=$PATH:/usr/lib/dotnet
export JAVA_HOME=$(dirname $(dirname $(readlink -f $(which java))))
export ANDROID_HOME=/opt/android-sdk
export TMPDIR=/var/tmp/dotnet-workload
```

**Build tokens live on this box, one per app slug.** `~/.dg-build-token` is UltraDark's and will
403 for anything else; each game gets its own, e.g. `~/.dg-build-token-jake01`. Mint one at
Admin → Builds.

**Never `pkill -f "node server.js"`.** It matches every game server on the box — about two dozen —
and has caused a full fleet outage. Kill by PID.

**Deploying is local.** There is no staging box and no deploy-over-SSH: the site is prerendered
and rsynced from the checkout into `/srv/darksgames/site`, and a game service is
`systemctl restart darksgame@<slug>`. Read `--delete` dry-runs before trusting them; the live web
root holds files that are not in the repo.

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
```

The export is a static site with a manifest and a service worker, so it installs to a phone's home
screen and runs offline.

**The web build is never published to DarksGames.** That API serves downloadable native builds.
HTML5 development builds go on the server as a Game Card instead.

Rules for this phase, and the reason for each:

- **A game has real UI now.** `UI.panel/label/bar/button/image` are screen-space and anchored, and
  `UI.width`/`UI.height` are the viewport the contract went years without. Text works on both
  engines. Do not build another HUD out of world-space sprites over the player's head; reach for
  the `hud-kit` cookie, and `screen-effects` for shake, flash, fade and hit-stop.
- **Do not read the engine source to write a game.** Read `wiki/11-scripting.md` and the
  template's own scripts. The engine is over 50,000 lines of C#; putting it in context is the
  single most expensive thing a session can do.
- **The validator and the tests are the checker, not screenshots.** A validator line costs about
  fifty tokens; a screenshot costs several hundred, and a screenshot loop costs many turns.
- **One game per session, opening with the brief**, not with a scene dump.
- Feedback comes back as text — an issue, or `Games/<Name>/FEEDBACK.md` — and the next round is a
  fresh session.

---
---

## Phase 1 field notes — where a session actually goes

Written after building Jake01 from the Survival Crafting template in one sitting. The rules above
say what to do; these are the things that cost hours anyway.

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

### Four things that pass every check and are still wrong

**An actor proxy has no `addComponent`.** `Scene.createActor` returns
`{id, name, tag, active, transform, getComponent, destroy}` — that is the whole proxy. Use
`Scene.addComponent(proxy, "SpriteRenderer", {…})`. The bundled **Survival Crafting** template
calls `enemy.addComponent(...)` on a spawned actor, so its night waves throw
`enemy.addComponent is not a function` on the browser engine. Nothing catches it because the
template smoke test stops long before nightfall.

**`layerDepth`: LOW is drawn first and ends up at the BACK.** `SpriteBatch.end()` sorts ascending
and draws in that order. The comment on `SpriteSortMode.BackToFront` says "high layerDepth first"
and is the opposite of what the code does. Believing it inverted every depth in Jake01 — the road
at 0.95, drawn last, over all 234 sprites. On screen that is a flat grey rectangle with no error
anywhere, 106/106 tests green and a happy validator. **No bundled template sets `LayerDepth` at
all**, so there is nothing to copy the convention from and nothing to catch it.
`Games/Jake01/tools/draw-order.mjs` is a check worth stealing: sort the sprites the way the
renderer will and fail if any layer is not strictly behind the next.

*Unverified but worth knowing:* the C# renderer passes `LayerDepth` to MonoGame's
`SpriteSortMode.BackToFront`, whose convention is the reverse. If that is right, a scene using
`LayerDepth` renders inside-out between the two engines, and no test on either side references it.

**There is no viewport in the scripting contract.** No window size, no camera bounds. A script
therefore cannot pin anything to a screen corner or size a full-screen quad, which rules out a
conventional HUD, a screen-space overlay and mouse-to-world aiming. The way through is to put it
in world space and follow an actor: status bars above the player's head, and a tint large enough
to cover any view centred on them. Both are in the CookieJar.

**The bundled smoke test proves less than it looks like.** It runs sixty frames with no physics
host and no asset loader — so it never reaches a day/night transition, and it never loads a script
attached at runtime, which is how most things get spawned. Give the scene a real
`PhysicsSystem2D` (gravity `{x: 0, y: 0}` for top-down, or everything falls off the map) and an
`engine.assets.loadText` backed by the filesystem, and run for minutes of game time.
`Games/Jake01/tools/soak.mjs` is the pattern; it found the `addComponent` bug in seconds.

### Cross-script calls: primitives only

`getComponent("ScriptComponent").call(name, ...args)` — `call` and `invoke` are the same function.
Pass and return **numbers, strings and booleans**. They marshal identically under Jint and in the
browser; objects and arrays do not reliably. So a HUD asks for `getHealth01()` rather than a
`getStats()` that returns an object, and a loot table answers with an integer code rather than
`{type: "wood"}`. It reads as more functions and it is the difference between working on both
engines and working on one.

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

### Smaller things, each of which cost a few minutes

- `Physics.overlapCircle` **does not return triggers**. Anything you want to find with it needs a
  non-trigger collider. This is also why Survival Crafting's `onTriggerEnter` gathering never
  fires: its resource nodes are plain colliders, so `nearbyResource` stays null forever.
- A collider with **no `Rigidbody2D` is static geometry** on both engines — that is how you build
  walls.
- **`Input.joystickX` throws when there is no touch state.** The bridge reads
  `input()?.touch.leftJoystick.value.x` and only guards the first hop, so a headless input stub
  needs a `touch: { leftJoystick: { value: { x: 0, y: 0 } } }`. The browser is fine.
- The export's **service worker precaches everything** and uses `skipWaiting` + `clients.claim`,
  so one reload picks up a new build — but the cache name carries the commit, so **commit before
  you export** or the name does not change.
- Keep every tuning number in a labelled block at the top of its script. The feedback loop on a
  prototype is "make it faster", and that should be a one-line diff, not a search.

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

Inside the editor the same step is the `export_build` tool, with `get_build_report` for a run that
outlives the wait.

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

**A build token is scoped to one app slug.** `build_tokens.app_slug` pins it; a token minted for
another game answers every publish with

```
403 forbidden: This build token may only publish for "ultradark".
```

which is deterministic and will not fix itself on a retry. A new game needs its **own** token,
minted at Admin → Builds; the raw value is shown exactly once and only its SHA-256 is kept, so a
lost token is a re-minted token. Revoke rather than delete, so the audit trail survives.

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
---

## Phase 3b — closed testing on DarksGames (the HTML5 build)

The publish API above serves **downloadable native builds**. A web build goes on the site as a
**Game Card** instead, and while a prototype is still being judged that card is for playtesters
only. This is the recipe, in the order it has to happen. It runs as root **on the server**, which
is production — there is no staging box.

### The gate is two layers, and only one of them is a lock

`playtest: true` on the catalogue entry hides the tile, the detail page and the sitemap entry from
everyone without the flag. **That is a listing, not a lock** — anyone handed the URL can still open
the game. The lock is the game's own server checking the `playtester` claim on a hub access token.
Build both or you have built neither.

The claim is minted by dg-accounts only when the flag is set, so its absence is a plain "no" and
there is nothing to look up. Revoking takes effect within one access-token lifetime.

### The trap that makes the lock useless

`add-game` writes an nginx vhost whose `try_files $uri $uri/index.html @node` serves **anything
that exists in the game's `public/` straight off disk**, never touching Node. Put the export there
and the gate is decoration.

So the build does not live in `public/`. Layout:

```
/srv/darksgames/games/<slug>/
  public/index.html     the gate: account SDK -> token -> POST /api/session
  game/                 the export from `node html5/tools/export.js <project> --pwa`
  server/auth/dgVerify.js   copied from snerf; add `playtester: claims.playtester === true`
  server.js             serves /play/* from game/ only for a request carrying the session cookie
  .env                  your session secret; add-game merges PORT= into it
```

`/play/…` exists nowhere on disk under `public/`, so every request for it falls through to Node,
which is the whole point.

### The order

```bash
# 1. the build
node html5/tools/export.js Games/<Name> --pwa

# 2. the service (copy an existing gated game; dgVerify.js is zero-dependency)
mkdir -p /srv/darksgames/games/<slug>/{public,game,server/auth}
cp -r Games/<Name>/dist/Web/. /srv/darksgames/games/<slug>/game/
printf 'SESSION_SECRET=%s\n' "$(openssl rand -hex 32)" > /srv/darksgames/games/<slug>/.env
chmod 600 /srv/darksgames/games/<slug>/.env
chown -R darks:darks /srv/darksgames/games/<slug>

# 3. the audience — a token's `aud` must be 'hub' or an ENABLED app slug, or the
#    hub refuses to mint one. Add the row to dg-accounts' apps table.

# 4. the subdomain: *.darksgames.app is a DNS wildcard, so this just works
add-game <slug>.darksgames.app <slug>

# 5. the card: add the entry to site/games.js with `playtest: true`, bump
#    VERSION in site/sw.js and the ?v= tags in site/index.html AND site/link.html
#    together, prerender, then rsync site/ to /srv/darksgames/site/
node tools/prerender.mjs
```

Grant a tester the flag at **Admin -> Users -> Grant playtester**.

### Things that will bite

- **Re-read the live `sw.js` before bumping.** Another session may have bumped it since you last
  looked; `tools/prerender.mjs` fails on drift between `VERSION` and the `?v=` tags, and
  `link.html` is not covered by that check — sed it by hand.
- **`--delete` on the site rsync.** The live web root can hold files that are not in the repo.
  Dry-run first and read what it wants to remove.
- **A playtest route still needs a prerendered file** — a tester who reloads the detail page meets
  nginx before the SPA, and no file is a real 404. It must not carry the copy, though: the
  prerenderer writes a blank `noindex` shell for a `playtest: true` entry and lets `app.js` render
  the real view once it has checked the claim.
- **certbot serialises.** "Another instance of Certbot is already running" means a renewal held the
  lock; everything else in `add-game` finished, so just re-run
  `certbot --nginx --redirect -d <domain>`.
- **A service worker outlives the session.** The export precaches the whole build, so a tester
  whose cookie lapses can still play offline from cache. Fine for closed testing; not a reason to
  treat the cookie as a licence check.

## Working inside the editor

When the `sexybiscuit` MCP server is connected, the editor process is already running this code.

- **Start with `get_context`** — the whole state in about a hundred tokens. Ask for more only when
  the task needs it: `get_scene_summary` for the actor list, `get_actor` for one actor,
  `get_project_info` for folders and templates.
- **Edit in batches.** `apply_scene_edits` runs a list of edits in one call and one undo step;
  `spawn_many` places repeated geometry. Scene edits go through the tools, never by editing
  `.scene` files, so the editor, undo and the viewport stay in step.
- **Verify at milestones, not per edit.** `run_scene_report` plays the scene and reports frames,
  fps and script errors; one `capture_viewport` at 640 px is enough to judge a scene.
- **`run_tests`** runs the engine, template, html5 or lint suite and returns totals and failing
  names rather than a log.
- Engine source changes need `rebuild_engine_and_restart`; game code changes need
  `reload_game_code`.
- Tool results are short on purpose. Trust them rather than re-reading an actor after every change.

`wiki/25-ai-assistant-mcp.md` is the generated tool catalogue.

---

## Reuse before writing: the CookieJar

Before writing a common mechanic from scratch, look in `CookieJar/` — a library of reusable
modules, each carrying its own `AGENT.md` saying how to wire it up. In the editor that is
`search_cookies` and `install_cookie`; from a terminal it is a folder to read. When a task
produces something a second game would want, bake it back with `bake_cookie`.

**Check the `engines` field first.** A cookie is `["csharp"]`, `["js"]` or both, and a C# cookie is
no use in phase 1 — which is where every game starts, so that is the filter that matters. The
JavaScript ones are:

| Cookie | What it gives you |
|---|---|
| `noise-and-hearing` | enemies that hunt by ear: a sound is a position and a radius, and they walk to where it *was* |
| `floating-status-bars` | meters in world space above an actor's head — the answer to having no viewport |
| `day-night-cycle` | a clock publishing a 0–1 darkness curve and a day number, plus the overlay that dims the world |
| `wave-director` | endless escalating waves and a boss cadence, where a wave is a *budget* rather than a headcount — plus the stall-breaker, without which one rooted enemy in a corner is a wave that never ends |
| `entity-ledger` | forty enemies in one script instead of forty script engines — and the four rules that make a ledger safe: removal swaps, iterate backwards, a cascade must not recurse, and colliders exist so projectiles can find what they hit |
| `stacking-upgrades` | upgrades that stack, recomputed from scratch every time and never deduplicated, with the effects as a data table so the cards read what the maths reads |
| `pickup-drops` | coins and hearts on the floor: pooled, blinking before they expire, magnetised by an upgrade the collector holds, collected with one call |
| `projectile-pool` | hundreds of projectiles in one script over a pool, swept so a fast one cannot step over its target between frames |
| `draft-picker` | one of N cards between rounds, in world space and said in colour and pips, because the contract has neither a viewport nor a font |

A cookie summary costs about thirty tokens. Deriving the same module again costs thousands, in
every game that needs it. See `wiki/28-the-cookiejar.md`.

Baking one back is the other half of the deal: a mechanic a second game would want is worth
generalising while it is still fresh. For a `js` cookie there is nothing to compile, so the check
is the validator — drop its scripts into a throwaway project, wire them up exactly as its
`AGENT.md` says to, and run `npm run validate -- <dir> --strict`. A cookie whose own documentation
does not validate is worse than no cookie.

---

## The one thing no gate can see

Every check in this repository reads text. The validator reads the scripting contract, `npm test`
and `dotnet test` read behaviour, a headless soak reads script errors. **Not one of them can see a
picture**, and the rule further up — "the validator and the tests are the checker, not
screenshots" — is about cost, not about coverage. It buys cheap iteration; it does not tell you
the game is visible.

Two defects shipped through a fully green board on 2026-09-06, both invisible to every gate:

- The browser's SpriteRenderer draws a tinted box when an actor has no texture, which is what
  makes a template visible before it has art. **C# had no such property and no such box**: it
  returned early on a null texture, so a native build of any bundled template was an empty
  cornflower-blue window. Fixed, and pinned by `ComponentSchemaParityTests`.
- A game laid out against the wrong `layerDepth` direction drew its ground over the whole city, in
  a build where every script ran, every test passed and the validator said `OK`.

So: **look at one frame before you believe a build.** On a headless box that costs nothing and
needs no browser and no screenshot tool —

```bash
Xvfb :99 -screen 0 1280x720x24 -fbdir /tmp/fb &     # -fbdir maps the framebuffer to a file
DISPLAY=:99 ./YourGame &                            # let it run ten seconds
python3 -c "from PIL import Image; from collections import Counter; \
raw=open('/tmp/fb/Xvfb_screen0','rb').read(); \
img=Image.frombytes('RGBA',(1280,720),raw[:1280*720*4],'raw','BGRA').convert('RGB'); \
img.save('/tmp/shot.png'); print(Counter(img.getdata()).most_common(3))"
```

One number tells you most of it: if a single colour is 99% of the screen, nothing is drawing, or
one thing is drawing over everything. `(100, 149, 237)` is MonoGame's default clear — that is an
empty window, not a dark game.

### A bare collider is scenery, and scenery must not fall

Every template builds its world out of actors carrying a SpriteRenderer and a collider and nothing
else — walls, crates, parked cars — and their comments call that "static geometry on both engines".

The browser makes it true by never integrating a collider that has no body. This engine
auto-created a `Rigidbody2D`, and a fresh one is **Dynamic**, so in a native build all of that
scenery accelerated off the bottom of the screen. The player and the enemies looked fine, which is
what made it confusing: their scripts set a velocity every frame, and that overwrites an
accumulating fall.

`GravityScale = 0` is not the fix — Aether has no per-body gravity and the property is stored for
game logic only. The auto-created body is now kinematic, which Aether does respect
(`BareColliderTests`).

### layerDepth sorts on both engines, and higher is nearer

Both engines draw **ascending, so the highest depth is in front**. MonoGame's constant names
mislead here: `BackToFront` draws the *highest* depth first, which lands it at the *back* — the
exact inverse of the browser — so `RenderSystem2D` opens its batch with **`FrontToBack`**.

This section used to say the native renderer did not sort at all. That was true, it was fixed, and
the note outlived the fix by a day, during which it was telling people to **create their actors
back-to-front** instead. That is not merely stale, it is unusable advice: a game that sorts by
*position* — anything top-down where you walk in front of one wall and behind the next — cannot be
laid out in creation order, because an actor is created once and the relationship changes with
every step.

Re-measured with `Games/DepthProbe` on a native Linux build, 2026-09-07: a red sprite created
FIRST at depth 0.90 fills the window over a blue one created after it at 0.10.
`spriteSortMode.test.js` now reads the sort mode out of `RenderSystem2D.cs` and fails on anything
but `FrontToBack`. If you ever doubt it again, run the probe — it takes four minutes.

### 2.5D: one rule, and a pivot

A flat top-down game gives each *kind* of thing a fixed depth, and reads as a diagram. The fix is
one rule: things that lie flat keep a band, things that **stand up** sort by where their feet are.

Height is a `Pivot`, never a position. One sprite sized `[w, h + height]` with a pivot that hangs
it upward from the footprint's south edge is the whole extrusion; the actor, and therefore its
collider, never moves. Lift the actor instead and you walk straight through the wall you can see.

`Pivot` is not listed in `bridge-api.json`, and does not need to be: `Scene.addComponent` matches
property-bag keys case-insensitively against the component's schema on both engines, so **any**
serialised property is settable from script.

Three failures this shape produces, none of which any gate catches:

- Anything that subdivides or measures a standing object must read its **collider**, not its
  sprite. The sprite is the extruded face and is taller than the thing is deep, so rubble from a
  shattered wall scatters up the screen into the air above the hole.
- A top face is a separate actor with no collider and no script, so nothing on it can notice that
  its wall has gone. Every hole leaves a roof hanging over it unless caps are tracked.
- Full-screen overlays parked at a comfortable-looking `0.8` are now in the **middle** of the
  standing band, so half the map sorts over the top of the dark and stands in full daylight at
  night.

The `depth-2-5d`, `breakable-walls` and `top-down-driving` cookies carry all of this.

### The renderer culls, and the cost of 2.5D is fill rate

`SpriteRenderer` skips anything that provably cannot touch the viewport; a pass with no camera
(a tool, a test, an identity transform) culls nothing. The bound is the distance to the furthest
**corner** and respects the pivot — measuring from the centre culls an extruded wall while the
visible half of it is still on screen, which reads as flickering geometry rather than as a bug in
culling.

Do not expect culling to pay for height. On the Jake01 city under Xvfb software rendering it moved
9,462 actors from 38 fps to 45, against 51-65 for the same map flat. Most of what 2.5D costs is
**fill rate** — a wall face is three times the pixels it was — and fill rate is what a GPU is for.
Trimming actor counts barely touches it.

---

## Gates that must stay green

```bash
dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c Debug -warnaserror
dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj
cd html5 && npm test && npm run lint && npm run validate
```

Both engines' action maps, euler conventions, scripting contract and hook lists are pinned by
tests that read the other side's source, so they fail if the two drift apart. The template smoke
tests run every template's scripts on both engines. A change to the scene format or to a
component's serialised properties has to land on both sides.

Conventions: XML docs on public API, `// ----` section banners, British spelling in prose, tests
named like `ARoundTripPreservesActorIdentity` with a why-comment, scenes destroyed in tests,
`FlushPendingActors()` after every mutation before reading back.

---

## Where the detail lives

| Topic | Page |
|---|---|
| The scripting contract both engines implement | `wiki/11-scripting.md` |
| Building, packaging, publishing, CI | `wiki/18-build-export.md` |
| The editor's MCP tools | `wiki/25-ai-assistant-mcp.md` |
| The HTML5 port | `wiki/26-html5.md`, `html5/README.md` |
| This workflow, and why it is shaped for low token use | `wiki/27-game-factory-workflow.md` |
| The CookieJar | `wiki/28-the-cookiejar.md` |
