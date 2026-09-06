# 27. The Game Factory Workflow

How many small games get made, playtested and shipped with an AI agent doing the typing, and why
each step is shaped the way it is. The measure throughout is **tokens per shipped game**: every
turn of an agent's session resends the whole conversation, so the cost of a game is roughly the
number of round trips times the size of what each one adds. Fewer turns, smaller results, no
rewrites.

## The loop

```
brief ──► /new-game ──► edit Scripts + Scenes ──► npm run validate ──► export --pwa ──► upload
                              ▲                                                          │
                              └──────────────── team feedback (text) ◄───────────────────┘
                                                                                          │
                                              when it is fun: /ship-game ──► desktop binaries + web
```

Two phases, two toolchains:

| Phase | Where | Toolchain | What the agent touches |
|---|---|---|---|
| **Prototype and playtest** | remote, headless, node only | `html5/tools/*` | `Games/<Name>/Scripts/*.js`, `Scenes/*.scene`, `BRIEF.md` |
| **Native port and ship** | a Mac with the editor and .NET | `sbengine` CLI, the editor's MCP tools | build settings, an optional C# layer |

## Phase 1 — a new game, remote and headless

1. **Seed a repo.** Each game is its own repository, cloned from the engine so one commit
   captures the game and the engine it ran on; the engine stays reachable as `upstream`. The
   game lives under `Games/<Name>/`. The `/new-game` skill does this.
2. **Write against the contract.** Game logic is JavaScript in `Scripts/*.js`, attached to
   actors by `ScriptComponent`. The globals a script may use are the shared scripting contract
   ([11. Scripting](11-scripting.md#the-scripting-contract)): the browser bridge and the Jint
   bridge implement it member for member, tests on both sides pin them to
   `html5/src/scripting/bridge-api.json`, and every bundled template runs under both engines.
   That is what makes the later port a build step rather than a rewrite.
3. **Validate instead of looking.**

   ```bash
   cd html5 && npm run validate -- ../Games/<Name>
   ```

   loads every scene through the real deserialiser, checks every referenced script exists,
   compiles every script, and flags any member outside the contract, a `findByTag` treated as
   one actor, or a misspelt hook — one line per problem, `OK` when there are none. It costs an
   agent about fifty tokens to read. `--strict` (warnings fail too) is the setting for a game
   about to ship. `npm test` runs the template smoke tests, which tick every script for sixty
   frames.
4. **Playtest.** `node html5/tools/serve.js --watch` serves the checkout with live reload and
   prints a LAN address; a phone opens
   `http://<lan-ip>:8080/html5/runtime/?project=/Games/<Name>/`. For a build the team keeps:

   ```bash
   node html5/tools/export.js Games/<Name> --pwa       # dist/Web/ + <slug>-<version>-web.zip
   node html5/tools/upload.js Games/<Name>/dist/*-web.zip --game "<Name>"
   ```

   The export is a static site with a manifest and a service worker, so it installs to a
   phone's home screen from the site and runs offline. The uploader POSTs the zip and its
   metadata to `$SB_UPLOAD_URL` with `$SB_UPLOAD_TOKEN` and prints the URL. In CI the same two
   commands run on every push, so the build never enters the agent's context at all.
5. **Feedback comes back as text** — an issue, or `Games/<Name>/FEEDBACK.md` — and the next
   session opens with the brief and the feedback, not with a scene dump.

## Phase 2 — the native port, on request

The validator has held the scripts to the contract since day one, so the game runs in the C#
engine unchanged. On the Mac, `/ship-game` runs the build CLI for every target — the web build
plus self-contained desktop binaries — runs the tests, uploads every artifact and prints one
line per target. A C# layer is added only for a documented reason (a feature the browser lacks,
native performance), through the editor's MCP tools, and the JavaScript stays the source for the
web build. Native mobile is a later engine milestone; the PWA is the mobile build until then.

Inside the editor the same steps are tools that answer in a few lines instead of a log:
`run_tests` (engine, templates, html5 or lint: totals and failing names), `run_scene_report`
(play the scene for a few seconds: frames, fps, script errors, console warnings and errors),
and `export_build` with `get_build_report` (every target, one line each, the archive size and
the upload URL). `get_context` opens a session in about a hundred tokens; `apply_scene_edits`
and `spawn_many` batch scene work into one call and one undo step.

## Token hygiene

The rules the skills and `CLAUDE.md` encode, and the reason for each:

| Rule | Why |
|---|---|
| Never read the engine source to write a game; read [11. Scripting](11-scripting.md) and the template's scripts | 44,000 lines of C# in the context is the single most expensive thing a session can do |
| `validate` and the tests are the checker; no screenshots | a validator line is ~50 tokens, a screenshot 300–800, and a screenshot loop is many turns |
| One game per session; open with the brief | a session's context grows with every turn; a fresh one is cheap |
| Builds run in CI or as one command with a one-line report | a build log is thousands of tokens that are never read |
| In the editor, start with `get_context`, batch edits, trust short results | the MCP tools' results are sized for an agent, not for a log |

## Measuring

The numbers this page rests on are estimates until measured. `npm run usage -- <transcript>`
reads a Claude Code transcript and reports turns, context per call, the cache share and the
tools that returned the most text; the editor writes the same per turn to
`<project>/.sexybiscuit/usage.jsonl`, shows it in the cost tooltip, and answers
`get_session_usage` with it in about seventy tokens. The benchmark is one fixed brief ("Hello
World: add a coin the player collects, ship a web build") run before and after a change.

One fixed cost is already measured: the full tool catalogue is 42,700 characters of compact
JSON (about 10,700 tokens), paid once per session when Claude Code loads the tool set, and
held under 45,000 by `--dump-mcp-tools --all --budget` in CI. The planned tool-surface merge
(about 55 tools with descriptions under 120 characters) would bring it near 20,000.

## Per-game checklist

- [ ] `Games/<Name>/BRIEF.md` says what fun means for this game
- [ ] `npm run validate -- ../Games/<Name>` prints OK; `--strict` before shipping
- [ ] `npm test` green (the template smoke test covers every script)
- [ ] a web build uploaded and the URL in the session's last message
- [ ] feedback captured as text before the next session
- [ ] for the port: `dotnet test` green, one line per target from the build

## Next

- [11. JavaScript Scripting](11-scripting.md) — the contract
- [26. The HTML5 Port](26-html5.md) — the runtime and the tools
- [18. Build & Export](18-build-export.md) — the native pipeline
- [25. AI Assistant & MCP](25-ai-assistant-mcp.md) — editor sessions
