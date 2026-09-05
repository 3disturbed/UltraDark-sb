# Tutorial 2 — Sprites & Cameras

**You will build:** a scrolling world with layered art, a camera that follows a
target with a deadzone, and screen shake. **Time:** ~25 minutes.

Builds on [Tutorial 1](01-hello-window.md).

---

## 1. Layers

Every `Scene` starts with four layers, drawn in `Order` ascending — lower draws
first, i.e. behind:

| Layer | Order |
|---|---|
| `background` | −100 |
| `default` | 0 |
| `foreground` | 100 |
| `ui` | 200 |

Add your own where you need them:

```csharp
scene.AddLayer("parallax",    -200);
scene.AddLayer("projectiles",   50);
```

Layers are also the unit of pausing and hiding:

```csharp
layer.Active  = false;   // stops Update / FixedUpdate / LateUpdate
layer.Visible = false;   // stops Draw
```

### The Y-sort you need to know about

`Layer.Draw` sorts its actors by `Transform.LocalPosition.Y` ascending every
frame. That is exactly right for a top-down game — things lower on screen
overlap things higher up — and exactly wrong for a side-scroller, where a
background hill at `y = 100` would draw in front of a player at `y = 400`.

**The rule:** put content that must not Y-sort against each other on separate
layers. Within a layer, use `SpriteRenderer.LayerDepth` (0 = front, 1 = back)
for fine control.

## 2. A layered world

Replace `BuildScene` in `Game.cs`:

```csharp
protected virtual void BuildScene()
{
    var scene = SceneManager.CreateScene("Main");
    scene.AddLayer("parallax", -200);

    // --- Camera ---------------------------------------------------------
    var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
    cameraActor.Transform.Position = new Vector2(640, 360);
    Camera = cameraActor.AddComponent<Camera2D>();
    scene.AddActor(cameraActor);

    // --- Far parallax: a band of dim rectangles --------------------------
    var rng = new Random(1234);
    for (int i = 0; i < 40; i++)
    {
        var star = CreateBox($"Star{i}",
            new Vector2(rng.Next(-500, 3000), rng.Next(0, 720)),
            new Vector2(3, 3),
            new Color(120, 130, 180) * 0.6f);
        scene.AddActor(star, "parallax");
    }

    // --- Ground ----------------------------------------------------------
    for (int i = 0; i < 12; i++)
    {
        var tile = CreateBox($"Ground{i}",
            new Vector2(i * 256, 640),
            new Vector2(256, 64),
            i % 2 == 0 ? new Color(58, 70, 88) : new Color(52, 64, 80));
        scene.AddActor(tile, "background");
    }

    // --- Pillars, to make movement legible -------------------------------
    for (int i = 0; i < 6; i++)
    {
        var pillar = CreateBox($"Pillar{i}",
            new Vector2(200 + i * 420, 520),
            new Vector2(48, 180),
            new Color(80, 96, 120));
        scene.AddActor(pillar, "default");
    }

    // --- Player -----------------------------------------------------------
    var player = CreateBox("Player", new Vector2(300, 540), new Vector2(40, 56),
                           new Color(90, 200, 255));
    player.Tag = "Player";
    scene.AddActor(player, "default");

    // Follow it.
    Camera.Follow.Target    = player;
    Camera.Follow.LerpSpeed = 6f;
    Camera.Follow.Offset    = new Vector2(0, -60);
    Camera.Follow.Deadzone  = new Vector2(120, 60);
}
```

Add a temporary mover so there is something to follow — we will replace this in
Tutorial 3:

```csharp
public sealed class AutoMove : Component
{
    public float Speed = 140f;
    public override void Update(float dt)
        => Transform.Position += new Vector2(Speed * dt, 0f);
}
```

```csharp
player.AddComponent<AutoMove>();
```

```bash
dotnet run --project MyGame
```

The player drifts right and the camera follows once it leaves the deadzone.

## 3. Camera2D

```csharp
Camera.Zoom    = 1.5f;                             // clamped to [MinZoom, MaxZoom]
Camera.MinZoom = 0.25f;
Camera.MaxZoom = 4f;
Camera.Bounds  = new Rectangle(0, 0, 3000, 720);   // clamps the camera centre; null = free
```

### Follow

```csharp
Camera.Follow.Target    = playerActor;
Camera.Follow.LerpSpeed = 6f;                    // higher = snappier
Camera.Follow.Offset    = new Vector2(0, -60);   // look slightly ahead/above
Camera.Follow.Deadzone  = new Vector2(120, 60);  // no camera motion inside this box
```

Following runs in `LateUpdate`, **after** gameplay has moved the target, which
is why the camera never lags a frame behind.

A deadzone is what separates a camera that feels calm from one that jitters with
every small movement. Start around a fifth of the screen and tune.

### Shake

```csharp
Camera.Shake(intensity: 0.8f, duration: 0.35f);
Camera.ShakeMaxOffset = 20f;      // pixels at full trauma
Camera.ShakeMaxAngle  = 0.05f;    // radians at full trauma
```

Trauma decays quadratically, so a hit reads as a sharp jolt that settles rather
than a linear fade. Add a test key in `Update`:

```csharp
if (Input.IsKeyPressed(Microsoft.Xna.Framework.Input.Keys.K))
    Camera?.Shake(1f, 0.4f);
```

### Coordinate conversion

```csharp
Vector2 world  = Camera.ScreenToWorld(Input.MousePosition, GraphicsDevice);
Vector2 screen = Camera.WorldToScreen(actor.Transform.Position, GraphicsDevice);
```

`ScreenToWorld` is how you turn a mouse click into a world position — aiming,
selecting, placing.

## 4. Parallax

Parallax is one component: move a layer's actors a fraction of the camera's
movement.

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Components;

/// <summary>
/// Shifts this actor by a fraction of the camera's movement, so it appears
/// further away. 0 = locked to the camera, 1 = locked to the world.
/// </summary>
public sealed class Parallax : Component
{
    public float Factor = 0.3f;

    private Camera2D? _camera;
    private Vector2   _lastCamera;
    private bool      _primed;

    public override void Start()
    {
        _camera = Actor.Scene?.FindByName("Main Camera")?.GetComponent<Camera2D>();
    }

    public override void LateUpdate(float dt)
    {
        if (_camera == null) return;

        Vector2 now = _camera.Actor.Transform.Position;
        if (!_primed) { _lastCamera = now; _primed = true; return; }

        Vector2 delta = now - _lastCamera;
        _lastCamera = now;

        // Move *with* the camera by (1 - Factor) so the layer appears to lag.
        Transform.Position += delta * (1f - Factor);
    }
}
```

Apply it to the star field:

```csharp
var star = CreateBox($"Star{i}", …);
star.AddComponent<Parallax>().Factor = 0.25f;
scene.AddActor(star, "parallax");
```

Distant things get a small `Factor`; near-foreground things get something above
1 to move *faster* than the world.

`LateUpdate` matters here — it runs after `Camera2D.LateUpdate` has moved the
camera, so the delta is for the current frame rather than the previous one.

## 5. SpriteRenderer in depth

```csharp
var sr = actor.AddComponent<SpriteRenderer>();
sr.Texture    = Assets.Load<Texture2D>("Assets/Sprites/hero.png");
sr.Tint       = Color.White;                     // multiplied with the texture
sr.Pivot      = new Vector2(0.5f, 1.0f);         // bottom-centre: feet on the ground
sr.LayerDepth = 0.5f;                            // 0 = front, 1 = back
sr.Effects    = SpriteEffects.FlipHorizontally;
sr.SourceRect = new Rectangle(0, 0, 32, 32);     // a sub-region
```

`Pivot` is normalised: `(0,0)` top-left, `(0.5,0.5)` centre, `(1,1)`
bottom-right. Bottom-centre is the right choice for characters — it puts the
transform position at the feet, so Y-sorting and ground alignment both work.

### Spritesheets

```csharp
sr.Texture     = Assets.Load<Texture2D>("Assets/Sprites/hero_sheet.png");
sr.FrameWidth  = 32;
sr.FrameHeight = 32;
sr.FrameIndex  = 4;      // 5th frame, left-to-right then top-to-bottom
```

`SpriteAnimator` drives `FrameIndex` for you — [Tutorial 9](09-animation-and-tweens.md).

### 9-patch

```csharp
sr.IsSliced    = true;
sr.SliceBorder = 8;                          // corner size, source pixels
sr.SliceSize   = new Vector2(240, 80);       // target size, world units
```

Corners keep their size, edges stretch one axis, the centre stretches both.

## 6. Render settings

`Renderer2D` is engine-owned. Configure it in `OnEngineReady`:

```csharp
protected override void OnEngineReady()
{
    Renderer2D.SamplerState = SamplerState.PointClamp;    // default — crisp pixel art
    Renderer2D.SortMode     = SpriteSortMode.BackToFront; // default — LayerDepth ordering
    Renderer2D.BlendState   = BlendState.AlphaBlend;      // default

    BuildScene();
}
```

Use `SamplerState.LinearClamp` for smooth, non-pixel art. `BackToFront` is what
makes `LayerDepth` mean anything — under `SpriteSortMode.Deferred` the depth
value is ignored and draw order is submission order.

## 7. Debug drawing

While tuning a camera or a layout, draw the shapes you are reasoning about:

```csharp
using SexyBiscuit.Engine.Debug;

public sealed class CameraGizmo : Component
{
    public Vector2 Deadzone;

    public override void Update(float dt)
        => Gizmos.DrawBox2D(Transform.Position, Deadzone * 2f, Color.Yellow);
}
```

Tutorial 1's bootstrap already pumps `Gizmos.Update` and `Gizmos.Flush`, so this
just works. `Gizmos` are drawn in the screen-space batch, so pass screen
coordinates — or move the `Flush` call into the world batch if you prefer world
coordinates.

---

## Checkpoint

You have:

- A layered scene with parallax
- A camera that follows with a deadzone and can shake
- Working knowledge of `SpriteRenderer`, pivots, and depth
- Gizmos for visual debugging

## Exercises

1. Add a `foreground` layer with a `Parallax.Factor` above 1 and watch it lead.
2. Bind mouse-wheel zoom: `Camera.Zoom += Input.ScrollDelta * 0.001f;`
3. Clamp the camera with `Camera.Bounds` so it stops at the level edges.

---

**Next:** [Tutorial 3 — Input & Movement](03-input-and-movement.md)
