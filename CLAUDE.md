# SexyBiscuit — orientation for Claude

A C#/.NET 8 game engine (MonoGame DesktopGL) with an ImGui editor, and a JavaScript port of the
same engine under `html5/` that reads the same project files. Actors carry Components; a Scene
holds Layers of Actors; GameMode, PlayerController and Character give an Unreal-style gameplay
layer. Game logic is JavaScript in `Scripts/*.js`, written against a contract both engines
implement, so a script that runs in the browser runs natively unchanged.

**The workflow (prototype in HTML5, build natively, publish to DarksGames) is
[`AGENTS.md`](AGENTS.md).** Each folder below has its own `CLAUDE.md`, loaded when you work
there, with that folder's gate and rules; `.claude/rules/` names the files that are twins.

| Folder | What | Gate |
|---|---|---|
| `SexyBiscuit.Engine/` | the C# runtime, no editor code | `dotnet build ... -c Debug -warnaserror`; `dotnet test ... --filter "FullyQualifiedName~<Area>"` |
| `SexyBiscuit.Editor/` | the editor, the MCP host, C# hot reload | `dotnet build`; `--dump-mcp-tools --all --budget 60000` |
| `SexyBiscuit.Tests/` | xunit, engine only | `dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj` |
| `html5/` | the JavaScript engine, editor, player and tools | `cd html5 && npm test && npm run lint && npm run validate` (seconds) |
| `Templates/`, `CookieJar/`, `Games/` | starter projects, reusable modules, engine probes | their `CLAUDE.md` |
| `SexyBiscuit.Build/` | `sbengine`, one Main over `Engine/Build` | `dotnet run --project SexyBiscuit.Build -- --project Games/<Name> --all` |
| `wiki/` | the reference, one page per topic | read the page, never the folder |

## Rules that cross folders

- **One contract, two engines.** A scripting member, a serialised property, a hook, a wire frame
  or a shared table changes on both sides in the same commit; `/mirrors.json` names the twins,
  `npm run mirror` fails a one-sided change, and the parity tests on both sides hold them
  together. The scripting API is edited in `bridge-api.json` first, then `npm run gen`, then both
  bridges. Never widen one bridge for one game.
- **One commit per concern.** A commit touches one folder's area, or one mirror pair with its
  parity test. No consolidate commits; split with `git add -p`.
- **Run the folder's gate before every commit.** After a push,
  `gh run list --workflow ci.yml --branch main --limit 1`; never start the next task on a red
  main. Windows and macOS run nightly and on release tags, not per push.
- **Do not read the engine source to write a game.** Read `wiki/11-scripting.md` and the
  template's own scripts; the validator and the tests are the checker, not screenshots.
- **Reuse before writing.** Look in `CookieJar/` before any common mechanic (a summary costs about
  thirty tokens); bake back what a second game would want.
- **In the editor** (the `sexybiscuit` MCP server is connected): start with `get_context`, batch
  with `apply_scene_edits`, verify at milestones with `run_scene_report`, trust the short results.
  Engine changes need `rebuild_engine_and_restart`; game code needs `reload_game_code`.
- If `/srv/darksgames` exists you are on the fleet box, which is production: read
  `ops/fleet-box.md` before any build or deploy.
- `README.md` is the design tour and may run ahead of the code; `wiki/` describes what the code
  does today.

Conventions: XML docs on public API, `// ----` section banners, British spelling in prose, tests
named like `ARoundTripPreservesActorIdentity` with a why-comment, scenes destroyed in tests,
`FlushPendingActors()` after every mutation before reading back.
