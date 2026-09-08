# SexyBiscuit Engine

> **For agents:** start at [`CLAUDE.md`](CLAUDE.md) and [`AGENTS.md`](AGENTS.md). This file is
> the design tour and may run ahead of the code; [`wiki/`](wiki/README.md) describes what the
> code does today.

An in-house, full-ownership game engine built on MonoGame. No vendor lock-in. No licensing fees. No surprises. Every line of source is ours to read, modify, and ship.

**Platforms:** Windows · Linux · macOS · Android · iOS (planned) · Steam
**Runtime:** MonoGame DesktopGL / DirectX
**Language:** C# engine core · JavaScript gameplay scripting

---

## Core Philosophy

- **Full ownership** — built on MonoGame, every system above it is ours
- **Actor/Component architecture** — Unity-familiar but fully under our control
- **JavaScript scripting** — attach `.js` files to any Actor; no compile step for gameplay logic
- **Almost no Content Pipeline** — PNG, OGG, TTF and FBX all load raw at runtime. Only
  custom shaders need `mgcb`, and the engine runs without them (see [Shaders](SexyBiscuit.Engine/Shaders/README.md))
- **Multiplayer first** — replication and networking designed in from day one, not bolted on
- **2D and 3D first-class** — neither is a second-class citizen or an afterthought

---

## Documentation

Three layers, depending on what you need:

| | For |
|---|---|
| **[Wiki](wiki/README.md)** | Reference — what each class does, written against the source |
| **[Tutorials](tutorials/README.md)** | A guided path from an empty window to a shipped game |
| This README | The tour: what the engine contains and how the pieces relate |

Start with [Getting Started](wiki/01-getting-started.md), then read
[The Game Loop](wiki/03-game-loop.md) before writing a game — it covers what the engine
pumps for you and what your subclass is expected to wire up.

Also: [Shaders](SexyBiscuit.Engine/Shaders/README.md) for building the built-in HLSL
effects, and [CONTRIBUTING](CONTRIBUTING.md) for build, test and style expectations.

---

## Table of Contents

1. [Core Architecture](#1-core-architecture)
2. [JavaScript Scripting](#2-javascript-scripting)
3. [2D Rendering](#3-2d-rendering)
4. [3D Rendering](#4-3d-rendering)
5. [Asset Management](#5-asset-management)
6. [Physics — 2D](#6-physics--2d)
7. [Physics — 3D](#7-physics--3d)
8. [Input](#8-input)
9. [Audio](#9-audio)
10. [UI System](#10-ui-system)
11. [Animation](#11-animation)
12. [Scene Management](#12-scene-management)
13. [Networking / Multiplayer](#13-networking--multiplayer)
14. [Serialisation & Save System](#14-serialisation--save-system)
15. [Visual Editor](#15-visual-editor)
16. [Debug & Tooling](#16-debug--tooling)
17. [Export & Platform Build System](#17-export--platform-build-system)
18. [Steam Integration (Steamworks.NET)](#18-steam-integration-steamworksnet)
19. [Demo Game — Biscuit Chronicles](#19-demo-game--biscuit-chronicles)
20. [Project Templates](#20-project-templates)
21. [Gameplay Framework](#21-gameplay-framework)
22. [AI & Navigation](#22-ai--navigation)
23. [Localisation](#23-localisation)
24. [Testing & CI](#24-testing--ci)
25. [AI Assistant & MCP](#25-ai-assistant--mcp)
26. [HTML5 / Web](#26-html5--web)
28. [The CookieJar](#28-the-cookiejar)

The game factory workflow (27), Darks Games accounts and social (29) and MakeChibi (30) are
wiki-only: [`wiki/27`](wiki/27-game-factory-workflow.md), [`wiki/29`](wiki/29-darksgames.md),
[`wiki/30`](wiki/30-makechibi.md).

---

## 1. Core Architecture

The foundation every other system is built on.

### Scene Graph
Scenes contain Layers; Layers contain Actors. Scenes are serialised to JSON and loaded by name or path.

```
Scene
  └── Layer (background, gameplay, ui, etc.)
        └── Actor
              └── Component
              └── Component
```

### Actor System
Every object in the world is an Actor. The base `Actor` class exposes a full lifecycle:

| Method | When it fires |
|---|---|
| `Awake()` | On instantiation, before Start |
| `Start()` | First frame the Actor is active |
| `Update(dt)` | Every frame |
| `FixedUpdate(dt)` | Fixed physics timestep |
| `LateUpdate(dt)` | After all Updates, before draw |
| `Draw(spriteBatch)` | Render step |
| `OnDestroy()` | Before the Actor is removed |

### Component System
Components add behaviour to Actors. Any number of components can be attached or detached at runtime.

```csharp
var rb = actor.AddComponent<Rigidbody2D>();
var sr = actor.GetComponent<SpriteRenderer>();
actor.RemoveComponent<SpriteRenderer>();
```

- `[RequireComponent(typeof(T))]` — auto-adds dependencies
- Components subscribe to the same lifecycle methods as Actors

### Tag & Layer System
- String tags — `actor.Tag = "Enemy"` — query with `Scene.FindActorsByTag("Enemy")`
- Integer layers (0–31) — used for rendering order and physics collision masks
- Layer names defined in `ProjectSettings.json`

### Prefabs
Serialised Actor + Component bundles stored as `.json` files.

```csharp
var enemy = Prefab.Instantiate("Prefabs/Enemy.json", position, rotation);
```

Supports nested prefabs and per-instance property overrides.

### Transform
Every Actor has a Transform component:
- `Position` (Vector2 / Vector3), `Rotation` (float / Quaternion), `Scale` (Vector2 / Vector3)
- Full parent/child hierarchy — `transform.SetParent(other)`
- `transform.WorldPosition`, `transform.LocalPosition` — automatic world/local space conversion

---

## 2. JavaScript Scripting

Gameplay logic lives in `.js` files. No compile step. Designers can edit scripts in any text editor and see changes immediately.

### Runtime
- **Default:** Jint — pure .NET JavaScript interpreter; zero native dependencies
- **Optional:** ClearScript/V8 — swap in for higher performance on complex AI scripts

### Script Component
Attach any `.js` file to an Actor as a `ScriptComponent`:

```json
{
  "type": "ScriptComponent",
  "script": "Scripts/EnemyAI.js"
}
```

Inside the script, lifecycle hooks map directly to the Actor lifecycle:

```javascript
function onStart() {
    this.speed = 120;
}

function onUpdate(dt) {
    const player = Scene.findActorByTag("Player");
    if (player) {
        const dir = Vector2.normalize(player.position.subtract(this.actor.position));
        this.actor.position = this.actor.position.add(dir.scale(this.speed * dt));
    }
}

function onDestroy() {
    Audio.playOneShot("Sounds/death.ogg");
}
```

### Hot Reload
A `FileSystemWatcher` monitors the Scripts folder. When a `.js` file is saved, the engine:
1. Stops the script on all affected Actors
2. Re-parses and re-compiles the script
3. Re-runs `onStart()` on all affected Actors
4. No engine restart required

### C# API Bridge
The full engine API is exposed to JavaScript:

| Namespace | Exposed APIs |
|---|---|
| `Transform` | position, rotation, scale, parent, worldPosition |
| `Input` | isPressed, isHeld, getAxis, getGamepad |
| `Physics` | raycast, overlapCircle, overlapBox |
| `Audio` | play, playOneShot, stop, setVolume |
| `Scene` | findActor, findActorsByTag, loadScene, instantiate |
| `UI` | findWidget, setText, setVisible |
| `Debug` | log, warn, error, drawLine, drawBox |

### TypeScript Definitions
A `.d.ts` file ships with the engine for full IDE autocomplete in VS Code or any TypeScript-aware editor.

### Script Console
An in-engine JavaScript REPL. Evaluate any expression against live Actor state while the game is running. Available in both editor and runtime debug builds.

---

## 3. 2D Rendering

### SpriteBatch Renderer
Built on MonoGame's `SpriteBatch`:
- **Sprite** — single texture region; tint, rotation, scale, flip
- **SpriteSheet** — frame-strip or JSON atlas; frame index or named region
- **9-Patch / Sliced Sprite** — for UI borders and resizable panels

### Camera System
- Multiple cameras with independent viewport rects
- Zoom, rotation, world-to-screen and screen-to-world projection helpers
- `CameraFollow` component — smooth lerp to target Actor with configurable lead and deadzone
- `CameraShake` component — trauma-based shake with configurable falloff

### Layers & Draw Order
- Each Layer renders to its own render target
- Z-depth sorting within a layer (float sort key per Actor)
- Per-layer visibility toggle; per-layer post-processing pass

### Shader Support
- Custom HLSL `.fx` files assigned per sprite, per material, or per layer
- Built-in shader library:
  - `Outline.fx` — configurable colour and width
  - `Dissolve.fx` — noise-based dissolve with progress float
  - `Flash.fx` — solid-colour flash with lerp amount
  - `Greyscale.fx` — full greyscale conversion
  - `Pixelate.fx` — pixel size uniform

### Tilemap
- Load Tiled `.tmx` (XML) and Tiled JSON export formats
- Multiple tile layers, object layers, image layers
- Auto-tile rules (terrain sets)
- Animated tiles — per-tile frame sequence at configurable FPS
- `TilemapCollider2D` component — auto-generates static physics bodies from tile collision shapes

### Particle System
`ParticleEmitter` component — configurable per emitter:
- Lifetime range, start/end size curve, start/end colour gradient
- Velocity range, angular velocity, gravity scale
- Burst mode (spawn N particles at once) or continuous rate
- Sub-emitters (fire another emitter on particle death)
- Blend mode per emitter (additive, alpha, multiply)

### 2D Lighting
`Lighting2D` renders every `Light2D` into an off-screen light map and multiplies it over
the scene:
- **Point lights** — position, radius, colour, intensity, falloff exponent
- **Spot lights** — a cone aimed along the transform's rotation
- **Global** — uniform over the screen, ignoring position
- **Ambient** — the darkness floor; black means unlit areas go fully black
- **Occluders** — `ShadowCaster2D` marks a convex polygon as blocking light
- `Downsample` trades light-map resolution for fill rate, which also softens light edges

Falloff is baked into a generated radial texture rather than computed per pixel, so the
pipeline needs no content pipeline at all. Supply `NormalMapEffect` and `SceneNormalMap`
for normal-mapped surface detail.

### Post Processing
Configurable render target chain per camera:
- **Bloom** — threshold, intensity, scatter
- **CRT Scanline** — line spacing, intensity, optional barrel curvature
- **Vignette** — radius, softness, colour
- **Colour Grading** — lift/gamma/gain, saturation, contrast
- Custom pass — attach any `.fx` file as a post-processing step

---

## 4. 3D Rendering

3D is a first-class citizen alongside 2D. 2D and 3D Actors coexist in the same scene.

### Render pipeline
`RenderSystem3D` runs each frame before the 2D pass, so sprites and UI composite over the
3D scene. It culls against the camera frustum, sorts opaque front-to-back and transparent
back-to-front, picks the most influential lights per object, and reports what it submitted
through `Renderer3D.Stats`. See the [3D rendering wiki page](wiki/05-rendering-3d.md).

### 3D Scene
- 3D Actors use a `Transform3D` component — `Vector3` position, `Quaternion` rotation, `Vector3` scale
- `Camera3D` covers both perspective and orthographic; tag an actor `MainCamera3D` to make it the default
- Scene can mix 2D layers (UI, HUD) composited over a 3D viewport

### Mesh Renderer
`MeshRenderer` component:
- Load `.obj`, `.gltf`, `.glb`, `.fbx` via **Assimp.NET**
- Per-submesh material assignment
- Cast/receive shadows toggle per mesh

### Material System
PBR-lite material pipeline:
- **Albedo** — base colour texture or colour value
- **Normal map** — tangent-space normal map
- **Metallic** — metallic factor (float) or texture channel
- **Roughness** — roughness factor (float) or texture channel
- **Emissive** — emissive colour/texture for self-illuminated surfaces
- Custom HLSL `.fx` per material — override the entire shading model if needed
- `StandardPBR.fx` ships with the engine and implements the full metallic/roughness model;
  with no shader assigned the renderer falls back to MonoGame's `BasicEffect`, which gives
  you three directional lights and textures with no content pipeline at all

### Lighting (3D)
| Light Type | Properties |
|---|---|
| Directional | Direction, colour, intensity, shadow map |
| Point | Position, range, colour, intensity, attenuation |
| Spot | Position, direction, inner/outer angle, range, colour |
| Ambient | Colour, intensity — scene-wide base light |

- Shadow maps — a depth pass for the primary directional caster, exposed to your shader as
  `ShadowMap` and `LightViewProjection`. Needs the compiled `ShadowDepth.fx`; without it
  the pass is skipped and a warning is logged once
- Light component added to any Actor; `Renderer3D.MaxLightsPerObject` caps how many bind per draw

### Skybox
- Cubemap skybox — assign 6-face cubemap texture
- Gradient fallback — top colour / horizon colour / ground colour

### Level of Detail (LOD)
`LODGroup` component:
- Assign multiple meshes at distance thresholds (LOD0, LOD1, LOD2, Culled)
- Smooth transition or hard-cut per group
- Auto-LOD generation tool in editor (planned)

### Skinned Meshes
`SkinnedMeshRenderer` deforms a rigged mesh from a `SkeletalAnimator`'s bone palette, via
MonoGame's `SkinnedEffect` — up to 72 bones and 4 weights per vertex. A mesh needing more
is rejected at load with a clear message rather than rendering wrong.

### 3D Particle System
`ParticleSystem3D`:
- Billboard particles — camera-facing quads, all drawn in a single call from one dynamic
  vertex buffer; thousands of particles cost one draw
- Vertical billboards — face the camera but keep an upright axis, for smoke and fire
- Mesh particles — render a mesh per particle
- Point, sphere, box and cone emission shapes; gravity, drag, spin, size and colour curves
- Fixed-size pool, so a long-running emitter allocates nothing per frame

### 3D Camera Controllers (built-in base classes)
- `FlyCamController` — free-look WASD + mouse; configurable speed and sensitivity
- `OrbitCamController` — orbit around a target; zoom, min/max distance, collision avoidance
- `FirstPersonController` — character-attached FPS camera; head-bob option

---

## 5. Asset Management

No Content Pipeline. No `.xnb`. Load everything raw at runtime.

### Raw Loaders
| Asset Type | Formats | Method |
|---|---|---|
| Texture | PNG, JPG, BMP, TGA | `Texture2D.FromStream` |
| Audio (small) | WAV | `SoundEffect.FromStream` |
| Audio (streaming) | OGG, MP3 | NVorbis / NAudio streaming |
| Font | TTF, OTF | FontStashSharp — any size at runtime |
| 3D Model | OBJ, GLTF, GLB, FBX | Assimp.NET |
| Shader | HLSL `.fx` | MonoGame Effect compile |
| Data | JSON, XML, CSV | System.Text.Json / raw stream |

### Asset Registry
Central `AssetManager` singleton:
- Path-based string key: `AssetManager.Load<Texture2D>("Sprites/hero.png")`
- Reference counting — assets stay loaded while any reference exists
- Explicit unload: `AssetManager.Unload("Sprites/hero.png")`
- List all loaded assets with byte sizes via Debug overlay

### Async Loading
```csharp
await AssetManager.LoadAsync<Texture2D>("Sprites/world.png", progress =>
{
    loadingBar.Value = progress;
});
```
- Background thread loading; callback on main thread
- `LoadingScreen` helper — batch-loads an asset list or a `Type|path` manifest a few items
  per frame, reporting weighted progress on the game thread so the bar keeps animating and
  the window stays responsive

### Hot Reload (Dev Mode)
`FileSystemWatcher` monitors the asset directory. On change:
- Textures — reloaded and re-assigned to all active sprite renderers
- Audio — reloaded into audio buffer
- Scripts — reloaded and re-run on affected Actors
- Hot reload is disabled in Release builds

### Asset Bundles
Pack assets into `.sba` (SexyBiscuit Archive) files:
- Single compressed archive; stream assets from disk or remote URL
- Used for DLC packs, Workshop content, and patching
- `AssetBundle.Mount("dlc01.sba")` — mounts into the virtual file system

---

## 6. Physics — 2D

Powered by **Aether Physics 2D** (a .NET Box2D port).

### Collider Components
| Component | Shape |
|---|---|
| `BoxCollider2D` | Axis-aligned or rotated rectangle |
| `CircleCollider2D` | Circle with radius |
| `PolygonCollider2D` | Convex polygon (up to 8 verts) |
| `EdgeCollider2D` | Open polyline (e.g. terrain edge) |
| `CompositeCollider2D` | Merge multiple shapes into one body |

### Rigidbody2D
```csharp
var rb = actor.GetComponent<Rigidbody2D>();
rb.Mass = 1.5f;
rb.GravityScale = 1.0f;
rb.LinearVelocity = new Vector2(200, 0);
rb.AngularVelocity = 0.5f;
rb.Drag = 0.1f;
rb.FreezeRotation = true;
```

### Collision Callbacks
Callbacks fire on the Actor (and forwarded to all components and scripts):

```csharp
void OnCollisionEnter(CollisionData data) { }
void OnCollisionStay(CollisionData data)  { }
void OnCollisionExit(CollisionData data)  { }
void OnTriggerEnter(Actor other)          { }
void OnTriggerStay(Actor other)           { }
void OnTriggerExit(Actor other)           { }
```

`CollisionData` contains contact point, normal, and relative velocity.

### Queries
```csharp
Physics2D.Raycast(origin, direction, distance, layerMask, out RaycastHit hit);
Physics2D.BoxCast(origin, size, angle, direction, distance, layerMask);
Physics2D.CircleCast(origin, radius, direction, distance, layerMask);
Physics2D.OverlapBox(origin, size, angle, layerMask);
```

### Tilemap Collider
`TilemapCollider2D` component auto-generates optimised static bodies from a Tilemap layer's tile collision shapes. Merged-mesh optimisation reduces body count for large levels.

---

## 7. Physics — 3D

Powered by **Bepu Physics v2** — high-performance, allocation-minimal .NET 3D physics.

### Collider Components
| Component | Shape |
|---|---|
| `BoxCollider3D` | Axis-aligned box |
| `SphereCollider3D` | Sphere |
| `CapsuleCollider3D` | Capsule (for characters) |
| `CylinderCollider3D` | Cylinder |
| `MeshCollider3D` | Concave triangle mesh (static only) |
| `CompoundCollider3D` | Multiple shapes as one body |

### Rigidbody3D
```csharp
var rb = actor.GetComponent<Rigidbody3D>();
rb.Mass = 5f;
rb.LinearVelocity = new Vector3(0, 10, 0);
rb.AngularVelocity = Vector3.Zero;
rb.GravityOverride = new Vector3(0, -20, 0);
rb.LinearDamping = 0.1f;
```

### Constraints
| Constraint | Description |
|---|---|
| `BallSocketConstraint` | Free rotation, locked position |
| `HingeConstraint` | Single-axis rotation (door, wheel) |
| `SliderConstraint` | Linear movement along one axis |
| `DistanceLimitConstraint` | Min/max distance between two bodies |

### Queries
```csharp
Physics3D.Raycast(origin, direction, distance, layerMask, out RaycastHit3D hit);
Physics3D.SphereCast(origin, radius, direction, distance, layerMask);
Physics3D.BoxCast(origin, halfExtents, orientation, direction, distance, layerMask);
```

### Character Controller
`CharacterController3D` component — capsule-based kinematic controller:
- Step-up height, max slope angle, ground snap distance
- `Move(velocity)` — engine resolves collisions each fixed step
- `IsGrounded`, `IsOnSlope` state queries
- No Rigidbody3D required; designed for direct player control

---

## 8. Input

Single unified input API regardless of device.

### Action Maps
Define named actions in `InputActions.json`:

```json
{
  "Jump":   [{ "device": "keyboard", "key": "Space" },
             { "device": "gamepad",  "button": "A" }],
  "MoveX":  [{ "device": "keyboard", "negKey": "A", "posKey": "D" },
             { "device": "gamepad",  "axis": "LeftStickX" }],
  "Shoot":  [{ "device": "mouse",    "button": "Left" },
             { "device": "gamepad",  "trigger": "Right" }]
}
```

Query in C# or JS:

```csharp
if (Input.IsPressed("Jump"))   { /* ... */ }
if (Input.IsHeld("Crouch"))    { /* ... */ }
if (Input.IsReleased("Shoot")) { /* ... */ }
float x = Input.GetAxis("MoveX");
```

### Rebinding
- `Input.RebindAction("Jump", newBinding)` — rebind at runtime
- Bindings auto-saved to `Saves/InputBindings.json` and loaded on start
- Reset to default: `Input.ResetBindings()`

### Gamepad
- Up to 4 simultaneous XInput gamepads; hot-plug detection at runtime
- `Input.GetGamepad(playerIndex)` — returns pad state for that slot
- Rumble: `Input.SetRumble(playerIndex, lowFreq, highFreq, duration)`
- Per-axis dead-zone config in project settings
- On disconnect during play: event fires; game can reassign or pause

### Cursor
```csharp
Input.ShowCursor();
Input.HideCursor();
Input.LockCursor();          // confine to window center
Input.SetCursorTexture(tex); // custom cursor sprite
```

### Touch
- Virtual joystick — configurable position/size, left or right thumb zone
- Tap, press, release, hold events per touch ID
- `Input.GetTouchPosition(id)`, `Input.GetTouchDelta(id)`
- Pinch gesture — `Input.GetPinchDelta()` for zoom
- All touch events available in Action Map as a `touch` device type

---

## 9. Audio

### AudioSource Component
Attach to any Actor for spatial audio:
```csharp
var src = actor.GetComponent<AudioSource>();
src.Clip = AssetManager.Load<AudioClip>("Sounds/explosion.ogg");
src.Volume = 0.8f;
src.Pitch = 1.0f;
src.Loop = false;
src.Is3D = true;          // positional audio
src.RolloffDistance = 500f;
src.DopplerScale = 1.0f;
src.Play();
src.Stop();
src.FadeOut(duration: 1.5f);
```

### Audio Buses
Four default buses; additional buses definable in project settings:

| Bus | Default Use |
|---|---|
| Master | Parent of all buses |
| Music | Background music tracks |
| SFX | Sound effects |
| Voice | Dialogue / voice-over |

Per bus: volume, pitch, mute, solo. `AudioBus.Music.Volume = 0.6f`.

### AudioManager
```csharp
AudioManager.PlayOneShot("Sounds/coin.wav");                      // fire and forget
var handle = AudioManager.Play("Music/theme.ogg", loop: true);    // managed
AudioManager.FadeIn(handle, duration: 2f);
AudioManager.FadeOut(handle, duration: 1f);
AudioManager.CrossFade("Music/battle.ogg", duration: 1.5f);
```

### Audio Effects
Per source or per bus:
- **Reverb** — room size, decay, wet/dry
- **Echo** — delay time, decay factor
- **Low-pass filter** — cutoff frequency (muffling through walls)
- **High-pass filter** — remove rumble

### Streaming
- Files > configurable size threshold are streamed from disk
- Streaming is transparent — same API regardless of load strategy
- Voice pool — fixed number of simultaneous voices; oldest non-looping voice stolen on overflow

---

## 10. UI System

One retained widget tree, shared by both engines and reachable from a game script. A UI is
a tree of nodes laid out by rows, columns and grids, navigable with a mouse, a finger, a
gamepad or a TV remote — and painted either across the screen or on a plane standing in the
scene, without the tree knowing which.

### A whole screen in one call

```js
UI.build({
    name: "hud", layout: "column", gap: 8, padding: 12, background: "#161920e6",
    children: [
        { name: "title", kind: "label", text: "JAKE01", scale: 3 },
        { layout: "row", gap: 6, crossAlign: "center", children: [
            { kind: "label", text: "HP", width: 34 },
            { name: "hp", kind: "bar", grow: 1, height: 8, tint: "#c63832" },
        ]},
    ],
});

UI.find("hp").value = health / maxHealth;
```

The same document loads from a `.ui` file or a `UiCanvas` component in a scene, and C# builds
it the same way with `UiDocument.FromJson`.

### The same widget, on the screen or on a wall

A document says nothing about which space it is in — the canvas decides. So a panel authored
once can be a HUD in one scene and a terminal bolted to a wall in another, with no second file
and no second layout pass:

```js
UI.space = "world";              // "screen" | "world"
UI.facing = "plane";             // or "billboard" / "verticalBillboard"
UI.pixelsPerUnit = 140;          // canvas units per world unit
```

A world canvas is painted into a texture by the same painter and hung on a quad in the 3D pass,
so geometry in front of it hides it and a pointer ray clicks it. For a marker that must stay
crisp and upright instead — damage numbers, quest arrows — set `worldFollow` on a node of an
ordinary screen canvas and it is projected through the camera each frame.

### Sizing

| Written | Means |
|---|---|
| `width: 240` | pixels |
| `width: "auto"` | as wide as the content |
| `width: "*"` | fill the parent — what "cover the screen" means |
| `width: "50%"` | a share of the parent |
| `grow: 1` | take the leftover space along the main axis |

### Kinds

`panel` `label` `bar` `button` `image` `slider` `toggle` `textField` `dropdown` `scrollView`
`tabStrip` `spacer` — laid out by `row`, `column`, `grid` or `flow`, with `gap`, `padding`,
`margin`, `mainAlign` and `crossAlign`.

### Anchors, for what you would rather place than lay out

`absolute: true` plus an anchor, which is both where on the parent the node hangs and which of
its own corners hangs there:

```js
UI.root.add({ kind: "label", text: "v1.0.1", absolute: true, anchor: "bottomright", x: -12, y: -12 });
```

### Focus, so a menu works on a pad

Any button is focusable and the engine walks between them spatially — no per-button wiring.

```js
UI.setFocus(UI.find("resume"));
UI.navigate("down");        // false when there is nothing that way
UI.inputMode;               // "pointer" | "directional" | "touch"
```

`modal: true` traps focus inside a node, so a pause menu cannot lose the cursor to the HUD
behind it, and a click outside it misses rather than pressing what it lands on.

### Interaction is polled, not called back

`clicked`, `hovered`, `pressed` and `focused` are flags read each frame. A JS function held by
the C# side marshals differently on the two engines; a boolean does not.

```js
if (UI.find("start").clicked) { Scene.load("Scenes/Level1"); }
```

### Text

One 5×7 bitmap font in `html5/src/ui/font5x7.json`, imported by the browser and embedded by
the C# engine, so the two cannot render different text. A label sizes itself unless given a
width, and `wrapText: true` breaks it to whatever the layout hands it.

Full reference: [`wiki/09-ui.md`](wiki/09-ui.md) and [`wiki/11-scripting.md`](wiki/11-scripting.md).


---

## 11. Animation

### Sprite Animation
`SpriteAnimator` component:
- Frame-strip animation — define start frame, end frame, FPS
- Atlas animation — named regions from a JSON atlas; frame sequence by name list
- Multiple named clips per animator (`Idle`, `Run`, `Attack`, `Death`)
- `Play("Run")`, `PlayOnce("Attack", onComplete: () => Play("Idle"))`

### Animator State Machine
`AnimatorController` asset — define in the Animator Editor:
- **States** — each state plays one animation clip
- **Transitions** — directional arrows between states
- **Conditions** — `bool`, `int`, `float`, `trigger` parameters on transitions
- **Any State** — transitions that can fire from any state (e.g. Death)

```csharp
animator.SetBool("IsRunning", true);
animator.SetTrigger("Attack");
animator.SetFloat("Speed", rb.LinearVelocity.Length());
```

### 3D Skeletal Animation
Loaded from `.gltf` / `.fbx` via Assimp.NET:
- Named animation clips extracted from file
- **Blend trees** — blend between clips by float parameter (e.g. walk→run by speed)
- **Root motion** — apply root bone delta to Actor transform each frame
- **Additive animation** — layer a clip on top of another (e.g. aim offset over locomotion)

### Tweening
Built-in tween engine — animate any property without keyframe data:

```csharp
actor.transform.TweenPosition(target, duration: 0.5f, easing: Ease.OutQuad);
actor.transform.TweenScale(Vector2.Zero, duration: 0.3f, easing: Ease.InBack);
sprite.TweenColour(Color.Transparent, duration: 1f);
ui.panel.TweenPosition(new Vector2(0, -200), duration: 0.4f, easing: Ease.OutElastic);

// Sequence
Tween.Sequence()
    .Append(actor.transform.TweenPosition(a, 0.3f))
    .AppendInterval(0.1f)
    .Append(actor.transform.TweenPosition(b, 0.3f))
    .Play();
```

Full easing library: Linear, InOut variants of Quad, Cubic, Quart, Quint, Sine, Expo, Circ, Back, Bounce, Elastic, Spring.

### Spine (not implemented)
Spine 2D runtime integration is not in the engine. Spine's runtime is separately licensed,
so it is left to the game project: implement a `Component` that owns a Spine skeleton and
draws it in `Draw(SpriteBatch)`.

---

## 12. Scene Management

### Scene Serialisation
Scenes are plain JSON files — human-readable and version-control friendly:
```json
{
  "name": "Level01",
  "layers": [
    {
      "name": "gameplay",
      "actors": [
        {
          "name": "Player",
          "position": [0, 0, 0],
          "components": [
            { "type": "SpriteRenderer", "sprite": "Sprites/hero.png" },
            { "type": "ScriptComponent", "script": "Scripts/PlayerController.js" }
          ]
        }
      ]
    }
  ]
}
```

### Scene Loading
```csharp
SceneManager.LoadScene("Scenes/Level01.json");              // replace current scene
SceneManager.LoadSceneAdditive("Scenes/HUD.json");          // add to current scene
await SceneManager.LoadSceneAsync("Scenes/Level02.json");   // async with progress
SceneManager.UnloadScene("Scenes/HUD.json");
```

### Prefabs
- JSON prefab files stored anywhere in the project
- `Prefab.Instantiate(path, position, rotation)` — spawns a full Actor hierarchy
- **Nested prefabs** — prefab files can reference other prefab files
- **Prefab overrides** — per-instance property overrides without breaking the prefab link
- Changes saved in the Prefab Editor propagate to all instances

### DontDestroyOnLoad
```csharp
SceneManager.DontDestroyOnLoad(actor); // survives all scene transitions
```
Used for GameManager, AudioManager, SteamManager, etc.

### World Streaming
- The world is divided into rectangular chunk scenes
- `WorldStreamer` component monitors player position and loads/unloads adjacent chunks
- Configurable load radius; async loading with priority queue
- Actors near chunk borders replicated into both chunks to prevent pop-in

---

## 13. Networking / Multiplayer

### Transport
**LiteNetLib** — UDP with reliable-ordered, reliable-unordered, unreliable, and sequenced channels. Low latency, battle-tested in production games.

### Topology
- **Client-Server authoritative** — server owns truth; clients send inputs, server sends state
- **Listen server** — host player runs server in-process; up to configurable player cap
- **Dedicated server** — headless mode; no rendering; minimal CPU/memory footprint

### Replication
Mark any Actor field for automatic sync:

```csharp
public class PlayerActor : Actor
{
    [Replicated]
    public Vector3 Position { get; set; }

    [Replicated]
    public int Health { get; set; }

    [Replicated(Condition = ReplicateCondition.OwnerOnly)]
    public int Ammo { get; set; }
}
```

- Dirty-flag tracking — only changed values sent each tick
- Delta compression — send diff from last acknowledged state
- Replication conditions: `Always`, `OwnerOnly`, `InitialOnly`

### Remote Procedure Calls

```csharp
[ServerRpc]
public void RequestFireWeapon(Vector3 direction) { /* runs on server */ }

[ClientRpc]
public void PlayHitEffect(Vector3 position)      { /* runs on all clients */ }

[ClientRpc(Target = RpcTarget.Owner)]
public void ShowRespawnTimer(float time)          { /* runs only on owning client */ }
```

### NetworkObject Component
Add `NetworkObject` to any Actor to make it multiplayer-aware:
- Assigned a `NetworkId` (uint) unique across the session
- Server spawns the Actor; `NetworkObject.Spawn()` replicates it to all clients
- `NetworkObject.Despawn()` removes it on all clients simultaneously

### Interest Management
Server-side relevance system:
- Each `NetworkObject` has a relevance radius
- Server only sends replication updates to clients within range
- Reduces bandwidth for large worlds significantly

### LAN Discovery
```csharp
LanDiscovery.StartBroadcast(port, serverInfo);          // host side
LanDiscovery.Discover(port, onFound: server => { });    // client side
```

---

## 14. Serialisation & Save System

### JSON Serialiser
`SceneSerializer` (in `SexyBiscuit.Engine.Scene`) — System.Text.Json backed, extended with
engine type converters:
```csharp
string json  = SceneSerializer.Serialize(scene);
Scene  scene = SceneSerializer.Deserialize(json);

SceneSerializer.SaveToFile(scene, "Assets/Scenes/Level1.json");
```
Component state is discovered by reflection over public read/write properties whose type
round-trips: primitives, string, enum, `Vector2`, `Vector3`, `Vector4`, `Quaternion`,
`Color` and `Nullable<T>` of any of those. GPU types (`Texture2D`, `Effect`) and
collections are skipped on purpose — see
[Scenes & Prefabs](wiki/12-scenes-prefabs.md) for why and what to do instead.

Single actors serialise the same way through `Prefab`.

### Save Slots
```csharp
SaveManager.Save(slot: 0, data: gameSave);
GameSave save = SaveManager.Load<GameSave>(slot: 0);
SaveManager.Delete(slot: 0);
bool exists = SaveManager.SlotExists(0);
```
- Optional AES-256 encryption: `SaveManager.SetEncryptionKey("...")`
- Auto-sync to Steam Cloud when Steamworks is initialised (see Section 18)

### PlayerPrefs
```csharp
Prefs.SetFloat("MasterVolume", 0.8f);
Prefs.SetInt("GraphicsQuality", 2);
Prefs.SetString("PlayerName", "Biscuit");
float vol = Prefs.GetFloat("MasterVolume", defaultValue: 1.0f);
Prefs.Save(); // explicit flush to disk; also called automatically on exit
```

### Binary Save Format
For large world state where JSON is too slow:
```csharp
SaveManager.SaveBinary(slot: 0, data: worldState);
WorldState ws = SaveManager.LoadBinary<WorldState>(slot: 0);
```
Uses `System.IO.BinaryWriter`/`BinaryReader` with a custom serialisation interface.

---

## 15. Visual Editor

A standalone editor application that hosts the engine in an embedded viewport.

### Editor Shell
Built with **ImGui.NET** — dockable panels, multi-window layout, dark theme by default. The engine game loop runs inside the editor viewport at full speed.

### Scene Viewport
- 2D and 3D view modes; toggle per scene type
- **Move / Rotate / Scale gizmos** — click to select, drag to transform; W/E/R shortcuts
- 3D centre handle supports camera-plane movement, uniform scale and screen-space rotation; snapping is configurable
- **Current limitation:** selection is single-actor. Box/Ctrl multi-select, selection pivots, world/local mode in native, plane handles and surface/vertex snapping are not implemented yet.

### Hierarchy Panel
- Full tree view of Scene → Layer → Actor → Component
- Drag actors to reparent
- Right-click context menu: Add Actor, Add Component, Duplicate, Delete
- **Current limitation:** visibility and lock state per actor are not implemented yet.

### Inspector Panel
- Displays all components on the selected Actor
- Edit primitive scalar/vector/colour/enum fields inline; the browser also has validated generic JSON collection editing
- JS `ScriptComponent` — exposes any property declared in the script's `properties` block as editable fields
- Add / Remove component buttons
- **Current limitation:** typed asset/object pickers, maps, nested structures and specialised component inspectors are not implemented yet.

### Asset Browser
- File-tree view of the project's `Assets/` folder
- Thumbnail previews for images only
- **Current limitation:** audio/mesh previews, typed reference assignment, dependency repair and per-asset editors are not implemented yet.

### Play / Pause / Step
- **Play** — starts the game loop inside the editor viewport; full engine systems active
- **Pause** — freezes the game loop; inspect any Actor's live state in the Inspector
- **Step** — advance one fixed-update tick while paused
- Game state is restored to pre-play state on Stop

### Console Panel
- Log output with timestamp, level (Info / Warning / Error), and source Actor link
- Click source link to select the Actor in the Hierarchy
- Filter by level; search by keyword
- **JS REPL tab** — evaluate JavaScript against the live game state while paused

### Authoring domains not yet implemented

Prefab isolation/overrides, animator graphs, material graphs, visual scripting,
Sequencer-style timelines, tilemap painting, terrain/foliage and world-partition
authoring are not currently end-to-end editor workflows. Runtime support or a
documentation section must not be read as a claim that either editor can author
and round-trip those assets. See [the editor workbench status](wiki/31-editor-workbench.md).

### Build Settings Panel
- Platform dropdown; per-platform settings pane (icons, IDs, signing, etc.)
- Scene list — drag to reorder; checkbox to include/exclude; first = startup scene
- App name, version string, bundle ID
- Output directory picker
- **Build** and **Build & Run** buttons
- Inline build log with errors and warnings

### Assistant Panel
- Claude Code embedded in the editor: a chat transcript with streaming replies, one row per tool call, questions with buttons, permission prompts
- **Stop** (Shift+F8) interrupts the turn and cancels running tool calls; a viewport banner shows when Claude is working
- Activity tab: every MCP call served, from the embedded session or a terminal Claude Code
- Autonomous by default; Accept-edits, Ask and Auto modes available
- See [§25](#25-ai-assistant--mcp)

### C# Project Panel
- Build, Build & Reload and Run Standalone for the project's C# code
- Live build log and clickable diagnostics that open the Code Editor
- Hot reload keeps the scene, including unsaved edits, across an assembly swap

---

## 16. Debug & Tooling

### Debug Overlay
Toggle with **F1** in Development and Debug builds:
- FPS (current, min, max over 1s window)
- Frame time (ms), fixed update time (ms)
- Draw calls, active particles, physics body count
- GC pressure (collections per second, heap size)
- Active network objects, replication bandwidth in/out

### Gizmos API
Draw debug shapes in world or screen space — visible in editor always, in runtime builds when the overlay is active:
```csharp
Gizmos.DrawLine(from, to, Color.Red);
Gizmos.DrawBox(center, size, angle, Color.Green);
Gizmos.DrawCircle(center, radius, Color.Yellow);
Gizmos.DrawSphere(center, radius, Color.Blue);
Gizmos.DrawText(position, "Label", Color.White);
```

### Profiler
Per-frame timeline breakdown:
- Update systems (per-Actor update time)
- Physics step time
- Render time (CPU prepare + GPU submit)
- Network tick time
- Asset streaming I/O time

View as bar chart or flame graph in the Debug overlay's Profiler tab.

### Memory Viewer
In-engine panel (Debug overlay > Memory tab):
- Lists all entries in the AssetRegistry with key, type, and byte size
- Total loaded asset memory
- Force-unload button per asset
- GC Collect button for manual testing

### Network Diagnostics
Shown in Debug overlay when a network session is active:
- Round-trip ping (ms)
- Packet loss percentage
- Inbound / outbound bandwidth (KB/s)
- Active replicated object count
- Last replication tick duration (ms)

---

## 17. Export & Platform Build System

One command per game: the `sbengine` CLI (`SexyBiscuit.Build/`) stages, publishes, packages,
reports and uploads; the editor's Build Settings panel runs the same pipeline for one platform.
Reference: [wiki/18](wiki/18-build-export.md).

### Supported Targets

| Platform | Output |
|---|---|
| Windows x64 / x86 | self-contained single-file `.exe` beside the staged content, zipped |
| Linux x64 | self-contained single-file binary, `.tar.gz` |
| macOS x64 / ARM64 | self-contained single-file binary, `.tar.gz` (unsigned: right-click Open the first time) |
| Web | static site — `index.html`, `engine/`, the project's files — installable to a home screen with a manifest and a service worker, zipped |
| Steam (Windows / Linux / macOS) | the desktop build plus an `app_build.vdf` for `steamcmd` |
| Android / iOS | validated (keystore, team id) and staged; no APK or IPA yet — the web build is the mobile build |

### Export Process
Each target runs these steps in order, collecting every error rather than stopping:

1. **Validate** — name, version, output folder, start scene, project root, platform fields
2. **Directories** — `Assets/ Scripts/ Scenes/ Logs/`
3. **Asset cooking** — `AssetCooker` over the project's `Assets/` (incremental, by source hash)
4. **Scripts and scenes** — copied as they are; the same files both engines read
5. **Settings** — the project's `ProjectSettings.json` with the build's metadata added
6. **Platform defines** — `PLATFORM_WINDOWS`, `ARCH_ARM64`, `BUILD_RELEASE`, `STEAMWORKS`, …
7. **Steam VDF** (Steam targets)
8. **Web staging** (Web) — the HTML5 runtime beside the project, the page, the manifest, the icons, the service worker
9. **Publish** (desktop) — `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=true`; a project without C# gets a generated player project
10. **Package** — `.zip` or `.tar.gz` beside the platform folder
11. **Upload** (`--upload`) — one multipart POST per archive to the configured endpoint, with the version, commit and checksum

`dist/build-report.json` and one console line per target are the result.

### Build Configurations
| Config | Hot Reload (editor) | Stats overlay in the web build | Symbols |
|---|---|---|---|
| Debug | Yes | On | Full |
| Development | Yes | On | Full |
| Release | No | Off | Stripped |

### CLI
```bash
dotnet build SexyBiscuit.Build/SexyBiscuit.Build.csproj -c Release
dotnet run --no-build --project SexyBiscuit.Build -c Release -- --project Games/Foo --all --upload
dotnet run --no-build --project SexyBiscuit.Build -c Release -- --project Games/Foo --platform web
dotnet run --no-build --project SexyBiscuit.Build -c Release -- --project Games/Foo --platform osx-arm64,linux-x64 --config release --version 1.2.0
```

`--all` is web + win-x64 + osx-arm64 + linux-x64. The release workflow (`.github/workflows/release.yml`)
runs the same command on a matrix and prints the combined summary.

### Steam Publishing
The Steam targets write `app_build.vdf`; run `steamcmd +run_app_build` on it yourself. The
editor's Steam tab does not run `steamcmd`.

### Mobile
The installable web build (`webInstallable`, on by default) installs from the site on Android
and iPhone and runs offline. Native Android/iOS targets are a later milestone.

### Asset Cooking
`AssetCooker` copies every asset, re-processing only the ones whose source hash changed.
Textures are compressed on Windows targets when `texconv.exe` is on the PATH and copied
otherwise; audio is encoded with `oggenc` when it is present and copied otherwise. There is no
ASTC, BC7 or LOD baking.

---

## 18. Steam Integration (Steamworks.NET)

Full first-class integration via [Steamworks.NET](https://steamworks.github.io/). Not a stub. A `SteamManager` singleton initialises the Steamworks API on startup and pumps callbacks every frame.

### Lobbies & Matchmaking

```csharp
// Host
var lobby = await SteamLobby.CreateLobby(LobbyType.Public, maxPlayers: 4);
lobby.SetMetadata("map", "forest");
lobby.SetMetadata("mode", "coop");

// Find
var results = await SteamLobby.FindLobbies(filter =>
{
    filter.AddStringFilter("mode", "coop");
    filter.MaxResults = 20;
});

// Join
await SteamLobby.JoinLobby(lobbyId);
```

- **Lobby metadata** — arbitrary string key-value pairs visible to all members and to the lobby browser
- **Invite friend** — `SteamLobby.InviteFriend(steamId)` sends a Steam invite notification
- **Join on launch** — `+connect_lobby <id>` launch argument handled automatically; connects on startup
- **Rich Presence** — set `steam_display`, `connect`, `status` so friends see *"In a match on Forest Map (2/4)"* in their friends list
- **Lobby chat** — text channel for all lobby members via `LobbyChatMsg_t` callback

### Hosting & Joining Sessions
When a lobby is created the host starts a LiteNetLib server and writes the address into lobby metadata. Joining clients read the address and connect:

```csharp
// Host side (fires automatically on lobby creation)
NetworkManager.StartServer(port);
lobby.SetMetadata("address", $"{localIp}:{port}");

// Client side (fires automatically on lobby join)
string address = lobby.GetMetadata("address");
NetworkManager.ConnectClient(address);
```

- `SteamFriends.GetFriendGamePlayed(steamId)` — detect if a friend is in your game; show **Join** in overlay
- `GameRichPresenceJoinRequested_t` callback — handles invite accepts; connects automatically without extra UI

### Friends & Social
```csharp
var friends = SteamFriends.GetFriendList(FriendFlags.Immediate);
foreach (var friend in friends)
{
    string name    = SteamFriends.GetFriendPersonaName(friend);
    Texture2D icon = SteamFriends.GetFriendAvatar(friend, AvatarSize.Medium);
    bool inGame    = SteamFriends.GetFriendGamePlayed(friend, out var gameInfo);
}

SteamFriends.ActivateGameOverlay("friends");
SteamFriends.ActivateGameOverlay("community");
```

### Steam Overlay
- Auto-enabled for all builds where `SteamAPI.Init()` succeeds
- `GameOverlayActivated_t` callback — pause the game loop when the overlay opens and resume when it closes
- `SteamFriends.ActivateGameOverlayToWebPage(url)` — open any URL inside the overlay browser
- `SteamFriends.ActivateGameOverlayToUser("steamid", id)` — open profile, trade, or stats page for any user
- `SteamUtils.SetOverlayNotificationPosition(position)` — move toast notifications away from your HUD

### Achievements & Stats
```csharp
// Unlock
SteamUserStats.SetAchievement("ACH_FIRST_CAPTURE");
SteamUserStats.StoreStats();

// Progress notification
SteamUserStats.IndicateAchievementProgress("ACH_CATCH_10", currentValue: 7, maxValue: 10);

// Stats
SteamUserStats.SetStat("stat_total_battles", ++battleCount);
SteamUserStats.SetStat("stat_playtime_seconds", (float)totalPlaytime.TotalSeconds);
SteamUserStats.StoreStats();
```

The engine's wrapper is `SteamAchievements` (static, in `SexyBiscuit.Engine.Steam`):
`Unlock`, `SetProgress`, `SetStat`, `GetStatInt`/`GetStatFloat` and `RequestStats`, all
no-ops when Steam is not initialised so the same code runs outside Steam.

### Leaderboards
```csharp
var board = await SteamUserStats.FindOrCreateLeaderboard("HighScore",
    LeaderboardSortMethod.Descending, LeaderboardDisplayType.Numeric);

await SteamUserStats.UploadLeaderboardScore(board, LeaderboardUploadScoreMethod.KeepBest, score);

var entries = await SteamUserStats.DownloadLeaderboardEntries(board,
    LeaderboardDataRequest.GlobalAroundUser, rangeStart: -4, rangeEnd: 5);
```

Leaderboard **UI** is left to the game: rendering entries with avatars, ranks and scores
is a presentation decision, and every game wants a different one. Build it from
`ScrollView` plus `Label`, driven by the entries Steamworks returns.

### Steam Cloud
```csharp
// Automatic — SaveManager routes through Steam Cloud in Steam builds
SaveManager.Save(slot: 0, gameSave);  // writes to SteamRemoteStorage automatically

// Manual access
SteamRemoteStorage.FileWrite("save_0.json", bytes);
byte[] data = SteamRemoteStorage.FileRead("save_0.json");
(ulong used, ulong total) = SteamRemoteStorage.GetQuota();
```

### Workshop (Steam UGC)
```csharp
// Upload a new item
var result = await SteamUGC.CreateItem(SteamUtils.GetAppID(), WorkshopFileType.Community);
var update = SteamUGC.StartItemUpdate(SteamUtils.GetAppID(), result.PublishedFileId);
SteamUGC.SetItemTitle(update, "My Custom Map");
SteamUGC.SetItemDescription(update, "A cool map for Biscuit Chronicles");
SteamUGC.SetItemContent(update, contentFolderPath);
SteamUGC.SetItemPreview(update, previewImagePath);
SteamUGC.SetItemTags(update, new[] { "map", "forest" });
await SteamUGC.SubmitItemUpdate(update, changeNote: "Initial release");

// Load subscribed items at startup
var subscribed = SteamUGC.GetSubscribedItems();
foreach (var id in subscribed)
{
    SteamUGC.GetItemInstallInfo(id, out ulong sizeBytes, out string path, out uint ts);
    AssetBundle.Mount(path); // mount Workshop folder into virtual file system
}
```

### DLC
```csharp
// Define in DLC.json; check at runtime
if (DlcManager.IsOwned("DLC_JUNGLE_PACK"))
{
    SceneManager.LoadScene("Scenes/JungleWorld.json");
}
else
{
    SteamFriends.ActivateGameOverlayToStore(jungleDlcAppId);
}
```

### In-Game Store / Inventory
```csharp
SteamInventory.GetAllItems(out var result);
SteamInventory.TriggerItemDrop(itemDefId);
// Microtransaction purchases — initiate via Steam overlay; server-side webhook validates receipt
```

### Authentication
```csharp
// Client — generate ticket and send to server with connection request
byte[] ticket = SteamUser.GetAuthSessionTicket();
networkConnection.SendAuthTicket(ticket);

// Server — validate ticket on connection
SteamGameServer.BeginAuthSession(ticket, steamId, out var authResult);
if (authResult != BeginAuthSessionResult.OK)
    networkConnection.Kick("Auth failed");
```

Steam Game Server registration makes dedicated servers visible in the Steam server browser.

### Steam Datagram Relay (SDR)
Optional — swap LiteNetLib transport for SDR to route through Steam's relay network:
```csharp
NetworkManager.UseTransport(new SteamworksTransport()); // replaces LiteNetLib
```
Provides NAT traversal, DDoS protection, and lower latency routing automatically.

### Utilities
```csharp
SteamUtils.GetAppID();                          // runtime app ID — detect Spacewar vs shipping
SteamApps.GetLaunchQueryParam("branch");        // read launch parameters
SteamUtils.GetIPCountry();                      // two-letter country code
SteamScreenshots.TriggerScreenshot();           // F12 hook; also called on custom screenshot key
SteamUtils.SetOverlayNotificationPosition(NotificationPosition.BottomLeft);
```

---

## 19. Demo Game — Biscuit Chronicles

A third-person RPG with pet capture and Final Fantasy ATB combat. Ships with the engine repo. Proves every system works end-to-end and serves as the canonical integration test suite.

### Game Overview
- 3D overworld — explore biomes, find wild pets roaming the world
- **Pet capture** — enter a battle, deplete the pet's HP, throw a capture item; success probability formula in a hot-reloadable `.js` file
- **Party** — carry up to 3 captured pets; swap at camp
- **ATB Combat** — Active Time Battle gauge per combatant fills in real time; when full, the player picks an action (Attack, Skill, Item, Flee, Capture); status effects; elemental type chart
- **Multiplayer** — Host or Join via Steam lobby; both players share the overworld; co-op battles where both players' parties fight together
- **Full UI** — main menu, lobby browser, overworld HUD, party screen, battle screen, inventory, world map, settings, pause menu

### Engine Systems Demonstrated

| System | How it's used |
|---|---|
| 3D Rendering | PBR materials on characters/environment, shadow maps, skybox, bloom + colour grade |
| Third-person camera | Orbit with collision avoidance, lock-on mode, cinematic transition to battle camera |
| Character Controller 3D | Run, jump, dodge roll, interaction radius trigger |
| Actor/Component | Player, PetActor, WildPetActor, BattleCombatantActor all composed from components |
| JS Scripting hot-reload | All AI, capture formulas, and ATB tick logic in `.js`; edit live |
| Animator State Machine | Player locomotion blend tree; pet idle/walk/attack/faint states |
| Scene Streaming | Overworld split into chunks; load/unload by player proximity |
| Turn-based battle | ATB system, elemental weaknesses, status effects — all data-driven JSON |
| Inventory | Item definitions in JSON; consumables, capture items, equipment |
| Save System | Full party + world state; auto-sync to Steam Cloud |
| Steam Lobby | Host/Join/Invite via Steam; lobby browser; rich presence showing current area |
| Replication | Player positions, encounter states, battle outcomes via `[Replicated]` |
| Achievements | 10 achievements wired to `SteamAchievements` |
| Workshop | Demo map published as a Workshop item; auto-mounted on startup if subscribed |
| Tweening | Damage numbers float upward and fade; UI screen transitions |
| World-space UI | HP bars above wild pets; floating capture text |

### HUD Layout

**Overworld**
```
[HP orb] [MP orb]    [area name toast — top centre]    [minimap — top right]
[pet portrait 1] [pet portrait 2] [pet portrait 3]      [bottom left]
[interaction prompt — bottom centre]
```

**Battle Screen**
```
[enemy combatants — top, with HP bars and ATB gauges]
[player party — bottom, with HP/MP bars and ATB gauges]
[action menu — bottom right: Attack / Skill / Item / Flee / Capture]
[battle log — bottom left scrolling text]
[turn order queue — right side]
[damage numbers — world space, tweened]
```

**Capture Sequence**
- Capture chance % bar fills in front of the ball graphic
- Ball shakes N times (based on capture formula roll)
- Break-out animation if failed; success sparkle if caught

**Multiplayer HUD additions**
- Partner nameplate + HP bar in overworld (world-space)
- Ping indicator (top right, colour-coded)
- Ready indicator in battle (flashes when waiting for partner's action)

### Input Coverage

| Device | Bindings |
|---|---|
| Keyboard + Mouse | WASD move · Mouse orbit camera · E interact · Tab party screen · Escape pause |
| Gamepad (XInput) | Left stick move · Right stick camera · A interact · LB/RB battle menu navigate · RT attack · Start pause · Rumble on hit/capture |
| Touch | Virtual joystick (left zone) · Tap interact · Battle menu touch targets · Pinch zoom |
| Mixed | P1 keyboard + P2 gamepad simultaneously in local co-op |

Full rebinding panel in the settings screen. Bindings persisted to `Saves/InputBindings.json`. Gamepad hot-plug during session shows reassignment prompt.

---

## 20. Project Templates

Selectable at engine launch — equivalent to Unreal Engine's template picker. Each template ships a pre-wired starter scene, annotated example scripts, and placeholder art.

### Template Picker UI
- Grid of cards with preview screenshot, name, one-line description
- Filter chips: **2D** · **3D** · **Multiplayer** · **Mobile**
- **Include demo content** checkbox — placeholder sprites/models and populated example Actors
- Project name field + output directory picker → **Create Project**

### Available Templates

| Template | Genre / Use Case | Key Systems Pre-Wired |
|---|---|---|
| **Blank** | Empty starting point | Scene only; no Actors |
| **2D Platformer** | Side-scrolling platformer | Rigidbody2D, TilemapCollider, SpriteAnimator, 2D camera follow, coyote time script |
| **Top-Down 2D** | Twin-stick shooter or RPG | Top-down movement, mouse aim, 2D point lights, minimap UI widget |
| **3D First Person** | FPS base | CharacterController3D, FPS camera, 3D physics, weapon slot system, headbob |
| **3D Third Person** | Action / adventure base | OrbitCamController, IK placeholder, NavMesh stub, interaction system |
| **Puzzle** | Grid / tile puzzle | Grid actor system, undo/redo stack, no physics, step-based update loop |
| **Visual Novel** | Dialogue-driven story | Dialogue engine, portrait renderer, choice UI, scene branch scripting |
| **Real-Time Strategy** | RTS with units | Unit group selection box, A* pathfinding, minimap, fog-of-war render pass |
| **Fighting Game** | 2-player local fighter | Frame-data input buffer, hitbox/hurtbox Actors, rollback netcode stub |
| **Multiplayer Lobby** | Online game foundation | Steam lobby + LiteNetLib fully wired; ready-up UI; host migration on disconnect |
| **Mobile Touch** | Touch-first mobile game | Virtual joystick, tap/hold/swipe events, portrait/landscape toggle, ad slot stub |
| **Card Game** | Card hand / deck builder | Card Actor, hand layout component, drag-and-drop, zone drop targets, deck shuffle |

### Template Project Structure
```
MyGame/
  Assets/
    Scenes/
      Main.json          # Pre-built starter scene
    Scripts/
      PlayerController.js  # Annotated example script
    Sprites/               # Placeholder art (CC0)
    Sounds/                # Placeholder audio (CC0)
    Fonts/
      Default.ttf
  ProjectSettings.json     # App name, layers, input actions
  README.md                # Template-specific quick-start guide
```

---

## 21. Gameplay Framework

The layer above actors and components — who is playing, what body they control, what the
rules are. See the [gameplay framework wiki page](wiki/22-gameplay-framework.md).

```
GameInstance          one per process — survives every level load
 └── Subsystems       session-scoped services

Scene
 ├── GameMode         the rules. Spawns players, decides when the match ends.
 │    └── GameState   the facts. Elapsed time, player list, match phase.
 ├── PlayerController one per player — reads input, owns the camera
 │    └── PlayerState that player's score, name, team
 └── Pawn / Character the body a controller possesses
```

The controller is not the body: it outlives the pawn it drives, which is what lets a
player respawn into a fresh body while keeping their score, bindings and camera. The mode
is not the state: the mode holds rules and lives on the server, the state holds facts every
client reads.

| Type | Role |
|---|---|
| `GameInstance` | Session-wide state. Save data, profile, matchmaking. Survives level loads. |
| `GameInstanceSubsystem` | A singleton service for the session, created on first request |
| `WorldSubsystem` | The same, scoped to the scene and torn down with it |
| `GameMode` | Rules: spawning, respawn delay, score and time limits, win conditions |
| `GameState` | Replicated match facts: elapsed time, player list, phase |
| `PlayerController` | Turns input into pawn intent; owns the view camera |
| `PlayerState` | Per-player score, name, team, ping — survives death |
| `Pawn` | A possessable body |
| `Character` | A pawn that walks, wrapping `CharacterController3D` |
| `PlayerStart` | Marks a spawn point; the mode picks the one furthest from live players |

### Frame services

| Service | Use |
|---|---|
| `Time` | `DeltaTime`, `UnscaledDeltaTime`, `TimeScale`, `Fps`, frame count |
| `TimerManager` | `SetTimer(delay, cb, looping, firstDelay, useUnscaledTime)` |
| `CoroutineRunner` | `IEnumerator` sequences with `WaitForSeconds` / `WaitUntil` / `WaitWhile` |
| `ObjectPool<T>` / `ActorPool` | Allocation-free spawning for bullets and effects |
| `SBEvent` / `SBEvent<T>` | Multicast delegates safe to mutate mid-broadcast |
| `SBMath` | Framerate-independent `Damp`, angle wrapping, remapping, seeded random |
| `Bounds` | AABB with conservative transform, used for culling |

```csharp
Time.TimeScale = 0f;                                  // pause; input still runs
SBEngine.Instance.Timers.SetTimer(3f, Respawn);
StartCoroutine(FadeOut());                            // cancelled if the actor dies
actor.LifeSpan = 2f;                                  // self-destructing projectile
```

---

## 22. AI & Navigation

See the [AI wiki page](wiki/23-ai.md).

### Blackboard
Typed key-value state shared by a behaviour tree's nodes. `TryGetPosition` accepts either
a `Vector3` or an `Actor` under the same key, so one node handles both a fixed waypoint
and a moving target.

### Behaviour trees

| Category | Nodes |
|---|---|
| Composites | `Sequence`, `Selector`, `Parallel` |
| Decorators | `Inverter`, `Succeeder`, `Repeater`, `Cooldown`, `Condition` |
| Leaves | `ActionNode`, `ConditionNode`, `WaitNode`, `MoveToNode` |

Composites remember which child is running between ticks, so a long-running child does not
re-run its siblings every frame.

### AIController
Possesses a pawn and drives it from a tree. `CanSee(actor)` checks range, then field of
view, then raycasts from eye height to confirm nothing is in the way.

### Nav mesh
`NavMesh.Build(vertices, indices)` filters unwalkable slopes, welds coincident vertices so
separately authored floor pieces connect, normalises winding, and builds edge adjacency.

Pathfinding is A* over triangle adjacency to find the corridor, then the funnel algorithm
to pull the path taut against the corridor's edges — without that second step, paths
visibly zig-zag between triangle centres.

`NavMeshAgent` follows the result, optionally pushing movement through a `Character` so
collision and gravity still apply. Repathing is on a timer rather than per frame.

---

## 23. Localisation

`Loc` loads one flat JSON table per language and switches between them at runtime.

```csharp
Loc.LoadDirectory("Assets/Locales");
Loc.SetLanguageFromSystem();

label.Text = Loc.Get("ui.menu.start");
hud.Text   = Loc.Format("hud.score", ("score", 1200));
```

Placeholders are named (`{score}`) rather than positional, because translators reorder
values and `{0}` tells them nothing about what a slot holds. A key missing everywhere
returns the key itself, so an untranslated string is visible on screen rather than blank.

See the [localization wiki page](wiki/24-localization.md).

---

## 24. Testing & CI

```bash
dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj
```

`SexyBiscuit.Tests` is an xunit suite covering the logic that can be tested without a GPU:
maths and damping, bounds and transform hierarchies, actor lifecycle, timers, coroutines,
pooling, events, possession, subsystems, blackboards, behaviour trees, nav mesh building
and pathfinding, scene serialization round-trips, and localisation.

The suite also covers the MCP layer: the JSON-RPC transport over real HTTP, the tool
registry and schema generation, the scene tools and undo stack, C# project generation and
the assembly loader, and the Claude Code stream-json plumbing.

[CI](.github/workflows/ci.yml) builds the engine, editor and demo and runs the tests on
Windows, macOS and Linux, runs the editor's headless tool dump as a smoke test, and builds
the engine a second time with `-warnaserror` to keep it warning-free.

---

## 25. AI Assistant & MCP

The editor is an MCP server (`http://127.0.0.1:7331/mcp/`, Streamable HTTP, JSON-RPC 2.0)
and it runs Claude Code. Claude builds and changes a game inside the running editor while
you watch: it spawns actors and primitives, sets materials, moves the camera, takes
screenshots of the viewport, writes C# and hot-reloads it, plays and stops the game, and
asks you through `ask_user` when a decision is expensive to reverse. Every mutating call
takes an undo snapshot first.

Two ways in: the **Assistant panel** spawns and drives a Claude Code process itself
(autonomous by default, with Stop, a viewport banner and an activity log as the safety
net), or a Claude Code in a terminal connects through the project's `.mcp.json` and talks to
you in the editor with `say`, `ask_user` and `wait_for_user`.

The C# side: `create_code_project` gives a project a `.csproj` with Unreal-style starters
(`GameMode`, `PlayerController`, `Character`), `reload_game_code` hot-swaps the assembly in a
collectible `AssemblyLoadContext` with the scene round-tripped through JSON, and
`rebuild_engine_and_restart` rebuilds the engine and editor from source, restarts the
editor into the same project and scene, and resumes the Claude session.

```bash
dotnet run --project SexyBiscuit.Editor -- --assistant-selftest --dry-run   # find the binary, print the command line
dotnet run --project SexyBiscuit.Editor -- --dump-mcp-tools --markdown        # the tool catalogue
```

See the [AI Assistant & MCP wiki page](wiki/25-ai-assistant-mcp.md) and
[Tutorial 20](tutorials/20-building-a-game-with-claude.md).

---

## 26. HTML5 / Web

`html5/` is a JavaScript port of the engine, with a class-based API mirroring the
C# one. It is not a converter and not an export format: **the same project files
open in both engines**. A `.scene` saved in the HTML5 editor loads in the C#
editor and the other way round.

Runs on desktop, iOS and Android browsers. No build step and no dependencies:
plain ES modules served over HTTP.

```bash
node html5/tools/serve.js
#   editor   http://localhost:8080/html5/editor/
#   player   http://localhost:8080/html5/runtime/
```

### The API

```js
import { Component, registerComponent } from './html5/src/index.js';

class Spinner extends Component {
    static schema = { degreesPerSecond: { type: 'number', default: 90 } };

    constructor() { super(); this.degreesPerSecond = 90; }

    update(dt) {
        this.transform.localRotation += this.degreesPerSecond * Math.PI / 180 * dt;
    }
}
registerComponent(Spinner, { category: 'Project', source: 'project' });
```

Same classes, same lifecycle, JavaScript casing. `static schema` replaces
reflection: it is what the serialiser writes, what the inspector draws and what a
tool can list, from one declaration. `installCSharpAliases()` adds PascalCase
aliases if you would rather transliterate C# than rewrite it.

### What is shared

| File | |
|---|---|
| `*.scene` | Read and written, both the canonical and hand-authored forms |
| `ProjectSettings.json` | Read, case-insensitively, in either casing |
| `Scripts/*.js` | Run — see below |
| `*.sbproject`, action map JSON | Read |
| `*.csproj`, `Source/**`, `bin/**` | Not readable in a browser |

A project's C# components can never resolve in a browser, so they are kept as
placeholders: the type name and its property JSON preserved verbatim and written
back untouched on save. That is what lets the HTML5 editor open, edit and save a
scene from a C# project without destroying it.

### Scripting

Project `.js` files run unchanged. The Jint bridge and the browser bridge implement one
contract, `html5/src/scripting/bridge-api.json`, member for member — `log()`,
`Input.isKeyHeld`, `actor.getComponent`, `Scene.createActor`, all twelve collision and
trigger hooks — and a test on each side holds its bridge to the file. Every bundled template
runs under both engines, and a smoke test on each side keeps it that way.

### Exporting

`BuildPlatform.Web` stages the runtime beside the project and writes the page
that boots it. The output is static files; serve them from anything.

Full detail, including the API mapping table, what differs by necessity, and the
three bugs this port found and fixed in the C# engine, is in
[`html5/README.md`](html5/README.md) and the
[HTML5 wiki page](wiki/26-html5.md).

---

## 28. The CookieJar

Every game used to start from nothing: the character controller, the input map, the scoring loop
typed again into a project nobody else could reuse. The CookieJar is where that work goes instead.

A **cookie** is one reusable module — some C# or JavaScript, whatever assets and scene fragments it
needs, and an `AGENT.md` saying how to wire it up. A **jar** is a folder or a git repository of
them; the engine ships its own in `CookieJar/`. Installing copies a cookie into the open project,
repoints its asset references, builds it, and records every file it wrote with a hash in
`CookieJar.lock.json` — so removing it later deletes its own work and keeps anything you have
edited since.

The assistant is told to look there first, which is the point: finding a module costs about thirty
tokens, and deriving one again costs thousands, every game.

```bash
dotnet run --project SexyBiscuit.Editor -- --dump-mcp-tools --markdown | grep cookie
```

Installing a cookie compiles and runs its code, so a cloned jar is only fetched once you trust it
in the editor's Cookie Jar panel. No tool can clone, enable or trust a jar.

See the [CookieJar wiki page](wiki/28-the-cookiejar.md) and [`CookieJar/README.md`](CookieJar/README.md).

---

## Dependency Summary

| System | Library |
|---|---|
| Engine foundation | MonoGame 3.8+ |
| JS scripting (default) | Jint |
| JS scripting (optional) | ClearScript / V8 |
| 2D Physics | Aether Physics 2D |
| 3D Physics | Bepu Physics v2 |
| 3D model import | Assimp.NET |
| Font loading | FontStashSharp |
| Audio (streaming) | NVorbis · NAudio |
| Networking | LiteNetLib |
| Steam integration | Steamworks.NET |
| Editor UI | ImGui.NET |
| JSON | System.Text.Json |
| Tests | xunit |

---

## Licence

MIT — see [LICENSE](LICENSE). No runtime fees, no restrictions on commercial use of games
built with it.

Third-party dependencies carry their own licences; see the dependency table above. Note
that Steamworks.NET requires a Steamworks partner agreement to ship, and Spine (if you add
it) is separately licensed by Esoteric Software.
