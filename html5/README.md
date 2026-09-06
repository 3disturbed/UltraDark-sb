# SexyBiscuit HTML5

A JavaScript port of the SexyBiscuit engine, with a class-based API that mirrors
the C# one and reads the same project files. A `.scene` written by either engine
opens in the other; a project is shared rather than converted.

Runs on desktop, iOS and Android browsers. No build step, no dependencies: plain
ES modules served over HTTP.

---

## Running it

```bash
node html5/tools/serve.js            # add --watch to reload every open tab on a change
```

Then open:

- **editor** — <http://localhost:8080/html5/editor/>
- **player** — <http://localhost:8080/html5/runtime/>

A project opens by URL: `/html5/runtime/?project=/Games/Foo/&scene=Scenes/Main`. The
server prints the machine's LAN address so a phone can open the same URL. It has no
dependencies and serves the whole repository, so the editor can open the projects
under `Templates/` directly.

A server is not optional even for local files: browsers refuse to load ES modules
from a `file://` path.

```bash
cd html5
npm test           # node --test; reads the real Templates/ and the C# sources
npm run lint       # parses every module, checks the shader sources
npm run validate   # every template, or `-- <projectDir>`: scenes load, scripts compile and stay in the contract
```

---

## The tools

`html5/tools/` is the toolchain a prototype is made with on a machine that has node
(22 or later) and nothing else. Every tool is dependency-free, has a `--help`-shaped
usage line, and is importable — `tests/tools.test.js` runs them end to end.

| Tool | What it does |
|---|---|
| `validate.js [projectDir...] [--strict]` | Loads every scene through the real deserialiser, checks every referenced script exists, compiles every script and holds each one to the shared scripting contract (`src/scripting/bridge-api.json`). One line per problem, `OK` when there are none; `--strict` fails on warnings too. The bundled templates are the default. |
| `serve.js [port] [root] [--watch]` | The static server above. `--watch` injects a two-line reload snippet into every page and reloads when a scene, script, asset or page changes. |
| `export.js <projectDir> [--pwa] [--out <dir>] [--version <v>] [--config <c>] [--no-zip]` | Stages a web build — the runtime, the project's files and the page — into `dist/Web/`, zips it beside the folder and writes `build-report.json`. `--pwa` adds a manifest, icons and a service worker so the build installs to a home screen and runs offline. |
| `upload.js <archive> [--app-slug <s>] [--title <n>] [--version <v>] [--platform windows\|macos\|linux] [--channel alpha\|beta\|demo] [--notes <t>] [--hidden] [--config <BuildSettings.json>] [--dry-run]` | Publishes one **native** archive to DarksGames: a POST whose body is the raw bytes with the metadata in a base64url `X-Build-Meta` header. The token is `$DG_BUILD_TOKEN` or the first line of `~/.sexybiscuit/dg-token`. Prints `publish <platform> <status> <url>`. The C# pipeline sends the identical request. |
| `check.js` | The lint: parses every module and checks the shader sources. |

The exporter and the C# `BuildPlatform.Web` fill the same page templates under
`runtime/export/` (`index.html.tmpl`, `manifest.webmanifest.tmpl`, `sw.js.tmpl`), so the
two exports are one build.

---

## The API

Every class mirrors its C# counterpart, with JavaScript casing. Porting a
component is a matter of lowering the first letter of each override.

```js
import { Actor, Component, Vector2, registerComponent } from './html5/src/index.js';

class Spinner extends Component {
    // What a scene file can hold, what the inspector can edit, and what a tool
    // can list — all from one declaration.
    static schema = {
        degreesPerSecond: { type: 'number', default: 90 },
    };

    constructor() {
        super();
        this.degreesPerSecond = 90;
    }

    update(dt) {
        this.transform.localRotation += this.degreesPerSecond * Math.PI / 180 * dt;
    }
}

registerComponent(Spinner, { category: 'Project', source: 'project' });
```

| C# | JavaScript |
| --- | --- |
| `actor.Transform.Position` | `actor.transform.position` |
| `AddComponent<Rigidbody2D>()` | `addComponent(Rigidbody2D)` or `addComponent('Rigidbody2D')` |
| `GetComponent<T>()` | `getComponent(T)` |
| `protected override void Update(float dt)` | `update(dt)` |
| `[RequireComponent(typeof(X))]` | `static requires = [X]` |
| reflection over public get/set properties | `static schema = { … }` |
| `IEnumerator` + `yield return` | a generator + `yield` |
| `StartCoroutine(Blink())` | `startCoroutine(blink())` |

### Transliterating C# directly

```js
import { installCSharpAliases } from './html5/src/compat/CSharpNaming.js';
installCSharpAliases();

actor.Transform.Position = new Vector2(10, 0);
actor.GetComponent(Rigidbody2D).AddForce(push);
```

Opt-in, because one obvious spelling per member is the better default for new
code. The aliases are non-enumerable, so they never reach a scene file or the
inspector. Call it after your own components have registered.

---

## Sharing projects with the C# engine

The formats are the same files, not an export.

| File | Status |
| --- | --- |
| `*.scene` | Read and written. Both the canonical flat-array form and the hand-authored `transform` / `transform3d` object form load; colours read as `#RRGGBBAA`, `#RGB` or `{R,G,B,A}` and are written as `#RRGGBBAA`. |
| `ProjectSettings.json` | Read. Keys match case-insensitively, so the templates' PascalCase and the demo's camelCase both work, and `appName` aliases `WindowTitle`. |
| `Scripts/*.js` | Run. See the scripting section below. |
| `*.sbproject` | Read for `ProjectName`, `DefaultScene`, asset and script directories. |
| Action map JSON | Read and written, with one addition — see Input. |
| `*.csproj`, `Source/**`, `bin/**` | Not readable in a browser. Components defined there load as placeholders. |

### Components a browser cannot load

A project's behaviour may be written in C# and compiled into an assembly no web
runtime can load. Those component types can never resolve here, so they are kept
as `MissingComponent`: the type name and its property JSON are preserved
verbatim and written back untouched on save.

That is what lets the HTML5 editor open, edit and save a scene belonging to a C#
project without destroying it. The same applies to actor subclasses, which load
as a plain `Actor` carrying a `MissingActorClass` marker. The inspector shows
these explicitly rather than presenting an empty box.

---

## Scripting

Project `.js` files run unchanged. Everything
`Scripting/TypeScriptDefinitions.cs` declares is present with the same names and
shapes: the `actor`, `transform`, `Input`, `Audio`, `Scene`, `Debug` and
`Vector2` globals, and the `onAwake` / `onStart` / `onUpdate` / `onFixedUpdate` /
`onLateUpdate` / `onDestroy` / `onCollisionEnter` / `onTriggerEnter` hooks.

Each script gets its own scope, so two components running the same file keep
separate state — the isolation Jint gets from one engine per component.

**It is the shared contract.** `src/scripting/bridge-api.json` lists every global, member
and hook; `tests/bridge.test.js` holds this bridge to the file and the C# suite's
`ScriptBridgeParityTests` holds the Jint bridge to the same file, in both directions. The
surface includes what the bundled template scripts use — `log()` as a bare global,
`Input.isKeyHeld`, `actor.getComponent`, `actor.transform`, `Scene.createActor` /
`addComponent` / `destroy`, the `Stay` and `Exit` hooks, `Physics`, `Time`, a `Network`
stub — and `tests/templates.test.js` runs every template's scripts for sixty frames, as the
C# suite does under Jint. A script that passes both runs under both engines.

The collision payload carries `tag` and `name` forwarding to `other`, so both
`data.other.tag` and `data.tag` work. A component proxy accepts either spelling of a
property (`rb.gravityScale`, `rb.GravityScale`) and coerces file-form values
(`"#FF0000"`, `[1, 2]`) the way a scene file's are.

New scripts can also be ES modules exporting a `Component` subclass, which is the
class-based API this engine prefers.

---

## Input, and what makes it work on a phone

Keyboard, mouse, gamepad and touch, reached through the engine host as
`engine.input`. Browser key codes are normalised to XNA's spelling, so an action
map naming `"LeftShift"` or `"D1"` works in both engines, and gamepad axes use
the C# names (`LeftX`, `RightY`) so a bindings file moves between them unchanged.

`ActionMap.default()` is the C# default map action for action, so a game written
against the defaults behaves the same under either engine. A test reads both
sources and fails if they drift apart.

Actions can be driven by a virtual joystick on either engine:

```json
{ "device": "touch", "axis": "LeftJoystickX" }
```

`TouchControls` draws the on-screen sticks and buttons as a DOM overlay. It is
only a visual: input still flows through the same action map, so a game reads
`Input.getAxis('MoveX')` and never knows a thumb is driving it.

The player also handles the parts of shipping in a browser that the engine should
not know about: the audio-unlock tap iOS requires, pausing in a background tab,
fullscreen and orientation, safe-area insets, and stopping the page scrolling
under a drag.

---

## What is here, and what is not

**Complete:** the object model and its lifecycle, scene serialisation, 2D
rendering (Canvas2D sprite batch, camera, follow and shake), 3D rendering
(WebGL2 forward renderer with metallic/roughness shading, directional, point and
spot lights, gradient or cubemap sky, frustum culling), 2D physics (SAT impulse
solver with raycasts and overlap queries), 3D physics and a kinematic character
controller, input, the Unreal-shaped gameplay layer, assets, WebAudio, tweening,
coroutines and timers.

**Different by necessity:**

- *2D physics* is a compact SAT impulse solver rather than the C# engine's
  Aether wrapper. Shipping a Box2D build to move rectangles around is a lot of
  bytes for a phone; the component API is the same.
- *3D physics* is axis-aligned rather than BEPU. It covers characters that stand
  on things and do not walk through walls, rigid bodies that fall and rest, and
  raycasts. A rotated box collider is reported as the box that contains it.
- *Models* are read from glTF and GLB only. The `.fbx` and `.obj` files the C#
  engine handles through AssimpNet have no browser-side reader; a renderer that
  cannot load its model falls back to its primitive and says so.
- *Shadows* are not implemented. Lights have `castsShadows` and it is preserved
  on save.

**Not ported:** Steam integration (no browser equivalent), the networking layer
(LiteNetLib is UDP, which browsers cannot open — WebRTC or WebTransport would be
a separate piece of work), C# hot reload, and the MCP server.

---

## Two bugs this port found in the C# engine

Both are now fixed on the C# side too, and pinned by tests in both suites.

### `Transform3D.QuaternionToEuler` was not the inverse of `EulerToQuaternion`

`EulerToQuaternion` composes through `Quaternion.CreateFromYawPitchRoll`, which
is `Ry · Rx · Rz` — the YXZ convention, where *pitch* is the constrained middle
axis. The extraction applied the standard ZYX aerospace formula, with `asin` on
yaw. They agreed only when one of the three angles was zero, which covers the
presets, every bundled template and most hand-authored rotations — and is why it
went unnoticed. With all three non-zero, (10, −170, 25)° came back as
(−166.75, −4.88, 155.31)°: a different rotation, not another spelling of the same
one.

Every read-modify-write of `EulerAngles` was affected — the camera controllers,
the character yaw, `CameraShake`'s roll, the inspector's rotation field. The
inspector had a second copy of the same formula, which now forwards to the
engine's.

Both engines use the true YXZ inverse, which round-trips to 3 × 10⁻¹³ degrees
across the whole range. Scene files never changed: they store `rotation3` as a
quaternion.

### `ActionMap` had no touch device

`InputBinding.Device` documented `"touch"` and nothing implemented it. Touch
existed — touch points, a virtual joystick, pinch — but only through
`Input.Touch` directly, so no action could be driven by a thumbstick and every
action-map-driven game was unplayable on a phone without bypassing the map, and
with it rebinding and gamepad support.

Both engines now bind `LeftJoystickX`, `LeftJoystickY`, `RightJoystickX`,
`RightJoystickY` and `PinchDelta`, have a right-hand stick as well as a left, and
carry touch bindings on `MoveX`, `MoveY`, `CameraX` and `CameraY` in the default
map. A touch binding with no axis reads as a tap anywhere on the screen.

Fixing that turned up a third: `TouchManager.PinchDelta` was always exactly zero.
It compared the current finger distance against a previous-position table that
the same method had already advanced to the current positions.

## Layout

```
html5/
  src/
    core/        Actor, Component, Scene, Layer, Transform, Transform3D, Time,
                 SceneManager, coroutines, timers, the type registry
    math/        Vector2/3/4, Quaternion, Matrix4, Color, Bounds, SBMath
    scene/       SceneSerializer, ActorPresets, SceneTemplates
    rendering/   SpriteBatch, cameras, renderers, materials, lights, shaders
    physics/     2D and 3D colliders, bodies, solvers, character controller
    input/       InputManager, ActionMap, gamepads, touch, on-screen controls
    gameplay/    GameMode, PlayerController, Pawn, Character, states
    scripting/   ScriptComponent and the compatibility bridge
    assets/      AssetManager, Texture2D
    audio/       WebAudio playback and buses
    animation/   Tween and easing
    compat/      Optional PascalCase aliases
  editor/        The editor: panels, state, chrome
  runtime/       The standalone player
  examples/      A worked project
  tools/         Dev server and the module checker
  tests/         node --test suites
```

`src/index.js` is the public API. `src/registerBuiltins.js` is what makes a scene
naming `"SpriteRenderer"` resolve to a class — the serialiser imports it, so
`deserialize` works from any entry point.
