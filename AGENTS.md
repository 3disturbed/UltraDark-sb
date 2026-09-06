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
| 2. Native build | a Mac (or CI) | the .NET 8 SDK and this engine checkout |
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

- **Do not read the engine source to write a game.** Read `wiki/11-scripting.md` and the
  template's own scripts. The engine is over 50,000 lines of C#; putting it in context is the
  single most expensive thing a session can do.
- **The validator and the tests are the checker, not screenshots.** A validator line costs about
  fifty tokens; a screenshot costs several hundred, and a screenshot loop costs many turns.
- **One game per session, opening with the brief**, not with a scene dump.
- Feedback comes back as text — an issue, or `Games/<Name>/FEEDBACK.md` — and the next round is a
  fresh session.

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

**Build every target with the `sbengine` CLI:**

```bash
dotnet build SexyBiscuit.Build/SexyBiscuit.Build.csproj -c Release
dotnet run --no-build --project SexyBiscuit.Build -c Release -- \
  --project "Games/<Name>" --all --quiet
```

`--all` is web, win-x64, osx-arm64 and linux-x64. `--platform <name>` picks one; it accepts RIDs
and `BuildSettings` platform names. Each desktop target is published self-contained and
single-file, with its native libraries beside the binary, then archived: `.zip` for Windows and
web, `.tar.gz` for macOS and Linux so the executable bit survives.

Useful flags: `--config debug|development|release`, `--output <dir>`, `--version <v>`,
`--no-publish` (stage only, no `dotnet publish`), `--no-zip`, `--report <path>`, `--quiet`.

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
| `--hidden` | upload but leave it unlisted, to be made live from the site's admin page |
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

A cookie summary costs about thirty tokens. Deriving the same module again costs thousands, in
every game that needs it. See `wiki/28-the-cookiejar.md`.

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
