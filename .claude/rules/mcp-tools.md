---
paths:
  - "SexyBiscuit.Engine/Mcp/**"
  - "SexyBiscuit.Editor/Assistant/**"
  - "SexyBiscuit.Editor/GameCode/**"
---
# MCP tools are paid for by every session

The catalogue is loaded once per session by every agent: 52 tools, 40,400 characters, about 10,100 tokens
with `--all`. CI and `SexyBiscuit.Editor.Tests` both cap it, and the cap only ever goes down.

- One tool per job, not one per argument. 88 tools merged to 52 in 2026-09: a mode goes on the
  tool that already carries the arguments (`spawn_actor` takes `shape` and `preset`, `play_mode`
  takes an `action`), and the absorbed name is deleted rather than aliased — including from
  `apply_scene_edits`, whose ops are dispatched by tool name. `wiki/25` has the map.
- Description under 120 characters; parameter descriptions short; enums in the schema; the prose
  in `wiki/25-ai-assistant-mcp.md`.
- Return a stub (`{id, name, ...}`), not a view; page long lists; totals and failing names, not
  logs (`McpDietTests` pins the shapes).
- The three flags mean different things. `Mutating`: edits the scene, so snapshot undo first.
  `Destructive`: warn before calling. `ReadOnly`: changes nothing anywhere — not the scene, not a
  file, not a process, not a remote service — and it is what a client reads to skip a
  confirmation, so it is opt-in and only ever true for a pure query.
- After a change: `dotnet SexyBiscuit.Editor/bin/Debug/net8.0/SexyBiscuit.Editor.dll --dump-mcp-tools --all --markdown --budget 41000`
  and regenerate the table in `wiki/25`; the CI budget fails the build over it.
- The editor process runs this code: an engine change needs `rebuild_engine_and_restart`.
