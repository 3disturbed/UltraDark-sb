# SexyBiscuit HTML5

A JavaScript port of the SexyBiscuit engine, with a class-based API that mirrors
the C# one and reads the same project files. A `.scene` written by either engine
opens in the other; a project is shared rather than converted.

Runs on desktop, iOS and Android browsers. No build step, no dependencies: plain
ES modules served over HTTP.

---

## Running it

```bash
node html5/tools/serve.js
```

Then open:

- **editor** — <http://localhost:8080/html5/editor/>
- **player** — <http://localhost:8080/html5/runtime/>

On a phone, use the machine's LAN address in place of `localhost`. The server has
no dependencies and serves the whole repository, so the editor can open the
projects under `Templates/` directly.

A server is not optional even for local files: browsers refuse to load ES modules
from a `file://` path.

```bash
cd html5
npm test     # 59 tests, node --test
npm run lint # parses every module, checks the shader sources
```

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

**It is deliberately a superset.** The C# bridge exposes seven globals and no
`getComponent`, which is why none of the forty-five bundled template scripts have
ever run: they call `log()`, `Input.isKeyHeld`, `actor.getComponent` and
`actor.transform`, none of which exist there. All of those work here, along with
`onCollisionStay` / `onCollisionExit` / `onTriggerStay` / `onTriggerExit`
(dispatched, where the C# `ScriptComponent` forwards only the two `Enter` hooks),
a `Physics` global, a `Time` global, and `actor.transform3d`. A script written
against the narrower documented surface is unaffected.

One shim is worth knowing about: the collision payload carries `tag` and `name`
forwarding to `other`, so both `data.other.tag` (the documented form) and
`data.tag` (what the templates assume) work.

New scripts can also be ES modules exporting a `Component` subclass, which is the
class-based API this engine prefers.

---

## Input, and what makes it work on a phone

Keyboard, mouse, gamepad and touch, reached through the engine host as
`engine.input`. Browser key codes are normalised to XNA's spelling, so an action
map naming `"LeftShift"` or `"D1"` works in both engines.

`ActionMap` gains a **`touch` device**, so an action can be driven by a virtual
joystick:

```json
{ "device": "touch", "axis": "LeftJoystickX" }
```

The C# `ActionMap` has no touch binding at all — touch is only reachable through
`Input.Touch` directly — which leaves every action-map-driven game unplayable on
a phone. The file stays compatible in both directions: the C# reader ignores a
device it does not know, and this reader loads a file without them unchanged.

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

## Two things found in the C# engine along the way

### `Transform3D.QuaternionToEuler` is not the inverse of `EulerToQuaternion`

`EulerToQuaternion` composes through `Quaternion.CreateFromYawPitchRoll`, which
is `Ry · Rx · Rz`. The extraction applies the standard ZYX aerospace formula,
with `asin` on yaw. They agree only when one of the three angles is zero. A
rotation of (10, −170, 25) degrees comes back as a *different rotation*, not a
different spelling of the same one.

This port implements the true YXZ inverse, which round-trips to 3 × 10⁻¹³ degrees
across the whole range, and keeps the C# behaviour available as
`Quaternion.toEulerCSharp` for a tool that needs to reproduce what the C# editor
shows. Nothing shared changes: scene files store `rotation3` as a quaternion.

The C# engine is untouched; the bug is still there.

### Light intensity and the missing π

A Lambertian diffuse term divides by π. Without a matching π in the radiance, an
intensity of 1 lights a facing white surface to about a third of full brightness,
and every scene authored against the C# engine's intensities reads as flat and
washed out. This renderer folds π into the radiance, so "intensity 1" means
"fully lights a surface facing this light" — which is what the numbers in the
shipped scenes assume.

---

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
