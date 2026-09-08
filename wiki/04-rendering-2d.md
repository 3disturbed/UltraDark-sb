# 4. 2D Rendering

Namespace: `SexyBiscuit.Engine.Rendering`

The 2D pipeline is a thin, explicit layer over MonoGame's `SpriteBatch`:

```
RenderSystem2D.RenderScene(spriteBatch, scene, camera)
├── SpriteBatch.Begin(sortMode, blend, sampler, …, camera.GetViewMatrix())
├── scene.Draw(sb) → layer.Draw(sb) → actor.InternalDraw(sb) → component.Draw(sb)
│                                                              └── SpriteRenderer, TilemapRenderer,
│                                                                  ParticleEmitter, Canvas…
├── SpriteBatch.End()
└── optional post-process chain via ping-pong render targets
```

---

## RenderSystem2D

`SBEngine` constructs one and initialises it for you:

```csharp
RenderSystem2D r = Renderer2D;      // or SBEngine.Instance.Renderer2D
```

Do not create your own — an uninitialised `RenderSystem2D` throws on the first
`Begin`. (If you do need a second one, for an off-screen pass, call
`Initialize(GraphicsDevice)` on it first.)

Throughout this page, `Renderer2D` is that engine-owned instance.

### Configuration

```csharp
Renderer2D.SortMode     = SpriteSortMode.BackToFront;  // default
Renderer2D.BlendState   = BlendState.AlphaBlend;       // default
Renderer2D.SamplerState = SamplerState.PointClamp;     // default — crisp pixel art
Renderer2D.UsePerLayerRenderTargets = false;           // default
```

`PointClamp` is the default because the engine is pixel-art-first. For smooth
scaled art use `SamplerState.LinearClamp`.

`SortMode = BackToFront` means `SpriteRenderer.LayerDepth` controls ordering
within the batch: **0 = front, 1 = back**.

### Drawing

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);

    var scene = SceneManager.ActiveScene;
    if (scene == null) return;

    if (Config.Enable3D) Renderer3D.Render(scene);       // 3D backdrop, if any
    Renderer2D.RenderScene(SpriteBatch, scene, _camera);

    // No base.Draw(gameTime) — it would issue an unbatched second scene draw.
}
```

`RenderScene` handles `Begin`/`End` for you. If you need to draw your own
geometry inside the camera transform, use the lower-level pair:

```csharp
Renderer2D.Begin(SpriteBatch, _camera);
// … your own sb.Draw calls, in world space …
Renderer2D.End(SpriteBatch);
```

`Begin` throws `InvalidOperationException` if called twice without an `End`,
and `End` throws if called without a `Begin`. There is also an overload taking
an explicit `Effect`:

```csharp
Renderer2D.Begin(SpriteBatch, _camera, myLitSpriteEffect);
```

### Per-layer render targets

```csharp
Renderer2D.UsePerLayerRenderTargets = true;
```

Each named layer is drawn into its own `RenderTarget2D`, cleared to
transparent, then composited in layer order. Use this when you want a
per-layer effect (a blurred background layer, a layer-specific tint). It costs
one full-screen target per layer, so leave it off unless you need it.

### Post-processing

```csharp
var effect = Assets.Load<Effect>("Assets/Shaders/grayscale.fx");   // see note below
Renderer2D.PostProcessPasses.Add(new PostProcessPass(effect, e =>
{
    e.Parameters["Intensity"]?.SetValue(0.8f);
}));
```

Passes run in order through ping-pong render targets after the scene is drawn.
The `ConfigureEffect` callback runs once per pass per frame — set your uniforms
there.

> `AssetManager` cannot load `Effect` (see [13. Assets](13-assets.md)). Compile
> shaders through MonoGame's content pipeline and load them with
> `Content.Load<Effect>("Shaders/grayscale")`, or construct an `Effect` from a
> compiled `.mgfxo` byte array yourself.

`RenderSystem2D.Dispose()` releases the ping-pong and per-layer targets. The
engine-owned `Renderer2D` is disposed with the engine, so you only call this on
an instance you created yourself.

---

## SpriteRenderer

The workhorse component. Attach to an actor; it draws `Texture` at the actor's
world transform.

```csharp
var sprite = actor.AddComponent<SpriteRenderer>();
sprite.Texture    = Assets.Load<Texture2D>("Assets/Sprites/player.png");
sprite.Tint       = Color.White;
sprite.Pivot      = new Vector2(0.5f, 0.5f);   // normalised: (0,0) top-left, (1,1) bottom-right
sprite.LayerDepth = 0.5f;                      // 0 = front, 1 = back
sprite.Effects    = SpriteEffects.FlipHorizontally;
```

| Property | Type | Meaning |
|---|---|---|
| `Texture` | `Texture2D?` | Nothing is drawn when `null`. |
| `SourceRect` | `Rectangle?` | Sub-region of the texture. `null` = whole texture. |
| `Tint` | `Color` | Multiplied with the texture. |
| `Effects` | `SpriteEffects` | Horizontal / vertical flip. |
| `LayerDepth` | `float` | Sort key when `SortMode` is depth-based. 0 front, 1 back. |
| `Pivot` | `Vector2` | Normalised origin. Default centre. |

### Spritesheets

Set `FrameWidth` and `FrameHeight`, then drive `FrameIndex`. The renderer
computes `SourceRect` from a left-to-right, top-to-bottom grid.

```csharp
sprite.Texture     = Assets.Load<Texture2D>("Assets/Sprites/hero_sheet.png");
sprite.FrameWidth  = 32;
sprite.FrameHeight = 32;
sprite.FrameIndex  = 4;      // 5th frame
```

`SpriteAnimator` drives `FrameIndex` for you — see
[10. Animation](10-animation.md).

### 9-patch (sliced) sprites

For scalable panels and buttons drawn as world sprites:

```csharp
sprite.IsSliced    = true;
sprite.SliceBorder = 8;                        // corner size in source pixels
sprite.SliceSize   = new Vector2(200, 64);     // target size in world units
```

The corners keep their size, edges stretch along one axis, and the centre
stretches both ways — nine `sb.Draw` calls per sprite.

---

## Camera2D

A component. The actor's `Transform.Position` is the camera centre.

```csharp
var cameraActor = new Actor("Main Camera");
var camera = cameraActor.AddComponent<Camera2D>();
scene.AddActor(cameraActor);
```

```csharp
camera.Zoom    = 2f;                              // clamped to [MinZoom, MaxZoom]
camera.MinZoom = 0.1f;
camera.MaxZoom = 10f;
camera.Bounds  = new Rectangle(0, 0, 4000, 2000); // clamp the camera centre; null = unbounded
```

### Follow

```csharp
camera.Follow.Target    = playerActor;
camera.Follow.LerpSpeed = 5f;                       // higher = snappier
camera.Follow.Offset    = new Vector2(0, -50);
camera.Follow.Deadzone  = new Vector2(60, 40);      // no movement inside this box
```

Following runs in `LateUpdate`, after gameplay has moved the target — which is
why the camera never lags a frame behind the player.

### Shake

Trauma-based: intensity decays quadratically over the duration, so a hit feels
punchy rather than linear.

```csharp
camera.Shake(intensity: 1f, duration: 0.4f);
camera.ShakeMaxOffset = 20f;      // pixels at full trauma
camera.ShakeMaxAngle  = 0.05f;    // radians at full trauma
```

### Coordinate conversion

```csharp
Vector2 world  = camera.ScreenToWorld(Input.MousePosition, GraphicsDevice);
Vector2 screen = camera.WorldToScreen(actor.Transform.Position, GraphicsDevice);
Matrix  view   = camera.GetViewMatrix(GraphicsDevice);
```

The view matrix maps the camera position to the **viewport centre**, then
applies rotation and zoom:

```
translate(-camPos) · rotateZ(-camRot) · scale(zoom) · translate(viewport/2)
```

so an actor at the camera's position renders in the middle of the screen.

---

## TilemapRenderer

Renders a `TilemapData` grid. Loads [Tiled](https://www.mapeditor.org/) JSON
maps.

```csharp
var map = TilemapData.LoadFromJson("Assets/Maps/level1.json");
map.Tileset        = Assets.Load<Texture2D>("Assets/Maps/tileset.png");
map.TilesetColumns = 16;

var tiles = actor.AddComponent<TilemapRenderer>();
tiles.Map        = map;
tiles.LayerDepth = 0.9f;      // behind most sprites
tiles.Tint       = Color.White;
```

`TilemapData`:

```csharp
public class TilemapData
{
    public int Width, Height;              // in tiles
    public int TileWidth, TileHeight;      // in pixels
    public List<TileLayer> Layers;
    public Texture2D? Tileset;
    public int TilesetColumns;
    public static TilemapData LoadFromJson(string jsonPath);
}

public class TileLayer
{
    public string Name = "Layer";
    public int[]  Tiles = [];              // 0 = empty, otherwise 1-based tile id
    public bool   Visible = true;
    public float  Opacity = 1f;
}
```

Queries:

```csharp
Vector2 tileCoord = tiles.WorldToTile(worldPosition);
int id = tiles.GetTile(layerIndex: 0, x: 3, y: 7);
int id2 = tiles.GetTile("collision", 3, 7);
```

The Tiled importer reads `width`, `height`, `tilewidth`, `tileheight`, `layers`
and `tilesets`, and handles both the array and the base64/`datastr` forms of
layer data. Assign `Tileset` and `TilesetColumns` yourself after loading — the
importer does not resolve image paths.

> There is no automatic tilemap collider. Build static colliders yourself by
> walking a collision layer and calling `PhysicsSystem2D.Instance.CreateBody` or
> adding `BoxCollider2D` actors — see
> [6. Physics](06-physics.md#building-collision-from-a-tilemap).

---

## ParticleEmitter

A CPU particle system drawn as sprites. Pool size is fixed at
`MaxParticles`; emission over time or as bursts.

```csharp
var fx = actor.AddComponent<ParticleEmitter>();
fx.MaxParticles     = 300;
fx.EmitRate         = 60f;                        // particles per second
fx.Loop             = true;
fx.ParticleTexture  = Assets.Load<Texture2D>("Assets/Sprites/spark.png");

fx.MinLifetime = 0.3f;  fx.MaxLifetime = 1.2f;
fx.MinVelocity = new Vector2(-80, -220);
fx.MaxVelocity = new Vector2( 80, -120);
fx.StartColor  = Color.Orange;
fx.EndColor    = Color.Transparent;               // fades out
fx.MinStartSize = 6f;  fx.MaxStartSize = 12f;  fx.EndSize = 0f;
fx.GravityScale = 1f;  fx.WorldGravity = 980f;    // pixels/s², applied × GravityScale
```

Control:

```csharp
fx.Play();
fx.Stop();          // stops emitting; live particles finish
fx.Clear();         // kills every live particle immediately
fx.Burst();         // emits BurstCount particles at once

fx.BurstMode  = true;
fx.BurstCount = 50;
```

Particles are simulated in the emitter's `Update` and drawn in its `Draw`, so
they land inside whatever batch `RenderSystem2D` opened — meaning they respect
the camera transform and are in **world space**.

---

## Layering strategy

Two independent mechanisms control what draws on top:

1. **Layer order** — `Scene.AddLayer(name, order)`. Coarse, and the only way to
   get per-layer render targets or independent `Active`/`Visible` toggles.
2. **`SpriteRenderer.LayerDepth`** — fine-grained, within a batch, when
   `SortMode` is `BackToFront` or `FrontToBack`.

And one that catches people out:

3. **`Layer.Draw` sorts actors by `Transform.LocalPosition.Y` ascending** before
   drawing. In a top-down game this is what you want. In a side-scroller it is
   not — put background and foreground content on separate *layers* so the Y
   sort only ever reorders things that should be Y-sorted.

A workable convention:

| Layer | Order | Contents |
|---|---|---|
| `background` | −100 | parallax, sky, far tilemap |
| `ground` | −50 | walkable tilemap |
| `default` | 0 | actors that Y-sort against each other |
| `projectiles` | 50 | bullets, VFX |
| `foreground` | 100 | canopy, near parallax |
| `ui` | 200 | `UiCanvas` actors |

---

## Where the UI is drawn

Nothing here. A `UiCanvas` is a `Component` but it does **not** draw through the component
pass, which runs inside the camera-transformed batch — that is what used to make a HUD pan,
zoom and shake with the camera. The host collects every canvas and paints it afterwards, in
its own screen-space batch ordered by `UiCanvas.Order`, and there is nothing for a game to
wire up.

A canvas whose `Space` is `World` is painted earlier still, into its own texture, and hung on
a quad by the 3D pass — so it *does* move with the camera, which for a screen bolted to a wall
is the whole point. See [9. UI](09-ui.md#world-space).

---

## Next

- [5. 3D Rendering](05-rendering-3d.md)
- [9. UI](09-ui.md)
- [Tutorial 2: Sprites & Cameras](../tutorials/02-sprites-and-cameras.md)
