# SexyBiscuit — orientation for Claude

SexyBiscuit is a C#/.NET 8 game engine (MonoGame DesktopGL) with an ImGui editor. Actors carry
Components; a Scene holds Layers of Actors; GameMode, PlayerController and Character give an
Unreal-style gameplay layer. Scenes are JSON (`.scene`) written by `SceneSerializer`.

Projects: `SexyBiscuit.Engine` (runtime, no editor code), `SexyBiscuit.Editor` (the editor, MCP
host, C# hot reload), `SexyBiscuit.Tests` (xunit, engine only), `SexyBiscuit.Demo`. `html5/` is a
JavaScript port of the engine with its own editor and player; it reads the same project files, so
a change to the scene format or a component's serialised properties has to land on both sides.

Build and test from the repository root:

    dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c Debug -warnaserror
    dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj

For the HTML5 port, from `html5/`:

    npm test          # node --test; the interop suite reads the real Templates/ files
    npm run lint      # parses every module and checks the shader sources
    node tools/serve.js   # editor at /html5/editor/, player at /html5/runtime/

Conventions: XML docs on public API, `// ----` section banners, British spelling in prose, tests
named like `ARoundTripPreservesActorIdentity` with a why-comment, scenes destroyed in tests,
`FlushPendingActors()` after every mutation before reading back.

Before writing a common mechanic from scratch, look in the CookieJar: `CookieJar/` in this
repository is a library of reusable modules, and each carries an `AGENT.md` saying how to wire it
up. In the editor that is `search_cookies` and `install_cookie`; from a terminal it is a folder to
read. When a task produces something a second game would want, bake it back (`bake_cookie`, or
Tools in the editor). See `wiki/28-the-cookiejar.md`.

When running inside the editor (the `sexybiscuit` MCP server is connected): the editor process
is already running this code. Engine changes only take effect after `rebuild_engine_and_restart`;
game-project changes after `reload_game_code`. Scene edits go through the MCP tools, not by
editing `.scene` files. See `wiki/25-ai-assistant-mcp.md` for the tool catalogue.

The HTML5 port is documented in `html5/README.md` and `wiki/26-html5.md`. Building it turned up
three bugs since fixed in the C# engine: `Transform3D.QuaternionToEuler` was not the inverse of
`EulerToQuaternion` (ZYX extraction over a YXZ composition — correct only when one angle is zero),
`ActionMap` had no touch device so no action could be driven by a thumbstick, and
`TouchManager.PinchDelta` was always zero. Both engines' action maps and euler conventions are
pinned by tests that read the other side's source, so they fail if the two drift apart.
