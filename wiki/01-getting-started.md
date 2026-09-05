# 1. Getting Started

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET 8 SDK | All three projects target `net8.0`. |
| A desktop GPU with OpenGL 3.0+ | The engine uses `MonoGame.Framework.DesktopGL`. |
| — | Nothing platform-specific. The engine, the editor and your game all target plain `net8.0` and run on Windows, macOS and Linux. |

Check your SDK:

```bash
dotnet --list-sdks
```

## Solution layout

```
SexyBiscuit.sln
├── SexyBiscuit.Engine/     the engine library (net8.0)
│   ├── Core/               Actor, Component, Scene, Layer, Transform, Transform3D
│   ├── Rendering/          SpriteRenderer, Camera2D/3D, MeshRenderer, particles, tilemaps
│   ├── Physics/            Aether 2D + Bepu 3D wrappers
│   ├── Input/              InputManager, ActionMap, gamepad, touch
│   ├── Audio/              AudioManager, buses, AudioSource, effects
│   ├── UI/                 Canvas, Widget, Widgets/, Layout/, Theme
│   ├── Animation/          SpriteAnimator, AnimatorController, Tween, skeletal
│   ├── Scripting/          Jint runtime, ScriptBridge, ScriptComponent, hot reload
│   ├── Scene/              SceneSerializer, Prefab, WorldStreamer
│   ├── Assets/             AssetManager, AssetBundle
│   ├── Save/               SaveManager, PlayerPrefs
│   ├── Networking/         NetworkManager, replication, RPC, LAN discovery
│   ├── Steam/              SteamManager, achievements, cloud, lobby, workshop
│   ├── Debug/              DebugOverlay, Gizmos, Profiler, MemoryViewer
│   └── Build/              PlatformConfig, AssetCooker, ExportPipeline
├── SexyBiscuit.Editor/     ImGui editor shell (net8.0)
├── SexyBiscuit.Demo/       "Biscuit Chronicles" sample game (net8.0)
├── SexyBiscuit.Tests/      unit + integration tests (net8.0)
└── Templates/              15 starter project templates
```

## Build

```bash
dotnet build SexyBiscuit.sln
```

Three configurations are defined in every project — `Debug`, `Development`,
`Release` — each with its own compilation symbols:

| Configuration | Symbols | Optimise | Debug info |
|---|---|---|---|
| `Debug` | `DEBUG;SEXY_DEBUG;STEAMWORKS` | off | full |
| `Development` | `DEVELOPMENT;SEXY_DEV;STEAMWORKS` | on | pdb-only |
| `Release` | `RELEASE;STEAMWORKS` | on | none |

These symbols matter:

- `DEBUG || DEVELOPMENT` enables **asset hot reload** (`AssetManager` file watchers)
  and the **script hot reload** implementation. In `Release`, `ScriptHotReload`
  compiles to a set of no-op stubs.
- `STEAMWORKS` enables the real Steamworks.NET calls in `SexyBiscuit.Engine/Steam/`.
  Without it those classes compile to stubs that return empty values. Note it is
  currently defined in **all three** configurations, so a Steam client or a
  `steam_appid.txt` is expected at runtime if you call `SteamManager.Init`.

```bash
dotnet build SexyBiscuit.sln -c Development
```

## Run the demo

```bash
dotnet run --project SexyBiscuit.Demo
```

`SexyBiscuit.Demo/Program.cs` is the canonical minimal entry point:

```csharp
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Steam;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
#if STEAMWORKS
        SteamManager.Init(480);          // 480 = Steam's "Spacewar" test app id
#endif
        var config = new EngineConfig
        {
            WindowTitle  = "Biscuit Chronicles",
            WindowWidth  = 1920,
            WindowHeight = 1080,
            StartScene   = "Scenes/MainMenu",
            ShowCursor   = true,
            VSync        = true,
        };

        SBEngine.Run(config);

#if STEAMWORKS
        SteamManager.Shutdown();
#endif
    }
}
```

> If Steam is not running, `SteamManager.Init` logs a failure and sets
> `IsInitialised = false` rather than throwing — the game still runs.

## Run the editor

```bash
dotnet run --project SexyBiscuit.Editor
```

It opens on the project launcher — pick a recent project, create one from a
template, or open an existing `.sbproject`.

Hotkeys: <kbd>F5</kbd> play · <kbd>F6</kbd> pause · <kbd>F7</kbd> stop ·
<kbd>G</kbd>/<kbd>R</kbd>/<kbd>S</kbd> gizmo mode.
See [16. The Editor](16-editor.md).

## Create your own game project

The engine is a plain class library — reference it from any .NET 8 executable.

```bash
dotnet new console -o MyGame
cd MyGame
dotnet add reference ../SexyBiscuit.Engine/SexyBiscuit.Engine.csproj
```

Then mirror the demo's `.csproj` so your content is copied next to the binary.
This matters because [asset paths are resolved relative to the working
directory](13-assets.md#paths):

```xml
<ItemGroup>
  <Content Include="Assets\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="Scripts\**\*.js">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="Scenes\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

A conventional project on disk:

```
MyGame/
├── MyGame.csproj
├── Program.cs
├── Assets/            textures, audio  (Content.RootDirectory is "Assets")
│   └── ProjectSettings.json
├── Scripts/           .js gameplay scripts
├── Scenes/            .scene / .json scene files
└── Saves/             created at runtime by SaveManager / PlayerPrefs
```

## The smallest game that draws something

`SBEngine.Run(config)` opens a window and pumps the scene graph, but its
`Draw` does **not** open a `SpriteBatch`, so nothing renders. The minimum useful
program subclasses `SBEngine`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

public sealed class MyGame : SBEngine
{
    private Camera2D? _camera;

    public MyGame() : base(new EngineConfig
    {
        WindowTitle  = "My Game",
        WindowWidth  = 1280,
        WindowHeight = 720,
        Enable3D     = false,          // pure 2D — skip the 3D pass
    }) { }

    protected override void OnEngineReady()
    {
        // Renderer2D is already constructed and initialised by SBEngine.
        var scene = SceneManager.CreateScene("Main");

        var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
        _camera = cameraActor.AddComponent<Camera2D>();
        scene.AddActor(cameraActor);

        var player = new Actor("Player") { Tag = "Player" };
        player.Transform.Position = new Vector2(640, 360);
        var sprite = player.AddComponent<SpriteRenderer>();
        sprite.Texture = Assets.Load<Texture2D>("Assets/player.png");
        scene.AddActor(player);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        var scene = SceneManager.ActiveScene;
        if (scene != null)
            Renderer2D.RenderScene(SpriteBatch, scene, _camera);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main() { using var game = new MyGame(); game.Run(); }
}
```

Note `Draw` does **not** call `base.Draw(gameTime)` here — the base
implementation would issue a second, unbatched scene draw. See
[3. The Game Loop](03-game-loop.md) for the complete bootstrap, including
physics and tweens.

## Next

- [2. Core Architecture](02-core-architecture.md) — the object model.
- [Tutorial 1: Hello, Window](../tutorials/01-hello-window.md) — the same ground, step by step.
