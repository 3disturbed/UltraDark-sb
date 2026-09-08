# SexyBiscuit.Editor — the ImGui editor, the MCP host and C# hot reload

`Panels/` the ImGui panels. `Assistant/` the embedded Claude Code session, `McpHost` (wires the
engine's MCP server to editor state) and `EditorTools` (the editor-only tools: context, project,
selection, camera, capture, play mode, console). `GameCode/` the C# game project host, hot reload,
`GameCodeTools`, `ShippingTools` and rebuild-and-restart. `CookieJar/` the jar panel.
`EditorApp.cs` is the composition root: wiring only; behaviour goes in the panel or tool class.

## Gate for this folder

    dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj
    dotnet SexyBiscuit.Editor/bin/Debug/net8.0/SexyBiscuit.Editor.dll --dump-mcp-tools --all --budget 60000 > /dev/null
    dotnet SexyBiscuit.Editor/bin/Debug/net8.0/SexyBiscuit.Editor.dll --assistant-selftest --dry-run

The editor uses the engine's public API only. The tests project does not reference the editor, so
a tool that lives here has no test unless you write one against the headless server
(`AssistantSelfTest.BuildHeadlessServer`).

## Rules that apply here only

- A tool is `[McpTool]` with a description under 120 characters, a stub echo rather than a view,
  and `Mutating`/`Destructive` set right. Then regenerate `wiki/25-ai-assistant-mcp.md` and re-run
  the budget dump: every agent pays for the catalogue once per session.
- The running editor does not see an engine change until `rebuild_engine_and_restart`.
- Twin: `html5/editor/` is the browser editor (a subset); a scene-format change reaches both.
