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

### UI scrolls with the camera

`Canvas.Draw` runs inside the camera-transformed scene batch. Hide the `ui`
layer during the world pass and draw it in its own screen-space batch — see
[9. UI](09-ui.md#drawing-the-canvas).

### `Canvas` hit-testing ignores the scale matrix

`Canvas.Update` reads `Mouse.GetState()` in raw screen pixels while rendering
applies `GetScaleMatrix`. With `ScaleWithScreen` at a viewport that differs from
`ReferenceResolution`, clicks land in the wrong place. Use `PixelPerfect`, or
set `ReferenceResolution` to the actual back-buffer size.

### Widgets that render nothing

`Widget` provides `FillRect` / `StrokeRect` helpers over a shared 1×1 pixel, and
the newer widgets (`ProgressBar`, `TextInput`, `Checkbox`, `Dropdown`,
`ScrollView`, `TabView`) use them — they are visible with no assets. The older
ones are not:

- `Label` draws nothing when `Canvas.Font` is null.
- `Panel.BackgroundColor` is only a **tint for `BackgroundTexture`** — no
  texture, no fill.
- `Button` with no `NormalTexture` draws only its text.
- `Image` needs a `Texture`; `Slider` needs `TrackTexture` / `ThumbTexture`.

A 1×1 white texture solves all of them:

```csharp
var white = new Texture2D(GraphicsDevice, 1, 1);
white.SetData(new[] { Color.White });
```

Full table on [9. UI](09-ui.md#which-widgets-need-textures).

### `Widget.Bounds` ignores `AnchorMax`

Stretch anchors only take effect through `AnchorLayout.Apply(child, parent)`,
which rewrites `Position` and `Size`. `Bounds` on its own uses `AnchorMin`
plus the literal `Size`.

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
