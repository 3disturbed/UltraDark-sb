# SexyBiscuit — orientation for Claude

SexyBiscuit is a C#/.NET 8 game engine (MonoGame DesktopGL) with an ImGui editor. Actors carry
Components; a Scene holds Layers of Actors; GameMode, PlayerController and Character give an
Unreal-style gameplay layer. Scenes are JSON (`.scene`) written by `SceneSerializer`.

Projects: `SexyBiscuit.Engine` (runtime, no editor code), `SexyBiscuit.Editor` (the editor, MCP
host, C# hot reload), `SexyBiscuit.Tests` (xunit, engine only), `SexyBiscuit.Demo`,
`SexyBiscuit.Build` (the `sbengine` CLI over the engine's export pipeline). `html5/` is a
JavaScript port of the engine with its own editor and player; it reads the same project files, so
a change to the scene format or a component's serialised properties has to land on both sides.

Build and test from the repository root:

    dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c Debug -warnaserror
    dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj
    dotnet run --project SexyBiscuit.Build -- --project Games/<Name> --all [--upload]   # sbengine: web + desktop builds, report, upload

For the HTML5 port, from `html5/` (node 22+, no dependencies):

    npm test                                  # node --test; reads the real Templates/ and C# sources
    npm run lint                              # parses every module and checks the shader sources
    npm run validate -- <projectDir>          # scenes load, scripts compile, scripts stay in the contract
    node tools/serve.js --watch               # editor at /html5/editor/, player at /html5/runtime/, live reload
    node tools/export.js <projectDir> --pwa   # a static, installable web build and its zip
    node tools/upload.js <zip>                # POST to $SB_UPLOAD_URL with $SB_UPLOAD_TOKEN

Conventions: XML docs on public API, `// ----` section banners, British spelling in prose, tests
named like `ARoundTripPreservesActorIdentity` with a why-comment, scenes destroyed in tests,
`FlushPendingActors()` after every mutation before reading back.

## Making games

Games live under `Games/<Name>/` in a repo seeded from this one (`.claude/skills/new-game`), and
ship with `.claude/skills/ship-game`; the workflow, and why it is shaped for low token use, is
`wiki/27-game-factory-workflow.md`. Game logic is JavaScript in `Scripts/*.js` written against
the **shared scripting contract** (`html5/src/scripting/bridge-api.json`, documented in
`wiki/11-scripting.md`): the Jint bridge and the browser bridge implement it member for member,
so a script that runs in the browser runs natively unchanged. The template smoke tests in both
suites are the gate.

When prototyping with no editor session (the usual case): edit `Scripts/*.js` and `.scene` files
directly, run `npm run validate -- ../Games/<Name>` from `html5/` until it prints OK, and serve
with `--watch`. Do not read the engine source to write a game; read `wiki/11-scripting.md` and the
template's own scripts. No screenshots: the validator and the tests are the checker.

When running inside the editor (the `sexybiscuit` MCP server is connected): the editor process
is already running this code. Start with `get_context`; ask for more only when a task needs it.
Engine changes only take effect after `rebuild_engine_and_restart`; game-project changes after
`reload_game_code`; `run_tests` runs a suite and returns totals and failing names. Scene edits
go through the MCP tools, not by editing `.scene` files, so the editor, undo and the viewport
stay in step; batch them with `apply_scene_edits`. Verify at milestones with `run_scene_report`
or one `capture_viewport`, not after every edit. Ship with `export_build`. Tool results are short
on purpose; trust them. See `wiki/25-ai-assistant-mcp.md` for the tool catalogue.

The HTML5 port is documented in `html5/README.md` and `wiki/26-html5.md`. Both engines' action
maps, euler conventions, scripting contract and hook lists are pinned by tests that read the other
side's source, so they fail if the two drift apart.
