# Tutorial 1 — Hello, Window

**You will build:** a project that opens a window, runs the engine, and draws a
coloured square you can see. **Time:** ~20 minutes.

By the end you will have the bootstrap that every other tutorial builds on.

---

## 1. Create the project

From the repository root:

```bash
dotnet new console -o MyGame
cd MyGame
dotnet add reference ../SexyBiscuit.Engine/SexyBiscuit.Engine.csproj
cd ..
```

## 2. Set up content copying

Assets are loaded by **path, relative to the working directory**, so content has
to sit next to the binary. Open `MyGame/MyGame.csproj` and make it look like
this:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RootNamespace>MyGame</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SexyBiscuit.Engine\SexyBiscuit.Engine.csproj" />
  </ItemGroup>

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

</Project>
```

Create the folders:

```bash
mkdir -p MyGame/Assets/Sprites MyGame/Assets/Audio MyGame/Scripts MyGame/Scenes
```

## 3. The naive version — and why it shows nothing

The engine has a one-line entry point:

```csharp
SBEngine.Run(new EngineConfig { WindowTitle = "My Game" });
```

That opens a window and runs the scene graph. But `SBEngine.Draw` calls
`SceneManager.Draw(SpriteBatch)` **without opening a `SpriteBatch`**, so the
moment you add a sprite you get:

```
InvalidOperationException: Begin must be called successfully before you can call Draw.
```

So we subclass `SBEngine` from the start. This is not a workaround you will
outgrow — it is where your game's frame lives.

## 4. The bootstrap

Create `MyGame/Game.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Debug;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;

namespace MyGame;

public class Game : SBEngine
{
    /// <summary>The camera the world is rendered through. Assigned when the scene is built.</summary>
    protected Camera2D? Camera;

    public Game() : base(new EngineConfig
    {
        WindowTitle     = "My Game",
        WindowWidth     = 1280,
        WindowHeight    = 720,
        VSync           = true,
        ShowCursor      = true,
        ClearColour     = new Color(18, 18, 28),
        Enable3D        = false,       // pure 2D: skip the 3D render pass
        EnablePhysics3D = false,       // …and the Bepu simulation
        EnablePhysics2D = true,        // Aether: stepped for us each fixed update
    })
    { }

    // -----------------------------------------------------------------------
    // Boot — the earliest point at which Assets, Input, Audio and SceneManager exist.
    // -----------------------------------------------------------------------
    protected override void OnEngineReady()
    {
        // Renderer2D and Renderer3D are already constructed and initialised for us.
        BuildScene();
    }

    protected virtual void BuildScene()
    {
        var scene = SceneManager.CreateScene("Main");

        var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
        Camera = cameraActor.AddComponent<Camera2D>();
        scene.AddActor(cameraActor);
    }

    // -----------------------------------------------------------------------
    // Update — base.Update pumps time, input, physics, the scene graph, tweens,
    // timers, coroutines and audio. Everything after it is ours to drive.
    // -----------------------------------------------------------------------
    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        float dt = Time.DeltaTime;

#if DEBUG || DEVELOPMENT
        Gizmos.Update(dt);
        DebugOverlay.Update(dt);
#endif
    }

    // -----------------------------------------------------------------------
    // Draw — we open the batches ourselves.
    // -----------------------------------------------------------------------
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        // World pass, through the camera. The "ui" layer is drawn separately
        // so it does not scroll with the world (Tutorial 7).
        var ui = scene.GetLayer("ui");
        if (ui != null) ui.Visible = false;
        Renderer2D.RenderScene(SpriteBatch, scene, Camera);
        if (ui != null) ui.Visible = true;

        // Screen-space pass: UI and debug overlays.
        SpriteBatch.Begin(samplerState: Microsoft.Xna.Framework.Graphics.SamplerState.LinearClamp);
        ui?.Draw(SpriteBatch);
#if DEBUG || DEVELOPMENT
        Gizmos.Flush(SpriteBatch, GraphicsDevice);
        DebugOverlay.Draw(SpriteBatch);
#endif
        SpriteBatch.End();

        // NOTE: deliberately no base.Draw(gameTime). It would issue a second,
        // unbatched scene draw.
    }
}
```

Replace `MyGame/Program.cs`:

```csharp
namespace MyGame;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var game = new Game();
        game.Run();
    }
}
```

```bash
dotnet run --project MyGame
```

A dark window. Press <kbd>F1</kbd> — the debug overlay appears with FPS and
frame time. That confirms the loop is running.

## 5. Draw something

`SpriteRenderer` needs a `Texture2D`. Rather than hunting for art, generate a
white pixel and tint it — a technique worth keeping for prototyping.

Add to `Game.cs`:

```csharp
using Microsoft.Xna.Framework.Graphics;

// … inside class Game …

private Texture2D? _white;

/// <summary>A 1×1 white texture. Tint and scale it to draw solid rectangles.</summary>
protected Texture2D White => _white ??= CreateWhitePixel();

private Texture2D CreateWhitePixel()
{
    var tex = new Texture2D(GraphicsDevice, 1, 1);
    tex.SetData(new[] { Color.White });
    return tex;
}

/// <summary>Creates a solid-colour rectangle actor, sized in pixels.</summary>
protected Actor CreateBox(string name, Vector2 position, Vector2 size, Color colour)
{
    var actor = new Actor(name);
    actor.Transform.Position   = position;
    actor.Transform.LocalScale = size;          // a 1×1 texture scaled to `size` pixels

    var sprite = actor.AddComponent<SpriteRenderer>();
    sprite.Texture = White;
    sprite.Tint    = colour;
    sprite.Pivot   = new Vector2(0.5f, 0.5f);
    return actor;
}
```

Now fill in `BuildScene`:

```csharp
protected virtual void BuildScene()
{
    var scene = SceneManager.CreateScene("Main");

    var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
    cameraActor.Transform.Position = new Vector2(640, 360);   // centre of the window
    Camera = cameraActor.AddComponent<Camera2D>();
    scene.AddActor(cameraActor);

    var player = CreateBox("Player", new Vector2(640, 360), new Vector2(48, 48),
                           new Color(90, 200, 255));
    player.Tag = "Player";
    scene.AddActor(player);

    var ground = CreateBox("Ground", new Vector2(640, 640), new Vector2(900, 32),
                           new Color(70, 80, 100));
    scene.AddActor(ground, "background");
}
```

```bash
dotnet run --project MyGame
```

A blue square above a grey bar. **That is a rendered scene.**

## 6. What just happened

```
Program.Main
└── new Game()                      window created, EngineConfig applied
    └── Run()
        ├── Initialize()            Assets, Input, Audio, SceneManager, Timers,
        │                           Coroutines, Renderer2D, Renderer3D, GameInstance
        │   └── OnEngineReady()     ← BuildScene()
        └── loop:
            ├── Update(gameTime)
            │   ├── base.Update     Time, Input, (Physics + FixedUpdate)×n,
            │   │                   Update, Tweens, Timers, Coroutines,
            │   │                   LateUpdate, Audio
            │   └── ours            gizmos, overlay
            └── Draw(gameTime)
                ├── Clear
                ├── Renderer2D.RenderScene   world, through the camera
                └── SpriteBatch.Begin/End    UI + overlays, screen space
```

Three things worth internalising now:

**The camera position is the screen centre.** `Camera2D.GetViewMatrix` maps the
camera's world position to the middle of the viewport. Camera at `(640, 360)`
with a 1280×720 window means world `(0,0)` is the top-left corner.

**`LocalScale` scales the texture.** A 1×1 texture at scale `(48, 48)` draws a
48-pixel square. With real art you leave scale at `(1,1)` and let the texture
size speak.

**Y increases downward.** Screen convention, matching MonoGame. The ground at
`y = 640` is below the player at `y = 360`.

## 7. Loading real art

Drop a PNG into `MyGame/Assets/Sprites/player.png` and:

```csharp
var sprite = actor.AddComponent<SpriteRenderer>();
sprite.Texture = Assets.Load<Texture2D>("Assets/Sprites/player.png");
sprite.Pivot   = new Vector2(0.5f, 0.5f);
// leave Transform.LocalScale at (1,1)
```

`AssetManager` reference-counts and caches by absolute path, so calling `Load`
for the same file twice returns the same instance. In `Debug` and `Development`
builds it also watches the file — edit the PNG while the game runs and the
sprite updates.

Supported types are `Texture2D`, `SoundEffect`, `string` and `byte[]`. Fonts and
shaders go through the MonoGame content pipeline instead — see
[Tutorial 7](07-ui-and-menus.md).

## 8. Configuration worth knowing

```csharp
new EngineConfig
{
    WindowTitle   = "My Game",
    WindowWidth   = 1280,
    WindowHeight  = 720,
    Fullscreen    = false,
    VSync         = true,
    ShowCursor    = true,
    AllowResize   = true,
    FixedTimestep = 1f / 60f,      // one physics + FixedUpdate step
    ClearColour   = Color.Black,
    HotReload     = true,          // read by you, not by the engine
    Enable3D      = false,         // skip the 3D render pass in a 2D game
    EnablePhysics2D = true,        // step the Aether world each fixed update
    EnablePhysics3D = false,       // …and skip Bepu entirely
    MaxFixedStepsPerFrame = 5,     // catch-up cap after a hitch
    GameInstanceFactory = null,    // Tutorial 14
}
```

`StartScene` exists on `EngineConfig` but nothing in the engine reads it — load
your first scene in `OnEngineReady`, as we do.

---

## Checkpoint

You have:

- A project referencing the engine, with content copying set up
- A `Game` class that owns the frame and both render passes
- A helper for solid-colour prototype actors
- A scene with a camera and two visible objects
- <kbd>F1</kbd> for the debug overlay

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Begin must be called` | `base.Draw(gameTime)` left in `Draw`, or `RenderScene` not called |
| Black window, no error | camera is null, or actors are off-screen — check the camera position |
| `FileNotFoundException` on a texture | content not copied; check the `<Content>` block and the path casing |
| Nothing updates | actor never added to the scene, or `IsActive` is false |
| Nothing falls | `EnablePhysics2D` is false, or the actor has no `Rigidbody2D` |

---

**Next:** [Tutorial 2 — Sprites & Cameras](02-sprites-and-cameras.md)
