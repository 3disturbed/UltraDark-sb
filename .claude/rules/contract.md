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

- Add the member to the JSON first (kind, shared, params/returns or type, a line of doc), run
  `cd html5 && npm run gen`, then implement it in both bridges, in one commit. Never widen one side.
- `sb-engine.d.ts` is generated from the JSON and embedded by the C# build; never edit it or
  `TypeScriptDefinitions.cs` by hand. `npm run lint` fails on a stale file.
- Gate: `cd html5 && npm test && npm run lint && npm run mirror`, then
  `dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj --filter "FullyQualifiedName~Parity"`.
- Then `wiki/11-scripting.md`, which is what game sessions read instead of the source.
