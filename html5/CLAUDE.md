# html5/ — the JavaScript engine, editor, player and tools

The browser port of the engine: zero npm dependencies, ES modules, node 22+. It reads the same
project files as the C# engine (`.scene`, `ProjectSettings.json`, `Scripts/*.js`) byte for byte.

`src/` is the runtime (core, scene, rendering, ui, scripting, net, dg, physics, chibi, input, math,
gameplay, animation, assets, audio, save, debug); `editor/` the browser editor; `runtime/` the
player page and the export templates; `tools/` the node CLIs (validate, serve, export, upload,
roomserver, usage, check); `tests/` the suite.

## Gate for this folder (run from html5/)

    npm test                                          # the whole suite, about three seconds
    npm run lint                                      # parses every module, checks the shaders
    npm run validate                                  # every template loads, compiles, stays in the contract
    node --test tests/ui*.test.js                     # one area only
    node --test --test-name-pattern "<text>" tests/   # one test by name

## Every runtime file has a twin

`src/<area>/X.js` mirrors `SexyBiscuit.Engine/<Area>/X.cs`. A serialised property, a hook, a wire
frame or a scripting global changed here is changed there in the same commit, and the parity tests
on both sides (`tests/interop.test.js`, `tests/bridge.test.js`, `SexyBiscuit.Tests/*Parity*`) fail
until it is. The six JSON tables under `src/` (font5x7, graphics-presets, graphics-menu,
sky-gradient, chibi-parts, chibi-clips) are embedded into the C# assembly by its csproj, so editing
one is a both-engines change with no C# edit. Tests that read C# source pin; never skip them.

## Rules that apply here only

- No dependencies. A `package.json` dependency is a design question, not a convenience.
- The scripting contract is `src/scripting/bridge-api.json`; `src/scripting/ScriptBridge.js`
  implements it member for member. Do not widen the bridge for one game.
- `tools/*.js` print one line per problem and `OK`; agents read that output, keep it short.
- Reference: `README.md` here (every tool and its flags, the API mapping), `wiki/26-html5.md`,
  `wiki/11-scripting.md`.
