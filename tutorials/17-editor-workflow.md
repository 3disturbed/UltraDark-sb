# Tutorial 17 — Editor Workflow

**You will learn:** what the editor does today, where it will bite you, and the
code-first workflow that is currently more reliable. **Time:** ~20 minutes.

---

## 1. Running it

```bash
dotnet run --project SexyBiscuit.Editor
```

**Cross-platform.** The editor targets plain `net8.0` — MonoGame plus ImGui,
both of which run everywhere. It used to need `net8.0-windows` for WinForms file
dialogs and the clipboard; those now go through the editor's own `FileDialog`
and `DesktopShell`.

It opens on the **project launcher**, which is modal in spirit: while it is up,
nothing else is drawn, because no project is loaded. Three tabs — Recent
Projects, New Project, Open Project.

The recent list lives in your per-user application data directory
(`%APPDATA%/SexyBiscuit/recent-projects.json` or the equivalent), so it survives
a rebuild, and entries whose project has moved or been deleted are dropped on
load.

Once a project is open, the window is one ImGui dockspace: drag panels by their
tabs to rearrange, and the layout persists in `imgui.ini` next to the
executable.

| Hotkey | Action |
|---|---|
| <kbd>F5</kbd> / <kbd>F6</kbd> / <kbd>F7</kbd> | Play / Pause / Stop |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> | Save scene |
| <kbd>G</kbd> / <kbd>R</kbd> / <kbd>S</kbd> | Translate / Rotate / Scale gizmo (hovering the viewport) |

## 2. The panels

All ten are wired in.

**Hierarchy** — the scene tree grouped by layer. `+ Actor` and `+ Layer`
buttons; right-click a layer for Add Actor / Delete Layer, an actor for
Rename / Duplicate / Delete. Selection is published through
`EditorState.SelectActor`.

**Inspector** — name, tag, active, and a Transform section with drag fields.
**Rotation is displayed in degrees** and converted to radians on write, while
`Transform.LocalRotation` stores radians. Each component gets a collapsible
header and its properties are enumerated by reflection:

| Type | Widget |
|---|---|
| `float`, `int` | drag field |
| `bool` | checkbox |
| `string` | text field |
| `Vector2` / `Vector3` | two or three labelled drag fields |
| `Vector4` | a four-component drag field |
| `Quaternion` | three drag fields as **Euler degrees** (Pitch, Yaw, Roll) |
| `Color` | RGBA picker |
| `enum` | combo box |

Quaternions are edited as Euler because nobody can reason about raw xyzw, and
the fields only write back when a drag actually changes something — writing
every frame would mark the transform dirty continuously and defeat the engine's
lazy world-transform cache. Types outside the set are listed but not editable,
the same constraint as
[scene serialisation](10-scenes-and-prefabs.md#3-workflow-b--scene-files).
An **Add Component** search box lists every non-abstract `Component` subclass.

**Viewport** — the engine rendering into a `RenderTarget2D`. Right-drag pans,
scroll zooms toward the cursor, and <kbd>G</kbd>/<kbd>R</kbd>/<kbd>S</kbd>
switches gizmo mode. Toggle **View → 3D Viewport** to render through the 3D
pipeline; it prefers the scene's own `Camera3D.Main` so the viewport shows what
the game will, and falls back to an editor camera that is deliberately not part
of the scene.

**Asset Browser** — browses `EditorState.ProjectPath` with image thumbnails.
Right-click for **Reveal in File Manager** and **Copy Path**.

**Console** — `EditorTraceListener` is registered on
`System.Diagnostics.Trace.Listeners`, so every `Debug.WriteLine` from engine and
game code lands here, including `[Script]` output from `Debug.log` in
JavaScript.

**Build Settings** — a form over `PlatformConfig`, saved to JSON.

**API Reference · Code Editor · Git · Render Stats** — toggled from the View
menu. Render Stats shows what `RenderSystem3D` submitted last frame: renderers
drawn/culled/total, draw calls, triangles, active lights, shadow casters.

### The Create menu

The reason to open the editor at all for 3D work. Building a scene by hand means
adding a bare `Actor`, then a `Transform3D`, then a `Camera3D`, then remembering
the `"MainCamera3D"` tag. Each preset is one click, correctly configured, and
selected so the Inspector opens on it.

| Submenu | Presets |
|---|---|
| *(top level)* | Empty Actor · Empty Actor (3D) |
| **3D Object** | Mesh · Skinned Mesh · Particle System · Skybox |
| **Light** | Directional · Point · Spot · 2D Light |
| **Camera** | Camera 3D · Camera 3D + Fly Controls · Camera 2D |
| **Gameplay** | Game Mode · Character · Player Start · AI Character |
| **UI** | Canvas · World Canvas |
| **2D Object** | Sprite · Tilemap · Particle Emitter |

The presets encode the setup the rest of these tutorials tell you to remember:
**Camera 3D** carries `"MainCamera3D"`, **Directional** gets sensible
`EulerAngles`, **AI Character** gets a `NavMeshAgent` with
`DriveCharacter = true`, **Skinned Mesh** pairs `SkeletalAnimator` with
`SkinnedMeshRenderer`.

New actors land on the selected layer, or `"default"`.

## 3. Play mode — what it actually runs

```csharp
if (EditorState.IsPlaying && !EditorState.IsPlayPaused && _engineInitialized)
    _engine?.SceneManager.ActiveScene?.Update(dt);
```

**Only `Update`.** No `FixedUpdate`, no `LateUpdate`, no physics stepping, no
tween updates. Components whose logic lives in `FixedUpdate` — including
`CharacterController2D` and everything in
[Tutorial 6](06-physics-platformer.md) — will look completely inert.

Drawing is a plain `SpriteBatch.Begin()` / `End()` with **no camera transform**,
so `Camera2D` has no effect on the viewport.

So editor play mode is useful for checking **layout and static appearance**, not
for testing gameplay. Test gameplay by running your game.

## 4. One bug to know about

### Play-mode restore discards the scene

```csharp
// EditorApp.ExitPlayMode
var restored = SceneSerializer.Deserialize(_sceneSnapshot);
_engine.SceneManager.CreateScene(restored.Name);      // ← a NEW EMPTY scene
```

`restored`'s actors are never installed. **Press Ctrl+S before F5.**

`SceneManager.AdoptScene` was added for exactly this case, and the Open Scene
path already uses it. Play mode is the one call site that was missed — a
one-line fix:

```csharp
var restored = SceneSerializer.Deserialize(_sceneSnapshot);
_engine.SceneManager.AdoptScene(restored);
```

### Opening a scene works

This used to have the same bug. It does not any more:

```csharp
FileDialog.OpenFile("Open Scene", EditorState.ProjectPath, new[] { ".scene", ".json" }, path =>
{
    var loaded = SceneSerializer.LoadFromFile(path);
    _engine?.SceneManager.AdoptScene(loaded);
});
```

`AdoptScene(scene)` destroys the previously active scene, re-adds every
`DontDestroyOnLoad` actor, installs the new scene, and raises the load/unload
events. Use the same pair in your own code —
[Tutorial 10](10-scenes-and-prefabs.md#the-loader-bridge).

## 5. Scene format

Save and Open go through `SceneSerializer`, which uses the
[canonical format](10-scenes-and-prefabs.md#3-workflow-b--scene-files):
`position` / `rotation` / `scale` arrays and assembly-qualified component type
names.

The files in `Templates/*/Scenes/` use a **different** shape —
`"transform": {x, y, …}` and short type names — which this serialiser does not
read. Treat them as design references.

## 6. Project templates

`Templates/` holds 15 starter projects:

| Template | Category |
|---|---|
| Empty | General |
| Hello World | Tutorial |
| 2D Platformer, Top-Down RPG | 2D |
| 3D Scene | 3D |
| Twin-Stick Shooter, Fighting Game | Action |
| Endless Runner | Arcade |
| Puzzle Match3 | Puzzle |
| Racing | Racing |
| Survival Crafting | Survival |
| Tower Defense | Strategy |
| Visual Novel | Narrative |
| Multiplayer, Multiplayer Arena | Multiplayer |

Each has `template.json`, `ProjectSettings.json`, `Scenes/` and `Scripts/`.

```csharp
string projectPath = ProjectFile.CreateNew("MyGame", @"C:\Games\MyGame",
                                           @"...\Templates\2D Platformer");
```

`CreateNew` builds the folder skeleton (`Assets/Sprites`, `Assets/Audio`,
`Scripts`, `Scenes`), copies the template without overwriting, reads
`StartScene` from `ProjectSettings.json`, and writes `<name>.sbproject`:

```json
{
  "ProjectName": "MyGame",
  "EngineVersion": "1.0.0",
  "DefaultScene": "Scenes/Main",
  "AssetDirectories": ["Assets"],
  "ScriptDirectories": ["Scripts"],
  "BuildConfigPath": "BuildSettings.json"
}
```

> **Template scenes and scripts are design references, not runnable content.**
> Their `.scene` shape and their JavaScript both target a wider API than the
> engine implements — see [Tutorial 5 §1](05-javascript-scripting.md#1-what-the-script-api-actually-is).
> Read them for structure; write against the real API.

## 7. The code-first workflow

What these tutorials use, what the demo project uses, and what works on every
platform.

### Fast iteration without the editor

**Scripts hot-reload.** Edit a `.js` and see the change immediately —
[Tutorial 5 §6](05-javascript-scripting.md#6-hot-reload).

**Assets hot-reload.** In `Debug` and `Development` builds, `AssetManager`
watches every loaded file. Edit a PNG and the sprite updates.

**Gizmos and overlays.** <kbd>F1</kbd> for the debug overlay; `Gizmos` for
collider and path visualisation — [Tutorial 6 §11](06-physics-platformer.md).

**Tune from a file.** For values you change constantly, read them from JSON and
reload on a key:

```csharp
public sealed class Tuning
{
    public float PlayerSpeed = 320f;
    public float JumpForce   = 780f;
    public float Gravity     = 1800f;

    public static Tuning Current = new();

    public static void Reload(string path = "Assets/tuning.json")
    {
        if (!File.Exists(path)) return;
        try { Current = JsonSerializer.Deserialize<Tuning>(File.ReadAllText(path)) ?? Current; }
        catch (Exception ex) { Debug.WriteLine($"tuning reload failed: {ex.Message}"); }
    }
}
```

```csharp
#if DEBUG
if (Input.IsKeyPressed(Keys.F8))
{
    Tuning.Reload();
    PhysicsSystem2D.Instance.Gravity = new Vector2(0, Tuning.Current.Gravity);
    Debug.WriteLine("tuning reloaded");
}
#endif
```

Ten minutes of setup, and you stop rebuilding to change a number.

### Save the scene you built

Build a level in code, then dump it to JSON and iterate on the file:

```csharp
#if DEBUG
if (Input.IsKeyPressed(Keys.F9))
{
    SceneSerializer.SaveToFile(SceneManager.ActiveScene!, "Scenes/Level1.scene");
    Debug.WriteLine("scene saved");
}
#endif
```

Remember that textures do not round-trip — use the
[`SpriteLoader` pattern](10-scenes-and-prefabs.md#the-asset-path-pattern).

### An in-game inspector

Fifty lines gets you most of what the Inspector panel offers, on every platform:

```csharp
using SexyBiscuit.Engine.Debug;

public sealed class DebugInspector : Component
{
    public Actor? Selected;

    public override void Update(float dt)
    {
        var engine = SBEngine.Instance;
        if (!engine.Input.IsMouseButtonPressed(MouseButton.Left)) return;

        var camera = Actor.Scene?.FindByName("Main Camera")?.GetComponent<Camera2D>();
        if (camera == null) return;

        var world = camera.ScreenToWorld(engine.Input.MousePosition, engine.GraphicsDevice);
        Selected = null;
        float best = float.MaxValue;

        foreach (var layer in Actor.Scene!.Layers)
        foreach (var a in layer.Actors)
        {
            float d = Vector2.Distance(a.Transform.Position, world);
            if (d < 40f && d < best) { best = d; Selected = a; }
        }

        if (Selected == null) return;

        Debug.WriteLine($"--- {Selected.Name} [{Selected.Tag}] id={Selected.Id}");
        Debug.WriteLine($"    pos {Selected.Transform.Position}  " +
                        $"rot {MathHelper.ToDegrees(Selected.Transform.Rotation):F1}°  " +
                        $"scale {Selected.Transform.LocalScale}");
        foreach (var c in Selected.GetAllComponents())
            Debug.WriteLine($"    + {c.GetType().Name} enabled={c.Enabled}");
    }

    public override void LateUpdate(float dt)
    {
        if (Selected is { IsDestroyed: false })
            Gizmos.DrawBox2D(Selected.Transform.Position,
                             Selected.Transform.LocalScale + new Vector2(6, 6),
                             Color.Yellow);
    }
}
```

## 8. When to use which

| Task | Editor | Code |
|---|---|---|
| Placing static level geometry | ✅ good fit | verbose |
| Assembling a 3D scene | ✅ the Create menu | four steps per actor |
| Tuning component values visually | ✅ Inspector | rebuild, or a tuning file |
| Inspecting render cost | ✅ Render Stats | `Profiler` |
| Testing gameplay | ❌ no `FixedUpdate`/physics | ✅ just run it |
| Iterating on behaviour | ❌ | ✅ script hot reload |
| Wiring events and assets | ❌ does not serialise | ✅ |
| Configuring builds | ✅ Build Settings | ✅ `PlatformConfig` |

The hybrid is now the good answer, because `AdoptScene` closed the loop: **lay
scenes out in the editor and save them**, then load them in code and attach
behaviour, assets and events there.

```csharp
protected override void OnEngineReady()
{
    var scene = SceneManager.AdoptScene(
        SceneSerializer.LoadFromFile("Scenes/Level1.scene"));

    // Everything the file cannot carry:
    foreach (var enemy in scene.FindByTag("Enemy"))
    {
        enemy.GetComponent<SpriteRenderer>()!.Texture =
            Assets.Load<Texture2D>("Assets/Sprites/enemy.png");
        enemy.GetComponent<Health>()!.Died += OnEnemyDied;
    }
}
```

Remember what does not survive serialisation: textures, clips, shaders, event
subscriptions, and actor subclasses. The
[`SpriteLoader` pattern](10-scenes-and-prefabs.md#the-asset-path-pattern) covers
assets; a loop like the one above covers the rest.

---

## Checkpoint

You know:

- What the editor does today — cross-platform, ten panels, a project launcher
- The Create menu, and why it beats assembling 3D actors by hand
- The one remaining scene-loss bug, and the one-line fix
- `AdoptScene`, which makes the editor→code hybrid workflow actually work
- A code-first loop with script, asset and tuning hot reload
- An in-game inspector that works everywhere

---

**Next:** [Tutorial 18 — Shipping Your Game](18-shipping.md)
