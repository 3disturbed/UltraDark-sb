---
paths:
  - "SexyBiscuit.Engine/Scripting/**"
  - "html5/src/scripting/**"
  - "html5/tools/validate.js"
---
# The scripting contract: one list, two implementations

`html5/src/scripting/bridge-api.json` names every global, member and hook a script may use.
`ScriptBridge.cs` (Jint) and `ScriptBridge.js` (browser) implement it member for member, and
`ScriptBridgeParityTests` and `bridge.test.js` fail when either drifts.

- Add the member to the JSON first, then to both bridges, in one commit. Never widen one side.
- `TypeScriptDefinitions.cs` describes the same contract for editors and for the MCP scripting
  resource; a member without a declaration is a member an agent cannot see. Keep it in step.
- Gate: `cd html5 && npm test`, then
  `dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~Parity"`.
- Then `wiki/11-scripting.md`, which is what game sessions read instead of the source.
