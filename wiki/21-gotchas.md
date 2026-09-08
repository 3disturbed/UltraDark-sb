# 21. Gotchas & Known Mismatches

Every trap in this file was found by reading `SexyBiscuit.Engine/` and is listed
with the fix. Skim it once before you start; come back when something behaves
strangely.

> The engine is under active development. Each entry ends with a one-line
> **check** you can run to confirm it still applies.

---

## The big ones

### 1. Nothing draws — `SpriteBatch.Begin` is never called

`SBEngine.Draw` calls `SceneManager.Draw(SpriteBatch)` with no open batch, so the
first `SpriteRenderer.Draw` throws.

```
InvalidOperationException: Begin must be called successfully before you can call Draw.
```

**Fix** — override `Draw` and route through the engine's `Renderer2D`:

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);
    var scene = SceneManager.ActiveScene;
    if (scene == null) return;

    if (Config.Enable3D) Renderer3D.Render(scene);
    Renderer2D.RenderScene(SpriteBatch, scene, _camera);
    // no base.Draw(gameTime)
}
```

**Check** — `sed -n '/protected override void Draw(GameTime/,/^    }/p' SexyBiscuit.Engine/Engine.cs`

---

### 2. Physics silently off — `EnablePhysics2D` / `EnablePhysics3D`

`SBEngine` steps both simulations inside its fixed loop, but each is gated:

```csharp
if (Config.EnablePhysics2D) PhysicsSystem2D.Instance.FixedStep(step);
if (Config.EnablePhysics3D) PhysicsSystem3D.Instance.FixedStep(step);
```

Both default to `true`. If you turned one off to save the idle cost of a
simulation you were not using, and later add a `Rigidbody`, nothing moves.

Also note the ordering: **physics steps before `SceneManager.FixedUpdate`**, so
a force applied in `FixedUpdate` is simulated on the *next* step.

**Check** — `grep -rn --include='*.cs' 'FixedStep(' SexyBiscuit.Engine | grep -v 'Physics/PhysicsSystem'`

---

### 3. Tweens frozen — `Time.TimeScale`

`Tween.UpdateAll(Time.DeltaTime)` is called by the engine, on **scaled** time.
A pause implemented as `Time.TimeScale = 0` therefore freezes every tween —
including UI animations. Either drive UI motion off
`Time.UnscaledDeltaTime` yourself, or pause by deactivating gameplay layers
instead:

```csharp
foreach (var layer in scene.Layers)
    if (layer.Name != "ui") layer.Active = false;
```

**Check** — `grep -rn --include='*.cs' 'Tween.UpdateAll' SexyBiscuit.Engine`

---

### 4. `LoadScene` only reads files that exist

`SceneManager.LoadScene(path)` deserialises the file on the next `Update` when
it exists (`.scene` or `.json`, resolved against `ProjectPaths.Root`). When it
does not, you get the old behaviour — an **empty scene named after the file** —
plus a warning on stderr, so a typo in a path looks like a working but empty
level.

`SceneSerializer.LoadFromFile` + `SceneManager.AdoptScene` is the synchronous
route and throws on a missing file; prefer it when you want to know.

**Check** — `sed -n '/private void ProcessPendingLoad/,/^    }/p' SexyBiscuit.Engine/Core/SceneManager.cs`

---

### 5. `Awake` runs inside `AddComponent`

```csharp
private void AttachComponent(Component c)
{
    c.Actor = this;
    _components.Add(c);
    c.Awake();                 // ← immediately, before you can configure anything
    if (_started) c.Start();
}
```

Every "I set the property and nothing happened" bug below is a consequence of
this one line.

**Check** — `sed -n '/private void AttachComponent/,/^    }/p' SexyBiscuit.Engine/Core/Actor.cs`

---

## Consequences of `Awake`-on-attach

| Component | What is captured at `Awake` | Fix |
|---|---|---|
| `Rigidbody2D` | creates its Aether body at the actor's **current** position | set `Transform.Position` **before** `AddComponent` |
| `BoxCollider2D` etc. | builds the fixture from **default** `Size` / `Radius` / `Points` / `Offset` / `IsTrigger` / `Material` | set the properties, then `OnDestroy(); Awake();` to rebuild |
| `Rigidbody3D` | registers the collider's **default** shape; falls back to a 0.5 sphere with no collider | set the shape, `PhysicsSystem3D.Instance.RemoveBody(actor)`, then `rb.Awake()` |
| `ScriptComponent` | reads `ScriptPath`, which is still `""` | set `ScriptPath`, then call `script.Awake()` |
| `AudioSource` | checks `PlayOnAwake`, but `Clip` is still null | assign `Clip`, then call `Play()` |

Two helpers worth keeping in every project:

```csharp
public static class EngineExtensions
{
    /// Rebuilds a 2D collider's fixture after changing its shape.
    public static T Rebuild<T>(this T c) where T : Collider2D
    {
        c.OnDestroy();
        c.Awake();
        return c;
    }

    /// Attaches a JS script and actually starts it.
    public static ScriptComponent AddScript(this Actor a, string path)
    {
        var sc = a.AddComponent<ScriptComponent>();
        sc.ScriptPath = path;
        sc.Awake();
        return sc;
    }
}
```

---

## Naming and tag traps

### Three different camera tags

| Consumer | Tag it looks for |
|---|---|
| `Camera3D.Main` | `"MainCamera3D"` |
| `AudioSource` 3D listener | `"Camera"` |
| bundled scene templates | `"MainCamera"` |

None of them agree. Pick the tag the system you actually use requires, and if
you need two, put the second on a child actor parented to the camera.

**Check** — `grep -rn --include='*.cs' 'FindByTag("' SexyBiscuit.Engine`

### `Actor.Layer` vs `Actor.Layer_`

`Layer` is an `int` used as a physics raycast mask. `Layer_` is the `Layer`
object the actor lives in. They are unrelated.

### `Actor.OnStart` vs `Component.Start`

Actor subclasses override `protected virtual void OnStart()`. Components
override `public virtual void Start()`. Overriding the wrong name on the wrong
type compiles fine and never runs.

---

## Rendering

### `Layer.Draw` sorts actors by **local** Y

```csharp
var sorted = _actors.Where(a => a.IsActive).OrderBy(a => a.Transform.LocalPosition.Y);
```

Correct for top-down; wrong for a side-scroller, and wrong for children of a
moving parent (local, not world). Put content that must not be Y-sorted against
each other on separate layers, and use `SpriteRenderer.LayerDepth` for
fine-grained order.

### A canvas is painted by the host, not by `Component.Draw`

`UiCanvas` is a component but it does not draw through the component pass, which runs
inside the camera-transformed scene batch. The host collects every canvas and paints it
afterwards, in screen space, ordered by `UiCanvas.Order`. That is why a HUD does not pan,
zoom or shake with the camera — the UI this replaced did all three.

### A bar is a fraction, not a value

`UiNode.Value` on a `Bar` is 0 to 1. There is no `MaxValue`: divide it out yourself. A
health bar handed 87 out of 100 draws full, and nothing complains.

### An empty container does not take clicks

A node with no background and no texture is a layout row, and the hit test walks straight
through it to whatever is behind. That is deliberate — an invisible container that ate
clicks would be impossible to debug — but it means a panel you meant to be a click target
needs a `background`, even a transparent one.


### A black 3D scene is a missing camera

`RenderSystem3D.Render` returns immediately when `OverrideCamera` is null **and**
`Camera3D.Main` is null — and `Main` requires an enabled camera on an active
actor tagged exactly `"MainCamera3D"`.

---

## Physics

### Units are metres, everywhere

`FixedStep` copies Aether positions straight onto `Transform.Position`, and
default gravity is `(0, 9.8)`. An actor at `(400, 300)` is 400 metres out and
falls at ~1 pixel per second if you draw 1 unit as 1 pixel. Either work at
metre scale and set `camera.Zoom = 64`, or scale gravity by your
pixels-per-metre. See [6. Physics](06-physics.md#units--read-this-first).

### `Rigidbody3D` with no collider is a ball

`Rigidbody3D.Awake` falls back to a 0.5-radius sphere when the actor has no
`Collider3D`. Boxes that roll are this.

### Raycast masks use `Actor.Layer`, the int

`(layerMask & (1 << actor.Layer)) != 0`. If you never set `Actor.Layer`, every
actor is layer `0` and only `1 << 0` matches.

---

## Scripting

### `actor` has no `transform`

The bridge exposes `actor` (name/tag/active/destroy) and `transform`
(x/y/rotation/scale) as **two separate globals**. `actor.transform.x` is
`undefined` → `NaN`. Actor *proxies* returned by `Scene.find` do have
`.transform`.

### `Scene.findByTag` returns an array

```js
var found = Scene.findByTag("Player");
if (found.length === 0) return;
var target = found[0];
```

An array is never falsy, so `if (!target) return;` never fires.

### Only eight lifecycle hooks are dispatched

`onAwake`, `onStart`, `onUpdate`, `onFixedUpdate`, `onLateUpdate`, `onDestroy`,
`onCollisionEnter/Stay/Exit`, `onTriggerEnter/Stay/Exit` — all twelve are dispatched, and any
other top-level function is reachable through `getComponent("ScriptComponent").invoke(name)`.

### The bridge is a contract shared with the browser

`html5/src/scripting/bridge-api.json` lists every global and member; a test on each side
holds its bridge to the file, and the template smoke tests run every bundled script under
both engines. A member that exists on one side only is a failing build, not a runtime
surprise. See [11. Scripting](11-scripting.md#the-scripting-contract).

### Script errors go through ScriptDiagnostics

Failures are published by `ScriptDiagnostics.Report` and reach the editor console with their
level; with no subscriber they go to the process console, so a Release build still shows them.
`ScriptDiagnostics.Capture(list)` collects them in a test.

---

## Assets and scenes

### `AssetManager` handles four types

`Texture2D`, `SoundEffect`, `string`, `byte[]`. `SpriteFont`, `Effect` and
models go through other paths — see
[13. Assets](13-assets.md#supported-types--verified).

### Audio must be 16-bit PCM WAV

`SoundEffect.FromStream` is the only decoder wired up, despite NAudio being
referenced.

### Component type names are short by default

`SceneSerializer` writes `"type": "Camera2D"` and resolves short, full or
assembly-qualified names across every loaded assembly, so hand-written files can
use short names too. The trap is a **short name two loaded types share**: the
writer falls back to the full name for those, but a hand-written short name picks
whichever the scan finds first. A name that resolves to nothing becomes a
`MissingComponent` placeholder rather than a dropped component.

**Check** — `grep -n 'TypeNameStyle' SexyBiscuit.Engine/Scene/SceneSerializer.cs`

### Textures survive only through their path twins

`Texture2D`, `SoundEffect` and `Model` are not serialisable.
`SpriteRenderer.TexturePath`, `AudioSource.ClipPath`, `MeshRenderer.ModelPath` and
`Material3D`'s `*MapPath` properties are, and the asset loads from the path
after the scene does. Assign a texture directly and it is lost on save; set the
path and it round-trips. Your own components need the same pattern: a `string`
path plus a `Start` that resolves it.

**Check** — `grep -n 'TexturePath\|ClipPath\|ModelPath' SexyBiscuit.Engine/Rendering/SpriteRenderer.cs SexyBiscuit.Engine/Audio/AudioSource.cs SexyBiscuit.Engine/Rendering/MeshRenderer.cs`

### A `new Scene(...)` you drop leaks into static registries

`SceneManager` calls `Scene.Destroy()` when it replaces or unloads a scene. A
scene you built yourself with `new Scene(...)` — in a test, a tool, a headless
server — gets no such call, and components only leave their static registries
(`MeshRenderer.All`, `Light3D.All`, `Light2D.All`, `PlayerStart.All`,
`SkinnedMeshRenderer.All`, `ParticleSystem3D.All`) in `OnDestroy`. The dropped
scene stays visible to the renderer and to spawn selection forever.

```csharp
var scene = new Scene("Headless");
// … use it …
scene.Destroy();          // not optional
```

---

### The bundled `.scene` templates load now

Template scenes use the canonical format with short type names, which the
serialiser reads. The remaining mismatch is the camera tag (`"MainCamera"`, not
`"MainCamera3D"`); see [Three different camera tags](#three-different-camera-tags).

**Check** — `head -20 'Templates/3D Scene/Scenes/Main3D.scene'`

---

## Editor and assistant

### Actor ids change after undo, redo or load

The Assistant's tools address actors by id or name. Ids are assigned at
construction, and undo, redo, play-mode Stop and `load_scene` all rebuild the
scene from JSON, so every id changes. Re-query with `get_scene_summary` or
`find_actors` instead of remembering ids; names are stable, and an ambiguous
name comes back with the candidates.

**Check** — `grep -n 'ids change' SexyBiscuit.Engine/Mcp/Tools/SceneResources.cs`

### Reach services through `EngineHost.Current`, not `SBEngine.Instance`

`SBEngine.Instance` is the standalone game window and is null when the editor
hosts the engine. Engine code that looked services up through it (player and
camera controllers reading input, `AudioSource`, `Prefab.Instantiate`,
`WorldStreamer`, the script bridge, cursor show/hide) silently did nothing or
threw in editor play mode — a first-person controller never got its input.
Everything now goes through `EngineHost.Current`, which both hosts set; game code
should do the same. `SBEngine.Instance` stays for code that really needs the
window.

**Check** — `grep -rn 'SBEngine.Instance' --include='*.cs' SexyBiscuit.Engine | grep -v '///'`

### Actors Start in edit mode too — gate gameplay on `PlayMode.IsActive`

The editor flushes pending actors every frame so the outliner and the renderer
see them, which runs `OnStart` / `Start` at edit time. Caching a component there
is fine; changing the world is not: `GameMode` used to spawn its game state and
players into every loaded, undone or hot-reloaded scene, and they were saved
with the level. `GameMode.OnStart` now returns early unless
`PlayMode.IsActive`, which the editor turns on only while Play runs a fresh copy
of the scene. Do the same in your own Start-time spawners; standalone games never
touch the flag (it defaults to true).

**Check** — `grep -n 'PlayMode.IsActive' SexyBiscuit.Engine/Gameplay/GameMode.cs SexyBiscuit.Editor/EditorApp.cs`

### Tool calls during play mode are discarded on Stop

Every mutating tool still runs while the scene is playing, but Stop restores the
pre-play snapshot, so the result carries a warning that the change is temporary
and no undo snapshot is taken. Stop first, then edit.

**Check** — `grep -n 'IsPlaying' SexyBiscuit.Editor/Assistant/McpHost.cs`

### Claude Code must be signed in for the binary the editor runs

The embedded assistant runs a standalone `claude` binary with its own
credentials; being signed in to the Claude desktop app does not count. The panel
shows **Claude Code is not signed in** with a button that opens a terminal —
type `/login` there once, then **Resume**.

**Check** — `dotnet run --project SexyBiscuit.Editor -- --assistant-selftest`


## Build and shipping

### `STEAMWORKS` is defined in every configuration

Including `Release`. Remove it from `DefineConstants` in
`SexyBiscuit.Engine.csproj` if you are not shipping on Steam.

### Publishing needs the engine source and the .NET SDK

A desktop build compiles the engine for the target runtime, so `sbengine` needs the
engine checkout (run it from inside one, or set `SEXYBISCUIT_REPO`) and `dotnet`. A
staged-only export (`--no-publish`, or the editor's Build button) needs neither. See
[18. Build & Export](18-build-export.md#publishing-without-c).

### An archive made on Windows loses the executable bit

Linux and macOS builds are packaged as `.tar.gz` for that reason; a `.zip` of a Unix
binary made on Windows unpacks without `+x`. The release workflow builds each target on its
own OS.

### The editor's build is a staged export

The Build Settings panel stages content only; **Build & Run** publishes as well. The
platform folder goes under `outputDirectory` once — a `BuildSettings.json` that saved
`dist\Windows_x64` is read as that folder, not doubled.

### Android and iOS are validated, not built

No manifest generation, no APK/AAB packaging, no Xcode project. The pipeline
checks for a keystore path and a team id, then stages content. The installable web build
is the mobile build until a native target exists.

### Saves default to the working directory

`SaveManager.SaveDirectory` is `"Saves/"` and `PlayerPrefs.PrefsPath` is
`"Saves/prefs.json"` — both relative. Point them at a per-user directory before
you ship.

### `PlayerPrefs.Save()` is not automatic

Nothing is written until you call it.

### `InputManager.LoadBindings` throws on a missing file

Guard with `File.Exists`.

---

## Design-doc-only features

Described in the root `README.md`, not present in the source at the time of
writing:

- Automatic `steamcmd` upload
- Android manifest / keystore manager / APK packaging
- iOS Xcode project generation and icon scaling
- `async` Steam lobby API (`await SteamLobby.CreateLobby(...)`, filter builders)
- Prefab editor, animator editor, tilemap painter panels

The root `README.md` has been partly corrected since — it now marks Spine as not
implemented, for instance — so cross-check it rather than assuming either
document is stale. Where the two disagree, **the source is right**; every entry
on this page carries a check you can run.

---

## Next

- [3. The Game Loop](03-game-loop.md) — what the frame does, and what it leaves you.
- [Tutorials](../tutorials/README.md) — the same knowledge, applied.

---

## Shipped through a green board

Post-mortems moved here from `AGENTS.md` on 2026-09-08. Every check in this repository reads text; none of
them can see a picture. The rule that survives in the agent guide is: look at one frame before you
believe a build.

### The one thing no gate can see

Every check in this repository reads text. The validator reads the scripting contract, `npm test`
and `dotnet test` read behaviour, a headless soak reads script errors. **Not one of them can see a
picture**, and the rule further up — "the validator and the tests are the checker, not
screenshots" — is about cost, not about coverage. It buys cheap iteration; it does not tell you
the game is visible.

Two defects shipped through a fully green board on 2026-09-06, both invisible to every gate:

- The browser's SpriteRenderer draws a tinted box when an actor has no texture, which is what
  makes a template visible before it has art. **C# had no such property and no such box**: it
  returned early on a null texture, so a native build of any bundled template was an empty
  cornflower-blue window. Fixed, and pinned by `ComponentSchemaParityTests`.
- A game laid out against the wrong `layerDepth` direction drew its ground over the whole city, in
  a build where every script ran, every test passed and the validator said `OK`.

So: **look at one frame before you believe a build.** On a headless box that costs nothing and
needs no browser and no screenshot tool —

```bash
Xvfb :99 -screen 0 1280x720x24 -fbdir /tmp/fb &     # -fbdir maps the framebuffer to a file
DISPLAY=:99 ./YourGame &                            # let it run ten seconds
python3 -c "from PIL import Image; from collections import Counter; \
raw=open('/tmp/fb/Xvfb_screen0','rb').read(); \
img=Image.frombytes('RGBA',(1280,720),raw[:1280*720*4],'raw','BGRA').convert('RGB'); \
img.save('/tmp/shot.png'); print(Counter(img.getdata()).most_common(3))"
```

One number tells you most of it: if a single colour is 99% of the screen, nothing is drawing, or
one thing is drawing over everything. `(100, 149, 237)` is MonoGame's default clear — that is an
empty window, not a dark game.

### A bare collider is scenery, and scenery must not fall

Every template builds its world out of actors carrying a SpriteRenderer and a collider and nothing
else — walls, crates, parked cars — and their comments call that "static geometry on both engines".

The browser makes it true by never integrating a collider that has no body. This engine
auto-created a `Rigidbody2D`, and a fresh one is **Dynamic**, so in a native build all of that
scenery accelerated off the bottom of the screen. The player and the enemies looked fine, which is
what made it confusing: their scripts set a velocity every frame, and that overwrites an
accumulating fall.

`GravityScale = 0` is not the fix — Aether has no per-body gravity and the property is stored for
game logic only. The auto-created body is now kinematic, which Aether does respect
(`BareColliderTests`).

### layerDepth sorts on both engines, and higher is nearer

Both engines draw **ascending, so the highest depth is in front**. MonoGame's constant names
mislead here: `BackToFront` draws the *highest* depth first, which lands it at the *back* — the
exact inverse of the browser — so `RenderSystem2D` opens its batch with **`FrontToBack`**.

This section used to say the native renderer did not sort at all. That was true, it was fixed, and
the note outlived the fix by a day, during which it was telling people to **create their actors
back-to-front** instead. That is not merely stale, it is unusable advice: a game that sorts by
*position* — anything top-down where you walk in front of one wall and behind the next — cannot be
laid out in creation order, because an actor is created once and the relationship changes with
every step.

Re-measured with `Games/DepthProbe` on a native Linux build, 2026-09-07: a red sprite created
FIRST at depth 0.90 fills the window over a blue one created after it at 0.10.
`spriteSortMode.test.js` now reads the sort mode out of `RenderSystem2D.cs` and fails on anything
but `FrontToBack`. If you ever doubt it again, run the probe — it takes four minutes.

### 2.5D: one rule, and a pivot

A flat top-down game gives each *kind* of thing a fixed depth, and reads as a diagram. The fix is
one rule: things that lie flat keep a band, things that **stand up** sort by where their feet are.

Height is a `Pivot`, never a position. One sprite sized `[w, h + height]` with a pivot that hangs
it upward from the footprint's south edge is the whole extrusion; the actor, and therefore its
collider, never moves. Lift the actor instead and you walk straight through the wall you can see.

`Pivot` is not listed in `bridge-api.json`, and does not need to be: `Scene.addComponent` matches
property-bag keys case-insensitively against the component's schema on both engines, so **any**
serialised property is settable from script.

Three failures this shape produces, none of which any gate catches:

- Anything that subdivides or measures a standing object must read its **collider**, not its
  sprite. The sprite is the extruded face and is taller than the thing is deep, so rubble from a
  shattered wall scatters up the screen into the air above the hole.
- A top face is a separate actor with no collider and no script, so nothing on it can notice that
  its wall has gone. Every hole leaves a roof hanging over it unless caps are tracked.
- Full-screen overlays parked at a comfortable-looking `0.8` are now in the **middle** of the
  standing band, so half the map sorts over the top of the dark and stands in full daylight at
  night.

The `depth-2-5d`, `breakable-walls` and `top-down-driving` cookies carry all of this.

### The renderer culls, and the cost of 2.5D is fill rate

`SpriteRenderer` skips anything that provably cannot touch the viewport; a pass with no camera
(a tool, a test, an identity transform) culls nothing. The bound is the distance to the furthest
**corner** and respects the pivot — measuring from the centre culls an extruded wall while the
visible half of it is still on screen, which reads as flickering geometry rather than as a bug in
culling.

Do not expect culling to pay for height. On the Jake01 city under Xvfb software rendering it moved
9,462 actors from 38 fps to 45, against 51-65 for the same map flat. Most of what 2.5D costs is
**fill rate** — a wall face is three times the pixels it was — and fill rate is what a GPU is for.
Trimming actor counts barely touches it.


---

## Prototype field notes

Written after building Jake01 from the Survival Crafting template in one sitting, and moved here
from `AGENTS.md` on 2026-09-08. The rules in the guide say what to do; these are the things that cost hours
anyway.

### Four things that pass every check and are still wrong

**An actor proxy has no `addComponent`.** `Scene.createActor` returns
`{id, name, tag, active, transform, getComponent, destroy}` — that is the whole proxy. Use
`Scene.addComponent(proxy, "SpriteRenderer", {…})`. The bundled **Survival Crafting** template
calls `enemy.addComponent(...)` on a spawned actor, so its night waves throw
`enemy.addComponent is not a function` on the browser engine. Nothing catches it because the
template smoke test stops long before nightfall.

**`layerDepth`: LOW is drawn first and ends up at the BACK.** `SpriteBatch.end()` sorts ascending
and draws in that order. The comment on `SpriteSortMode.BackToFront` says "high layerDepth first"
and is the opposite of what the code does. Believing it inverted every depth in Jake01 — the road
at 0.95, drawn last, over all 234 sprites. On screen that is a flat grey rectangle with no error
anywhere, 106/106 tests green and a happy validator. **No bundled template sets `LayerDepth` at
all**, so there is nothing to copy the convention from and nothing to catch it.
`Games/Jake01/tools/draw-order.mjs` is a check worth stealing: sort the sprites the way the
renderer will and fail if any layer is not strictly behind the next.

*Unverified but worth knowing:* the C# renderer passes `LayerDepth` to MonoGame's
`SpriteSortMode.BackToFront`, whose convention is the reverse. If that is right, a scene using
`LayerDepth` renders inside-out between the two engines, and no test on either side references it.

**A full-screen overlay is `width: "*"`, not a panel resized every frame.** The layout engine
fills the parent for you, through a resize, a rotation or going fullscreen. Sizing something from
`UI.width` each frame is the shape this had before there was a tree, and it is now a per-frame
loop that does nothing.

**The bundled smoke test proves less than it looks like.** It runs sixty frames with no physics
host and no asset loader — so it never reaches a day/night transition, and it never loads a script
attached at runtime, which is how most things get spawned. Give the scene a real
`PhysicsSystem2D` (gravity `{x: 0, y: 0}` for top-down, or everything falls off the map) and an
`engine.assets.loadText` backed by the filesystem, and run for minutes of game time.
`Games/Jake01/tools/soak.mjs` is the pattern; it found the `addComponent` bug in seconds.

### Cross-script calls: primitives only

`getComponent("ScriptComponent").call(name, ...args)` — `call` and `invoke` are the same function.
Pass and return **numbers, strings and booleans**. They marshal identically under Jint and in the
browser; objects and arrays do not reliably. So a HUD asks for `getHealth01()` rather than a
`getStats()` that returns an object, and a loot table answers with an integer code rather than
`{type: "wood"}`. It reads as more functions and it is the difference between working on both
engines and working on one.

### Smaller things, each of which cost a few minutes

- `Physics.overlapCircle` **does not return triggers**. Anything you want to find with it needs a
  non-trigger collider. This is also why Survival Crafting's `onTriggerEnter` gathering never
  fires: its resource nodes are plain colliders, so `nearbyResource` stays null forever.
- A collider with **no `Rigidbody2D` is static geometry** on both engines — that is how you build
  walls.
- **`Input.joystickX` throws when there is no touch state.** The bridge reads
  `input()?.touch.leftJoystick.value.x` and only guards the first hop, so a headless input stub
  needs a `touch: { leftJoystick: { value: { x: 0, y: 0 } } }`. The browser is fine.
- The export's **service worker precaches everything** and uses `skipWaiting` + `clients.claim`,
  so one reload picks up a new build — but the cache name carries the commit, so **commit before
  you export** or the name does not change.
- Keep every tuning number in a labelled block at the top of its script. The feedback loop on a
  prototype is "make it faster", and that should be a one-line diff, not a search.
