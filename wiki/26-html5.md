# 26. The HTML5 Port

`html5/` is a JavaScript port of the engine with a class-based API that mirrors
the C# one and reads the same project files. It is not a converter: a `.scene`
written by either engine opens in the other.

Full detail, including the API mapping table, lives in
[`html5/README.md`](../html5/README.md). This page covers what someone working on
the C# engine needs to know about it.

---

## Running it

```bash
node html5/tools/serve.js
```

- editor — <http://localhost:8080/html5/editor/>
- player — <http://localhost:8080/html5/runtime/>

A server is required even for local files: browsers refuse to load ES modules
from a `file://` path. The server has no dependencies and serves the repository
root, so the editor can open `Templates/` projects directly.

```bash
cd html5
npm test      # 60 tests
npm run lint  # parses every module, checks the shader sources
```

The interop suite reads every `.scene` and `ProjectSettings.json` the repository
ships rather than fixtures. **A change to the C# scene format fails those tests**,
which is the point of them.

---

## What is shared, and what is not

Shared, byte for byte: `*.scene`, `ProjectSettings.json`, `Scripts/*.js`,
`*.sbproject`, action map JSON.

Not shared: anything compiled. A project's C# components cannot load in a
browser, so they resolve to `MissingComponent` — the type name and its property
JSON preserved verbatim and written back untouched. That is what lets the HTML5
editor open, edit and save a scene from a C# project without destroying it.
Actor subclasses degrade the same way, through `MissingActorClass`.

If you add a serialisable property to a component, both engines pick it up
automatically — the C# side by reflection, the JavaScript side from the
component's `static schema`. If you add a whole component type, the JavaScript
side needs its own implementation, or scenes using it will carry it as a
placeholder.

---

## Exporting a project to the web

`BuildPlatform.Web` stages `html5/src` and `html5/runtime` into the output as
`engine/`, then writes an `index.html` pointing at the project's own files:

```bash
dotnet run --project SexyBiscuit.Engine -- --export --platform web
```

or pick **Web** in the editor's Build Settings panel. The result is static files;
serve them from anything. It will not run from `file://`.

`Web` is deliberately the last value in `BuildPlatform`: the editor's platform
dropdown maps its selection by ordinal, so inserting a value anywhere else would
silently retarget every project that had one selected.

---

## Things a C# change can break

**Scene format.** The JavaScript writer emits the canonical flat-array form —
`position: [x, y]`, `rotation3: [x, y, z, w]`, `Color` as `#RRGGBBAA`, enums by
name, PascalCase property keys. The reader also accepts the hand-authored
`transform` / `transform3d` object form and `{R,G,B,A}` colours, because every
bundled template uses those. Changing what `SceneSerializer` writes means
changing `html5/src/scene/SceneSerializer.js` to match.

**Component and property names.** A scene names a component by its bare
`Type.Name`. Renaming a C# component renames it in every scene file, and the
JavaScript class has to follow.

**Byte order marks.** `Encoding.UTF8` emits one, and `JSON.parse` rejects it
outright. The JavaScript loader strips a leading U+FEFF from everything it reads;
if you add a writer that emits one, nothing breaks, but prefer
`new UTF8Encoding(false)` for anything a browser parses directly.

---

## Three bugs this port found in the C# engine

All three are fixed, and pinned by tests on both sides.

### `Transform3D.QuaternionToEuler` was not the inverse of `EulerToQuaternion`

`EulerToQuaternion` composes through `Quaternion.CreateFromYawPitchRoll`, which
is `Ry · Rx · Rz` — the YXZ convention, in which *pitch* is the constrained
middle axis. `QuaternionToEuler` applied the standard ZYX aerospace formula,
which puts `asin` on *yaw*. They were not inverses.

They agree whenever one of the three angles is zero, which covers the presets,
every bundled template, and most hand-authored rotations — which is why it went
unnoticed for so long. With all three non-zero it failed: (10, −170, 25) degrees
came back as (−166.75, −4.88, 155.31), a **different rotation** rather than
another spelling of the same one.

Everything that reads `EulerAngles`, changes one axis and writes it back was
affected: `CameraControllers`, `Character`'s orient-to-movement yaw,
`NavMeshAgent`, `MovementComponents`, `CameraShake`'s roll, and the inspector's
rotation field — which had its own copy of the same formula, and now forwards to
the engine's. Scene files were never affected: they store `rotation3` as a
quaternion.

The extraction now matches the composition:

```
pitch = asin(clamp(2(wx − yz), −1, 1))
roll  = atan2(2(xy + wz), 1 − 2(x² + z²))
yaw   = atan2(2(xz + wy), 1 − 2(x² + y²))
```

with roll pinned to zero and yaw taking the whole turn when |pitch| approaches
90°, where the two are not separable. `CoreTests.TransformTests` covers the
round trip, the poles, and repeated single-axis edits.

### `ActionMap` had no touch device

`InputBinding.Device` documented `"keyboard" | "mouse" | "gamepad" | "touch"`,
and `InputManager` implemented the first three. Touch existed —
`Input/TouchState.cs` has touch points, a virtual joystick and pinch — but was
reachable only through `Input.Touch` directly, so **no action could be driven by
a thumbstick**. Every action-map-driven game was unplayable on a phone without
bypassing the map, and with it rebinding and gamepad support.

`InputManager` now evaluates `"touch"` bindings. The axes are
`LeftJoystickX`, `LeftJoystickY`, `RightJoystickX`, `RightJoystickY` and
`PinchDelta`; a binding with no axis reads as a tap anywhere on the screen, so
`IsPressed` / `IsHeld` / `IsReleased` work on it like a button.

`VirtualJoystick` gained a `Side` (`Left`, `Right`, `Any`) and `TouchManager` a
`RightJoystick`, so a player can move and look at once — opposite halves can
never fight over the same finger. `ActionMap.Default()` binds `MoveX`, `MoveY`,
`CameraX` and `CameraY` to the sticks, so the default map is playable on a phone
as it stands.

### `TouchManager.PinchDelta` was always zero

Found while wiring the pinch axis up. It compared the current distance between
two fingers against a previous-position table that the same method had already
advanced to the current positions, so it measured the distance against itself.
The previous positions now come from each touch's own `Delta`.

## Where the JavaScript bridge is wider than the C# one

`html5/src/scripting/ScriptBridge.js` implements everything
`Scripting/TypeScriptDefinitions.cs` declares, and more. The additions are the
API the forty-five bundled template scripts already assume and do not get: `log()`
as a bare global, `Input.isKeyHeld` / `isKeyPressed` / `isMouseHeld` /
`scrollDelta`, `actor.getComponent`, `actor.transform`, `actor.transform3d`, the
`Stay` and `Exit` collision and trigger hooks, and `Physics` and `Time` globals.

Those scripts run under the HTML5 runtime and still do not run under Jint. See
[11. JavaScript Scripting](11-scripting.md) for what the C# bridge actually
provides, and `wiki/21-gotchas.md` for the list of what breaks.
