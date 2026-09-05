# SexyBiscuit — orientation for Claude

SexyBiscuit is a C#/.NET 8 game engine (MonoGame DesktopGL) with an ImGui editor. Actors carry
Components; a Scene holds Layers of Actors; GameMode, PlayerController and Character give an
Unreal-style gameplay layer. Scenes are JSON (`.scene`) written by `SceneSerializer`.

Projects: `SexyBiscuit.Engine` (runtime, no editor code), `SexyBiscuit.Editor` (the editor, MCP
host, C# hot reload), `SexyBiscuit.Tests` (xunit, engine only), `SexyBiscuit.Demo`.

Build and test from the repository root:

    dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c Debug -warnaserror
    dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj

Conventions: XML docs on public API, `// ----` section banners, British spelling in prose, tests
named like `ARoundTripPreservesActorIdentity` with a why-comment, scenes destroyed in tests,
`FlushPendingActors()` after every mutation before reading back.

When running inside the editor (the `sexybiscuit` MCP server is connected): the editor process
is already running this code. Engine changes only take effect after `rebuild_engine_and_restart`;
game-project changes after `reload_game_code`. Scene edits go through the MCP tools, not by
editing `.scene` files. See `wiki/25-ai-assistant-mcp.md` for the tool catalogue.
