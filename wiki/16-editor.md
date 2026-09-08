# 16. The Editor

Project: `SexyBiscuit.Editor` · **cross-platform** (`net8.0`, MonoGame +
ImGui.NET)

```bash
dotnet run --project SexyBiscuit.Editor
```

An ImGui.NET shell hosting a live `SBEngine` instance that renders into a
`RenderTarget2D`, shown in a dockable Viewport panel.

The engine is brought up as an
[`EngineHost`](03-game-loop.md#hosting-the-engine-inside-another-app) rather than
a `Game` of its own, because the editor already owns the device and the message
loop. `EngineHost.Tick` advances it in the standalone loop's order, and only in
play mode — the editor decides when the game sees the keyboard, which is how
WASD stays out of the game while you type in a panel.

The editor is also an MCP server and hosts Claude Code: see
[25. AI Assistant & MCP](25-ai-assistant-mcp.md).

> The editor used to target `net8.0-windows` and depend on WinForms for file
> dialogs and the clipboard. Both now go through the editor's own
> [`FileDialog`](#filedialog) and [`DesktopShell`](#desktopshell), so it runs on
> Windows, macOS and Linux.

---

## Startup

`ProjectManagerPanel` opens first — `EditorState.ShowProjectManager` defaults to
`true` — and while it is up nothing else is drawn, because no project is loaded
yet.

```csharp
if (EditorState.ShowProjectManager)
{
    _projectManager.Draw();
    FileDialog.Draw();
    return;                 // the workspace behind it is meaningless
}
```

Three tabs: **Recent Projects**, **New Project**, **Open Project**.

`EditorState.OpenProject(path)` loads a `.sbproject`, sets `ProjectPath` to its
directory, records it in the recent list, closes the launcher, and raises
`OnProjectOpened`.

### Recent projects

```csharp
EditorState.RecentProjects;          // List<RecentProject>, most recent first
EditorState.LoadRecentProjects();    // called once from EditorApp.Initialize
EditorState.SaveRecentProjects();
```

The list lives in the platform's per-user application data directory
(`%APPDATA%/SexyBiscuit/recent-projects.json`, or the equivalent) rather than
next to the executable, so it survives a rebuild and works from a read-only
install. Entries whose project file has since moved or been deleted are dropped
on load, so the launcher never offers a dead link. Re-opening a project moves it
to the top instead of duplicating it; twelve are kept.

---

## Layout

The whole window is one ImGui dockspace
(`ImGuiDockNodeFlags.PassthruCentralNode`), so every panel is dockable, tabbable
and resizable. Drag a panel by its tab to rearrange; the layout persists in
`imgui.ini` next to the executable.

### Menu bar

| Menu | Items |
|---|---|
| **File** | New Scene · Open Scene… · Save Scene (Ctrl+S) · Save Scene As… · Exit |
| **Edit** | Undo (Ctrl+Z) · Redo (Ctrl+Y) — currently the MCP/assistant scene history; interactive panel edits are not yet all routed through one transaction history |
| **Play** | Play (F5) · Pause (F6) · Stop (F7 / Ctrl+S) · Fullscreen Viewport (Ctrl+P) |
| **View** | 3D Viewport · Render Stats · API Reference · Code Editor · Git · C# Project · Assistant (F8) · Project Manager · Reset Layout |
| **Tools** | Assistant Settings… · Focus Assistant (F8) · Stop Assistant (Shift+F8) · New / Resume / Stop Assistant Session · Write .mcp.json · Copy MCP Connect Command · Rebuild Engine & Restart |
| **Create** | actor presets — see [below](#the-create-menu) |

### Hotkeys

| Key | Action |
|---|---|
| <kbd>F5</kbd> / <kbd>F6</kbd> / <kbd>F7</kbd> | Play / Pause / Stop |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> (<kbd>Cmd</kbd> on macOS) | Save scene; while playing, Stop |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> (<kbd>Cmd</kbd> on macOS) | The game over the whole window, while playing (again to leave) |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | Undo / Redo a scene edit (not while a text field has focus) |
| <kbd>F8</kbd> / <kbd>Shift</kbd>+<kbd>F8</kbd> | Focus the Assistant composer / stop Claude |
| <kbd>W</kbd> / <kbd>E</kbd> / <kbd>R</kbd> | Move / Rotate / Scale gizmo (while hovering the viewport and no text field has focus) |

---

## Panels

Thirteen panels are wired into `EditorApp.DrawPanels`, plus the Render Stats
overlay. The Assistant and C# Project panels have a page of their own,
[25. AI Assistant & MCP](25-ai-assistant-mcp.md), and so does the Cookie Jar,
[28. The CookieJar](28-the-cookiejar.md); the short versions are below.

### Hierarchy

The scene tree grouped by layer. `+ Actor` and `+ Layer` buttons; right-click a
layer for Add Actor / Delete Layer, an actor for Rename / Duplicate / Delete.
Selection is published through `EditorState.SelectActor` and every other panel
listens.

### Inspector

Edits the selected actor: name, tag, active, and a Transform section with drag
fields. **Rotation is shown in degrees** and converted to radians on write,
while `Transform.LocalRotation` stores radians.

Each component gets a collapsible header with an Enabled checkbox, and its
properties are enumerated by reflection:

| Property type | Widget |
|---|---|
| `float`, `int` | drag field |
| `bool` | checkbox |
| `string` | text field |
| `Vector2` | two labelled drag fields (X Y) |
| `Vector3` | three labelled drag fields (X Y Z) |
| `Vector4` | a four-component drag field |
| `Quaternion` | three drag fields as **Euler degrees** (Pitch, Yaw, Roll) |
| `Color` | RGBA picker |
| `enum` | combo box |

Two details worth knowing:

- **Quaternions are edited as Euler degrees**, because nobody can reason about
  raw xyzw. The round-trip is stable as long as the widget only writes back the
  axis you actually dragged, which is what it does.
- **Vector fields only write when a drag changes something.** Writing every
  frame would mark the transform dirty continuously and defeat the engine's lazy
  world-transform cache for every selected actor.

Types outside that set are listed but not editable — the same constraint as
[scene serialisation](12-scenes-prefabs.md#which-properties-round-trip). An
**Add Component** search box lists every non-abstract `Component` subclass found
by reflection.

The native inspector does not yet provide collection, map, nested-object, typed
asset-picker or per-component specialist widgets. These are tracked as editor
parity work rather than being represented by inert controls; see
[31. Editor Workbench Status](31-editor-workbench.md).

### Viewport

The engine rendering into a `RenderTarget2D`, resized with the panel.

Navigation, while the pointer is over the viewport: **right-drag pans**,
**scroll zooms** toward the cursor. Gizmo mode is switched with
<kbd>W</kbd> / <kbd>E</kbd> / <kbd>R</kbd> or the matching buttons in the
toolbar and held in `EditorState.GizmoMode`.

In the 3D viewport, one selected `Transform3D` can be translated along a local
axis or in the camera plane, rotated about a local axis or the screen-facing
axis, and scaled per-axis or uniformly from the centre handle. Translation,
rotation and scale snapping are configurable in the toolbar. 2D has its own
single-actor handles. There is **not yet** multi-selection, box selection,
world/local switching, pivot modes, plane handles, surface/vertex snapping or
transactional undo for all interactive edits; the workbench status page is the
authoritative feature record.

Toggle **View → 3D Viewport** (`EditorState.Viewport3D`) to render through the
3D pipeline instead of the 2D sprite pass. The renderer prefers the *scene's*
`Camera3D.Main` so the viewport shows what the game will, and falls back to the
editor's own camera when the scene has none:

```csharp
_engine.Renderer3D.OverrideCamera = Camera3D.Main ?? _editorCamera3D;
_engine.Renderer3D.Render(scene);
```

That editor camera is deliberately **not** in the scene: it must not be saved
with the level, must not appear in the hierarchy, and must survive the scene
being replaced.

### Asset Browser

Browses `EditorState.ProjectPath`, with thumbnails for images. Selecting a file
sets `EditorState.SelectedAssetPath`. Right-click for **Reveal in File Manager**
and **Copy Path**, both through `DesktopShell`.

### Console

`EditorTraceListener` is registered on `System.Diagnostics.Trace.Listeners` at
startup, so every `Debug.WriteLine` from engine and game code appears here —
including `[Script]` output from `Debug.log` in JavaScript — tagged by
`LogLevel`.

### Build Settings

A form over [`PlatformConfig`](18-build-export.md), saved to and loaded from
JSON. `SexyBiscuit.Editor/BuildConfig.json` is a working example.

### API Reference · Code Editor · Git

Toggled from the View menu:

| Panel | What it does | Flag |
|---|---|---|
| `ApiReferencePanel` | searchable, reflected engine API browser | `EditorState.ShowApiReference` |
| `CodeEditorPanel` | in-editor text editor for `.js` and `.cs` | `EditorState.ShowCodeEditor` |
| `GitPanel` | status, stage, commit, push/pull via `GitHelper` | `EditorState.ShowGitPanel` |

### Render Stats

A small overlay of what `RenderSystem3D` submitted last frame — FPS, renderers
drawn/culled/total, draw calls, triangles, active lights, shadow casters. Toggle
with **View → Render Stats**.

### Place Actors

The palette on the left: every `ActorPresets` entry grouped by category (Basic,
Geometry, Lights, Cameras, Gameplay, UI, 2D) plus a **Project** group for actor
classes from the project's own C# assembly. Click to place on the selected layer.
The same presets back the Create menu and `spawn_actor`'s `preset` argument.

### C# Project

The project's code: the csproj and source count, the loaded assembly generation
and whether it matches the engine build the editor runs, **Build**, **Build &
Reload**, **Run Standalone**, **Auto-reload on save**, the live build log, and
diagnostics that open the Code Editor at the line. **Add C# Project** generates
one for a project that has none. Toggle with **View → C# Project**
(`EditorState.ShowCodeProject`).

### Assistant

Claude Code inside the editor: a chat transcript with a row per tool call,
questions and permission prompts, an Activity tab over every MCP call, and a
Diagnostics tab. <kbd>F8</kbd> focuses it, <kbd>Shift</kbd>+<kbd>F8</kbd> stops
Claude, `EditorState.ShowAssistant` toggles it. The whole story is
[25. AI Assistant & MCP](25-ai-assistant-mcp.md).

### Cookie Jar

The module library. **Browse** searches every jar and installs a cookie into the
open project; **Installed** lists what this project has, and flags any file that
has been edited since; **Jars** adds a folder or a repository, and is the only
place a cloned jar can be trusted; **Bake** turns files from this project into a
new cookie. Off by default (`EditorState.ShowCookieJar`), reachable from View
and from Tools. The whole story is [28. The CookieJar](28-the-cookiejar.md).

---

## The Create menu

Presets for the actors a scene almost always needs. Building a 3D scene by hand
means adding a bare `Actor`, then a `Transform3D`, then a `Camera3D`, then
remembering the `"MainCamera3D"` tag — four steps to get anything on screen.
Each preset is one click, and the result is selected so the Inspector opens on
it.

| Submenu | Presets |
|---|---|
| *(top level)* | Empty Actor · Empty Actor (3D) |
| **3D Object** | Mesh · Skinned Mesh · Particle System · Skybox |
| **Light** | Directional · Point · Spot · 2D Light |
| **Camera** | Camera 3D · Camera 3D + Fly Controls · Camera 2D |
| **Gameplay** | Game Mode · Character · Player Start · AI Character |
| **UI** | Canvas · World Canvas |
| **2D Object** | Sprite · Tilemap · Particle Emitter |

The presets encode the setup the rest of this wiki tells you to remember:
**Camera 3D** carries the `"MainCamera3D"` tag, **Directional** light gets a
sensible `EulerAngles`, **AI Character** gets a `NavMeshAgent` with
`DriveCharacter = true`, **Skinned Mesh** pairs `SkeletalAnimator` with
`SkinnedMeshRenderer`.

New actors land on `EditorState.SelectedLayer` when one is selected, and on
`"default"` otherwise.

---

## FileDialog

A modal file browser drawn with ImGui, replacing the WinForms common dialogs.

```csharp
FileDialog.OpenFile("Open Scene", EditorState.ProjectPath, new[] { ".scene", ".json" },
    path => LoadScene(path));

FileDialog.SaveFile("Save Scene", EditorState.ProjectPath, new[] { ".scene" },
    defaultName: scene.Name + ".scene", path => Save(path));

FileDialog.PickFolder("Select project location", startDir, chosen => _dir = chosen);

bool busy = FileDialog.IsOpen;
```

```csharp
FileDialog.Draw();      // once per frame, after everything else
```

The result arrives through a **callback rather than a return value**, because
ImGui is immediate-mode: the call that opens the dialog has long since returned
by the time the user picks a file. One dialog is open at a time, which matches
how the editor uses them — a dialog is always a response to a menu item or a
button, and those are unreachable while a modal is up.

`SaveFile` warns before overwriting. Extensions include the dot; an empty array
shows everything.

## DesktopShell

Cross-platform shims for the desktop integrations that used to come from
WinForms.

```csharp
DesktopShell.SetClipboardText(path);      // via ImGui's clipboard backend
string text = DesktopShell.GetClipboardText();
DesktopShell.RevealInFileManager(path);   // Explorer / Finder / the Linux file manager
```

---

## Play mode

```
F5            EnterPlayMode   → snapshot = SceneSerializer.Serialize(activeScene), game camera on
F6            TogglePause
F7 / Ctrl+S   ExitPlayMode    → restore from the snapshot, camera choice restored
Ctrl+P        fullscreen viewport on/off (Cmd on macOS)
```

While playing, the editor calls `EngineHost.Tick(dt, pumpInput: true)`: the
full standalone order — time, game instance, fixed steps with physics, `Update`,
tweens, timers, coroutines, `LateUpdate`, audio — with input reaching the game
only in play mode. A component that throws is disabled after three consecutive
exceptions (the Output Log names it); anything that escapes ends play mode
instead of the editor. While paused, `play_mode` with `action: "step"` advances fixed frames
one at a time.

The 2D viewport draws with a plain `SpriteBatch.Begin()` / `End()` and **no
camera transform**, so `Camera2D` has no effect there. The 3D viewport renders
through the scene's `MainCamera3D` when **Game Cam** is on, otherwise through the
editor camera. Play switches **Game Cam** on, so the possessed player's camera is
what plays, and Stop puts your choice back. <kbd>Ctrl</kbd>+<kbd>P</kbd>
(<kbd>Cmd</kbd> on macOS) shows the game over the whole work area in a separate
window; the docked panels keep their layout and return when play stops.

Play loads a fresh copy of the snapshot (`PlayMode.IsActive` is switched on first),
so every actor's Start runs at play start the way it does when a standalone game
loads the level: a `GameMode` spawns its players then, and never in the edit-time
scene. Stop switches `PlayMode.IsActive` off, restores the snapshot with
`SceneManager.AdoptScene`, resets `Time.TimeScale`, and clears timers and
coroutines. Tool calls made during play mode warn that they are discarded on Stop,
and skip the undo snapshot.

---

## Scene files

**Open Scene…**, **Save Scene** and **Save Scene As…** go through
`SceneSerializer`, so they use the
[canonical format](12-scenes-prefabs.md#the-scene-file-format): `position` /
`rotation` / `scale` arrays, an actor `class` for subclasses, and short component
type names — the same shape the bundled templates use. **Save Scene** writes to
the scene's known path (`EditorState.CurrentScenePath`) and clears the dirty
marker in the title (`EditorState.SceneDirty`); the Assistant's `save_scene`
tool goes through the same path.

Opening a scene works correctly: `EditorApp.OpenSceneDialog` deserialises the
file and installs it with `SceneManager.AdoptScene`.

---

## Project templates

`Templates/` holds 15 starter projects. Each has:

```
Templates/<Name>/
├── template.json        { "name", "description", "category" }
├── ProjectSettings.json { WindowTitle, WindowWidth, …, StartScene }
├── Scenes/*.scene
└── Scripts/*.js
```

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

`ProjectFile.CreateNew(name, directory, templatePath)` builds the folder
skeleton (`Assets/Sprites`, `Assets/Audio`, `Scripts`, `Scenes`), copies the
template without overwriting, reads `StartScene` from `ProjectSettings.json`,
and writes `<name>.sbproject`:

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

`ProjectManagerPanel.ScanTemplates` walks up from the working directory looking
for a `Templates/` folder, so it works from a build output directory as well as
from the repo root.

> Template scenes and scripts are **design references, not runnable content**.
> Their `.scene` shape and their JavaScript both target a wider API than the
> engine implements — see
> [12. Scenes](12-scenes-prefabs.md#the-bundled-scene-templates-use-a-different-shape)
> and [11. Scripting](11-scripting.md#what-the-bundled-templates-assume--and-what-breaks).

---

## EditorState

The data bus every panel reads and writes. No panel logic lives here.

```csharp
// Selection
EditorState.SelectedActor;            // Actor?, set via SelectActor
EditorState.SelectedLayer;            // Layer?
EditorState.SelectedAssetPath;        // string?
EditorState.OnSelectionChanged;       // event Action<Actor?>
EditorState.SelectActor(actor);

// Play mode
EditorState.IsPlaying;  EditorState.IsPlayPaused;

// Viewport
EditorState.GizmoMode;                // Translate | Rotate | Scale
EditorState.Viewport3D;
EditorState.ViewportFocused;

// Panel visibility
EditorState.ShowApiReference;  EditorState.ShowCodeEditor;
EditorState.ShowGitPanel;      EditorState.ShowProjectManager;   // true on startup
EditorState.ShowRenderStats;   EditorState.ShowCodeProject;      EditorState.ShowAssistant;

// Assistant
EditorState.AssistantBusy;  EditorState.AssistantBusyLabel;      // drive the viewport banner

// Project
EditorState.ProjectPath;              // root directory
EditorState.CurrentProject;           // ProjectFile?
EditorState.CurrentProjectFile;       // absolute .sbproject path
EditorState.CurrentScenePath;  EditorState.SceneDirty;           // the open scene file and its dirty flag
EditorState.OnProjectOpened;          // event Action<string>
EditorState.RecentProjects;           // List<RecentProject>
EditorState.OpenProject(sbprojectPath);
EditorState.LoadRecentProjects();  EditorState.SaveRecentProjects();
```

---

## Working without the editor

The editor is optional, and code-first development remains a perfectly good
path — it is what `SexyBiscuit.Demo` does and what the
[tutorials](../tutorials/README.md) teach:

- Build scenes in static `Load(SceneManager)` methods.
- Iterate gameplay with [JavaScript hot reload](11-scripting.md#hot-reload).
- Iterate art with [asset hot reload](13-assets.md#hot-reload).
- Use [`Gizmos` and `DebugOverlay`](17-debugging.md) for in-game inspection.

The two approaches now compose well: lay a scene out in the editor, save it, and
load it in code with `SceneSerializer.LoadFromFile` +
`SceneManager.AdoptScene`. See
[12. Scenes](12-scenes-prefabs.md#loading-a-scene-file).

---

## Next

- [25. AI Assistant & MCP](25-ai-assistant-mcp.md)
- [17. Debugging & Profiling](17-debugging.md)
- [Tutorial 17: Editor Workflow](../tutorials/17-editor-workflow.md)
- [Tutorial 20: Building a Game with Claude](../tutorials/20-building-a-game-with-claude.md)
