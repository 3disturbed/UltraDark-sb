---
paths:
  - "SexyBiscuit.Engine/Mcp/**"
  - "SexyBiscuit.Editor/Assistant/**"
  - "SexyBiscuit.Editor/GameCode/**"
---
# MCP tools are paid for by every session

The catalogue is loaded once per session by every agent (88 tools, about 18k tokens with `--all`).

- Description under 120 characters; parameter descriptions short; enums in the schema; the prose
  in `wiki/25-ai-assistant-mcp.md`.
- Return a stub (`{id, name, ...}`), not a view; page long lists; totals and failing names, not
  logs (`McpDietTests` pins the shapes).
- `Mutating` and `Destructive` set right: the registry snapshots undo before a mutation.
- After a change: `dotnet SexyBiscuit.Editor/bin/Debug/net8.0/SexyBiscuit.Editor.dll --dump-mcp-tools --all --markdown --budget 60000`
  and regenerate the table in `wiki/25`; the CI budget fails the build over it.
- The editor process runs this code: an engine change needs `rebuild_engine_and_restart`.
