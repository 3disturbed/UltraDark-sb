# 24. Localization, Pooling & Utilities

A grab-bag of engine services that do not need a page each but will save you
writing them.

---

# Localization — `Loc`

Namespace: `SexyBiscuit.Engine.Localization`

A string table with runtime language switching and named argument substitution.

## Table format

One flat JSON object per language, keyed by a **stable identifier** rather than
English text — so rewording the English copy does not invalidate every other
language.

`Assets/Locales/en.json`:

```json
{
  "ui.menu.start":    "Start Game",
  "ui.menu.options":  "Options",
  "ui.menu.quit":     "Quit",
  "hud.score":        "Score: {score}",
  "hud.wave":         "Wave {current} of {total}",
  "dialogue.elder.1": "Welcome, traveller."
}
```

`Assets/Locales/fr.json`:

```json
{
  "ui.menu.start":   "Commencer",
  "ui.menu.options": "Options",
  "ui.menu.quit":    "Quitter",
  "hud.score":       "Score : {score}"
}
```

## Loading and switching

```csharp
using SexyBiscuit.Engine.Localization;

int loaded = Loc.LoadDirectory("Assets/Locales");   // one table per *.json
Loc.LoadFile("Assets/Locales/de.json");             // language code from the filename
Loc.LoadTable("pt-BR", jsonString);

Loc.FallbackLanguage = "en";
Loc.SetLanguageFromSystem();                        // from CultureInfo
bool ok = Loc.SetLanguage("fr");                    // false if not loaded

Loc.CurrentLanguage;
Loc.AvailableLanguages;                             // IEnumerable<string>
Loc.Clear();
```

## Lookup

```csharp
label.Text = Loc.Get("ui.menu.start");
bool has   = Loc.Has("ui.menu.start");

hud.Text = Loc.Format("hud.score", ("score", 1200));
hud.Text = Loc.Format("hud.wave", ("current", 3), ("total", 10));
```

**A missing key returns the key itself.** `Loc.Get("ui.menu.stat")` renders as
`ui.menu.stat` on screen — obvious during testing, and it keeps layout roughly
right while translation is in progress. Missing keys also fall back to
`FallbackLanguage` first, so a partially translated build stays usable.

## Rebuilding UI on a language change

```csharp
Loc.LanguageChanged.Add(code =>
{
    startButton.Text   = Loc.Get("ui.menu.start");
    optionsButton.Text = Loc.Get("ui.menu.options");
    quitButton.Text    = Loc.Get("ui.menu.quit");
});
```

For a larger UI, keep the key on the widget and refresh in a loop:

```csharp
public sealed class LocalizedLabel : Component
{
    public Label  Target = null!;
    public string Key    = "";

    public override void Start()
    {
        Refresh();
        Loc.LanguageChanged.Add(_ => Refresh());
    }

    private void Refresh() => Target.Text = Loc.Get(Key);
}
```

Remember `SBEvent` holds strong references — `Loc.LanguageChanged.Remove(...)`
in `OnDestroy`, or the label outlives its scene.

## Practical notes

- Font coverage is your problem: a `SpriteFont` baked for ASCII will not render
  accented or CJK glyphs. Bake per-language character sets, or move to
  FontStashSharp.
- Leave room in layouts — German and Finnish routinely run 40 % longer than
  English.
- Persist the choice: `PlayerPrefs.SetString("lang", Loc.CurrentLanguage)`.

---

# Object pooling

Namespace: `SexyBiscuit.Engine.Core`

## `ObjectPool<T>`

```csharp
var pool = new ObjectPool<Bullet>(
    factory:  () => new Bullet(),
    onRent:   b => b.Enabled = true,
    onReturn: b => b.Enabled = false,
    prewarm:  64);

Bullet b = pool.Rent();
pool.Return(b);

pool.CountInactive;   // ready to rent
pool.CountActive;     // rented out
pool.CountCreated;    // ever allocated
pool.Clear();
```

**Not thread-safe by design** — pool from the game thread only.

`CountCreated` climbing past your prewarm during play tells you the prewarm is
too small. `CountActive` climbing without bound means you are missing `Return`
calls.

## `ActorPool`

The scene-aware wrapper — the one you will actually use for bullets, pickups and
enemies.

```csharp
var bullets = new ActorPool(
    scene,
    factory:   () => BuildBullet(),
    layerName: "projectiles",
    prewarm:   64);

Actor b = bullets.Spawn();
b.Transform.Position = muzzle;
// … on hit or lifetime expiry …
bullets.Despawn(b);

bullets.CountActive;
bullets.CountInactive;
```

Pooled actors are added to the scene once and toggled with `IsActive`, so
`Start` runs a single time per instance. Reset per-spawn state yourself
immediately after `Spawn()`, or in an `onRent` callback on the underlying
`ObjectPool`.

Use a pool whenever you would otherwise create and `Destroy()` actors every few
frames. `Destroy()` costs a two-frame removal dance plus a component teardown;
`Despawn` costs a bool.

---

# 2D lighting

Namespace: `SexyBiscuit.Engine.Rendering`

A light-map pass composited over the 2D scene.

```csharp
var lighting = new Lighting2D();
lighting.Initialize(GraphicsDevice);
lighting.AmbientColor = new Color(28, 30, 44);
lighting.Downsample   = 1;             // >1 renders the light map smaller and cheaper
```

Lights and shadow casters are components that register themselves:

```csharp
var lamp = new Actor("Lamp");
lamp.Transform.Position = new Vector2(400, 300);
var light = lamp.AddComponent<Light2D>();
light.Type       = Light2DType.Point;    // Point, Spot, …
light.Color      = new Color(255, 220, 150);
light.Intensity  = 1.4f;
light.Radius     = 260f;
light.Falloff    = 2f;
light.SpotAngle  = 45f;                  // Spot only
light.CastsShadows = true;

var wall = new Actor("Wall");
var caster = wall.AddComponent<ShadowCaster2D>();
caster.SetBox(128, 32);                  // or assign caster.Points directly
```

Drive it from `Draw`, between the world pass and the UI pass:

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);
    var scene = SceneManager.ActiveScene;
    if (scene == null) return;

    Renderer2D.RenderScene(SpriteBatch, scene, _camera);

    _lighting.BuildLightMap(_camera!.GetViewMatrix(GraphicsDevice));
    _lighting.Composite(SpriteBatch);          // multiplies the light map over the scene

    SpriteBatch.Begin();
    scene.GetLayer("ui")?.Draw(SpriteBatch);   // UI stays unlit
    SpriteBatch.End();
}
```

```csharp
lighting.Enabled         = false;      // skip the pass entirely
lighting.LightMap;                     // RenderTarget2D?, for debugging
lighting.NormalMapEffect = effect;     // optional normal-mapped lighting
lighting.SceneNormalMap  = normals;
lighting.Dispose();
```

`Light2D.All` and `ShadowCaster2D.All` are the static registries the pass reads.
Raise `Downsample` to 2 before optimising anything else — a half-resolution
light map is usually indistinguishable and roughly a quarter of the cost.

---

# Tilemap collision

Namespace: `SexyBiscuit.Engine.Physics`

`TilemapCollider2D` builds static collision from a `TilemapRenderer` layer, and
**merges runs of solid tiles into maximal rectangles** with a greedy sweep — so
a 200×200 map becomes a few dozen boxes instead of 40 000 fixtures.

```csharp
var mapActor = new Actor("Map");
var tiles = mapActor.AddComponent<TilemapRenderer>();
tiles.Map = TilemapData.LoadFromJson("Assets/Maps/level1.json");

var collider = mapActor.AddComponent<TilemapCollider2D>();
collider.LayerName   = "collision";      // empty = the first layer
collider.IsSolid     = id => id > 0;     // predicate over tile ids
collider.Friction    = 0.3f;
collider.Restitution = 0f;
collider.IsTrigger   = false;
collider.MergeTiles  = true;             // false for one box per tile
collider.Rebuild();

Debug.WriteLine($"{collider.FixtureCount} fixtures");
```

`Start` calls `Rebuild()` automatically. Call it again after editing tiles —
destructible terrain, a level editor, a procedurally extended map:

```csharp
tiles.Map!.Layers[0].Tiles[index] = 0;   // blow a hole
collider.Rebuild();
```

`Clear()` removes the geometry; `OnDestroy` calls it for you.

Use different `IsSolid` predicates for different layers to build water volumes,
ladders or damage zones as separate trigger colliders on child actors.

---

# 3D constraints

Namespace: `SexyBiscuit.Engine.Physics`

Joints between two BepuPhysics bodies. All derive from `Constraint3D`:

```csharp
constraint.SpringFrequency = 30f;    // stiffness
constraint.SpringDamping   = 1f;
constraint.Handle;                   // ConstraintHandle
constraint.IsActive;
constraint.Rebuild();                // after changing parameters
constraint.Remove();
```

| Type | Joint |
|---|---|
| `BallSocketConstraint` | a point both bodies share — a shoulder, a chain link |
| `HingeConstraint` | rotation about one axis — a door, a wheel |
| `SliderConstraint` | translation along one axis — a piston, an elevator |
| `DistanceLimitConstraint` | keeps bodies between `MinDistance` and `MaxDistance` — a rope |
| `AngularWeldConstraint` | locks relative orientation |

```csharp
var rope = anchor.AddComponent<DistanceLimitConstraint>();
rope.MinDistance = 0f;
rope.MaxDistance = 4f;
rope.Rebuild();
```

Like every physics component, a constraint reads its configuration when it is
built — set the properties, then call `Rebuild()`.

---

# Maths helpers — `SBMath`

Namespace: `SexyBiscuit.Engine.Core`

```csharp
SBMath.Epsilon;  SBMath.Deg2Rad;  SBMath.Rad2Deg;

SBMath.Approximately(a, b, tolerance);
SBMath.Clamp01(v);
SBMath.InverseLerp(a, b, value);
SBMath.Remap(v, inMin, inMax, outMin, outMax);
SBMath.SmoothStep(edge0, edge1, x);

SBMath.MoveTowards(current, target, maxDelta);        // float and Vector3
SBMath.SafeNormalize(v);                              // zero instead of NaN
SBMath.SignedAngle(from, to, axis);
SBMath.ProjectOnPlane(v, planeNormal);

SBMath.WrapAngle(degrees);                            // → −180..180
SBMath.DeltaAngle(from, to);
SBMath.LerpAngle(from, to, t);                        // shortest way round
SBMath.MoveTowardsAngle(from, to, maxDelta);

SBMath.RandomRange(0f, 1f);
SBMath.RandomRange(0, 10);                            // [min, max)
SBMath.SetSeed(1234);                                 // reproducible runs
SBMath.Random;                                        // the shared Random
```

## `Damp` — use this instead of `Lerp` in an Update

```csharp
// Framerate-dependent: converges faster at 144fps than at 30fps.
pos = Vector2.Lerp(pos, target, 0.1f);

// Framerate-independent: halfLife seconds to close half the gap, at any framerate.
pos = SBMath.Damp(pos, target, halfLife: 0.15f, dt);
```

Overloads exist for `float`, `Vector2` and `Vector3`. This is the single most
useful function in the file — camera follow, aim smoothing, UI easing and
`OrbitCamController.SmoothingHalfLife` all use it.

---

# Bounds

```csharp
var b = new Bounds(center, size);        // size, not extents
Bounds.Zero;
Bounds.FromPoints(points);
Bounds.FromMinMax(min, max);

b.Center;  b.Extents;  b.Size;  b.Min;  b.Max;  b.BoundingRadius;

b.Encapsulate(point);
b.Encapsulate(otherBounds);
b.Expand(0.5f);
b.Contains(point);
b.Intersects(other);
b.ClosestPoint(point);
b.DistanceTo(point);
b.Transform(matrix);                     // conservative AABB of the result
b.ToBoundingBox();  b.ToBoundingSphere();
```

Used by frustum culling in `RenderSystem3D`, by `NavMesh.Bounds`, and by LOD
sizing.

---

## Next

- [20. API Index](20-api-index.md)
- [21. Gotchas](21-gotchas.md)
