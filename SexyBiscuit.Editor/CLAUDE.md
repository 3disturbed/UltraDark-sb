# SexyBiscuit.Editor — the ImGui editor, the MCP host and C# hot reload

`Panels/` the ImGui panels. `Assistant/` the embedded Claude Code session, `McpHost` (wires the
engine's MCP server to editor state) and `EditorTools` (the editor-only tools: context, project,
selection, camera, capture, play mode, console). `GameCode/` the C# game project host, hot reload,
`GameCodeTools`, `ShippingTools` and rebuild-and-restart. `CookieJar/` the jar panel.
`EditorApp.cs` is the composition root: wiring only; behaviour goes in the panel or tool class.

## Gate for this folder

    dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
    dotnet test SexyBiscuit.Editor.Tests/SexyBiscuit.Editor.Tests.csproj
    dotnet SexyBiscuit.Editor/bin/Debug/net8.0/SexyBiscuit.Editor.dll --dump-mcp-tools --all --budget 41000 > /dev/null

The editor uses the engine's public API only. `SexyBiscuit.Tests` references the engine, so the
tools that live here are covered by `SexyBiscuit.Editor.Tests`, which builds the real catalogue
through `AssistantSelfTest.BuildCatalogue` without opening a window.

## Rules that apply here only

- A tool is `[McpTool]` with a description under 120 characters, a stub echo rather than a view,
  and its three flags set right: `Mutating` means it edits the scene (snapshot undo first),
  `Destructive` warns a client, and `ReadOnly` claims it changes **nothing** — no scene, no file,
  no process, no upload. `ReadOnly` is opt-in; leaving it off is always safe. Then regenerate
  `wiki/25-ai-assistant-mcp.md` and re-run the budget dump: every agent pays for the catalogue
  once per session.
- The running editor does not see an engine change until `rebuild_engine_and_restart`.
- Twin: `html5/editor/` is the browser editor (a subset); a scene-format change reaches both.
