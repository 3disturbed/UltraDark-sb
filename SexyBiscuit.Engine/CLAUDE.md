# SexyBiscuit.Engine — the C# runtime (no editor code)

| Folder | Owns |
|---|---|
| `Core` | Actor, Component, Scene, Layer, Transform, Time, coroutines, `EngineHost` |
| `Scene` | `SceneSerializer` (the `.scene` JSON), prefabs, actor presets, templates, streaming |
| `Rendering`, `Physics`, `Input`, `Audio`, `Animation`, `UI`, `Assets`, `Save`, `Localization` | the systems |
| `Gameplay`, `AI` | GameMode, GameState, Pawn, Character, controllers; behaviour trees, navmesh |
| `Scripting` | the Jint runtime and `ScriptBridge`, the C# half of the scripting contract |
| `Networking`, `DarksGames`, `Steam` | multiplayer, accounts and social, Steamworks |
| `Chibi` | MakeChibi characters |
| `Build` | the export pipeline, the publishers and the `sbengine` CLI logic |
| `Code` | C# game projects, the dotnet runner, assembly loading, repo and tool locators |
| `CookieJar` | module manifests, the catalogue, install, uninstall and bake |
| `Mcp` | the MCP server, tool registry, scene tools and the Claude Code plumbing |

## Gate for this folder

    dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c Debug -warnaserror
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~<Area>"   # ~Ui, ~Networking, ~Parity ...
    dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj                                          # everything, about 40 s after the build

## Every runtime file has a twin

`<Area>/X.cs` mirrors `html5/src/<area>/X.js`. The change lands on both sides in one commit, and
the parity tests (`SexyBiscuit.Tests/*Parity*`, `html5/tests/interop.test.js`) hold them together.
Shared data lives as JSON under `html5/src` and is embedded by this csproj; do not restate it in
C#. `Scripting/TypeScriptDefinitions.cs` describes the contract in
`html5/src/scripting/bridge-api.json` and must follow it.

## Rules that apply here only

- Zero warnings: CI builds with `-warnaserror`. XML docs on public API. `// ----` section banners.
- `FlushPendingActors()` after a mutation before reading back; destroy the scenes a test creates.
- Reach services through `EngineHost.Current`; gate gameplay on `PlayMode.IsActive`.
- Networking and DarksGames are one wire and one contract with the browser: read
  `wiki/15-networking.md` and `wiki/29-darksgames.md` before touching `Networking/` or `DarksGames/`.
- A new subsystem gets a folder, a namespace and a wiki page (`CONTRIBUTING.md`).
