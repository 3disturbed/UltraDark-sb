# 12. Scenes, Prefabs & Serialisation

Namespace: `SexyBiscuit.Engine.Scene`

---

## The scene file format

`SceneSerializer` reads and writes this shape:

```json
{
  "name": "Level1",
  "layers": [
    {
      "name": "default",
      "order": 0,
      "actors": [
        {
          "name": "Player",
          "tag": "Player",
          "layer": 1,
          "active": true,
          "position": [400.0, 300.0],
          "rotation": 0.0,
          "scale": [1.0, 1.0],
          "components": [
            {
              "type": "SexyBiscuit.Engine.Rendering.SpriteRenderer, SexyBiscuit.Engine",
              "properties": {
                "Tint": "#FFFFFFFF",
                "LayerDepth": 0.5,
                "Pivot": [0.5, 0.5]
              }
            }
          ]
        }
      ]
    }
  ]
}
```

Field by field:

| Field | Type | Notes |
|---|---|---|
| `name` | string | scene name |
| `layers[].name` | string | resolved with `GetOrCreateLayer` |
| `layers[].order` | int | draw order |
| `actors[].name` / `tag` | string | |
| `actors[].layer` | int | the numeric `Actor.Layer`, used for raycast masks |
| `actors[].active` | bool | default `true` |
| `actors[].position` | `[x, y]` | **local** 2D position |
| `actors[].rotation` | float | **local** 2D rotation, radians |
| `actors[].scale` | `[x, y]` | **local** 2D scale |
| `actors[].position3` | `[x, y, z]` | local `Transform3D` position; absent when the actor has none |
| `actors[].rotation3` | `[x, y, z, w]` | local `Transform3D` rotation quaternion |
| `actors[].scale3` | `[x, y, z]` | local `Transform3D` scale |
| `actors[].lifeSpan` | float? | `Actor.LifeSpan`; absent when the actor lives indefinitely |
| `components[].type` | string | a .NET type name — see below |
| `components[].properties` | object | property name → value |

Property names are matched **case-insensitively**
(`PropertyNameCaseInsensitive = true`).

### Component type names must resolve — Verified

```csharp
Type? type = Type.GetType(compDto.Type);
if (type is null)
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    { type = asm.GetType(compDto.Type); if (type != null) break; }
```

Both lookups need a **namespace-qualified** name. The serialiser writes
`type.AssemblyQualifiedName`, which always round-trips. A short name like
`"Camera2D"` resolves to `null` and the component is skipped with:

```
[SceneSerializer] Could not resolve component type 'Camera2D'. Skipping.
```

Hand-written scene files must therefore use at least the full name:

```json
{ "type": "SexyBiscuit.Engine.Rendering.Camera2D" }
```

or, for a component in your own game assembly:

```json
{ "type": "MyGame.Components.PlayerController, MyGame" }
```

### Which properties round-trip

`GetSerializableProperties` keeps public instance properties that are **both
readable and writable** and whose type is serialisable:
primitives, `string`, `bool`, `enum`, `Vector2`, `Vector3`, `Vector4`,
`Quaternion` and `Color`.

Everything else is skipped — notably **`Texture2D`, `SoundEffect`, `Effect`,
`SpriteFont`, and every collection type**. A serialised `SpriteRenderer`
keeps its `Tint`, `Pivot`, `LayerDepth`, `FrameWidth`, `FrameHeight`,
`FrameIndex`, `IsSliced` and `SliceBorder`, and loses its `Texture`.

Assign textures after loading:

```csharp
foreach (var actor in scene.FindByTag("Enemy"))
    if (actor.GetComponent<SpriteRenderer>() is { } sr)
        sr.Texture = Assets.Load<Texture2D>("Assets/Sprites/enemy.png");
```

Or store the path in a custom string property on your own component and resolve
it in `Start`:

```csharp
public sealed class SpriteLoader : Component
{
    public string TexturePath { get; set; } = "";     // round-trips as a string

    public override void Start()
    {
        if (string.IsNullOrEmpty(TexturePath)) return;
        var sr = GetComponent<SpriteRenderer>() ?? AddComponent<SpriteRenderer>();
        sr.Texture = SBEngine.Instance.Assets.Load<Texture2D>(TexturePath);
    }
}
```

This pattern — a serialisable `string` path plus a `Start` that resolves it —
is the standard way to get assets into scene files.

Converters, all in `SceneSerializer.cs`:

| Type | JSON |
|---|---|
| `Vector2` | `[x, y]` |
| `Vector3` | `[x, y, z]` |
| `Vector4` | `[x, y, z, w]` |
| `Quaternion` | `[x, y, z, w]` |
| `Color` | `"#RRGGBBAA"` |

The `Transform` component is skipped in the component list; it is stored as the
flat `position` / `rotation` / `scale` fields on the actor.

---

## Reading and writing scenes

```csharp
using SexyBiscuit.Engine.Scene;

string json = SceneSerializer.Serialize(scene);
Scene loaded = SceneSerializer.Deserialize(json);

SceneSerializer.SaveToFile(scene, "Scenes/Level1.scene");
Scene s = SceneSerializer.LoadFromFile("Scenes/Level1.scene");
```

`SaveToFile` creates intermediate directories. Output is indented and omits
nulls.

### Loading a scene file

`SceneManager.LoadScene` does **not** read the file — it creates an empty scene
named after it. `SceneSerializer.LoadFromFile` reads the file and returns a
detached `Scene`. `SceneManager.AdoptScene` connects the two:

```csharp
using SexyBiscuit.Engine.Scene;

var loaded = SceneSerializer.LoadFromFile("Scenes/Level1.scene");
SceneManager.AdoptScene(loaded);
```

`AdoptScene` destroys the previously active scene (raising `OnSceneUnloaded`),
re-adds every `DontDestroyOnLoad` actor, installs the new scene as
`ActiveScene`, and raises `OnSceneLoaded` — everything a scene load should do.

Wrap the pair if you load scenes from more than one place:

```csharp
public static class SceneLoader
{
    public static Scene Load(SceneManager sm, string path)
        => sm.AdoptScene(SceneSerializer.LoadFromFile(path));

    public static void Save(Scene scene, string path)
        => SceneSerializer.SaveToFile(scene, path);
}
```

One ordering note: the actors in a deserialised scene were built by
`BuildActor`, which already ran `AddComponent` — and therefore every
component's `Awake` — before the actor belonged to a scene. **Component work
that needs scene context belongs in `Start`**, which fires when the adopted
scene flushes the actor on its next `Update`.

### The bundled `.scene` templates use a different shape

The files in `Templates/*/Scenes/*.scene` are written for an editor-side format
that `SceneSerializer` does not read:

```json
{
  "name": "Village",
  "layers": [{ "name": "Default", "actors": [{
    "name": "Hero",
    "components": [ { "type": "Camera2D", "properties": { "Zoom": 1.0 } } ],
    "transform": { "x": 400, "y": 300, "rotation": 0, "scaleX": 1, "scaleY": 1 }
  }]}]
}
```

Two incompatibilities: `"transform": {x, y, …}` instead of
`"position"/"rotation"/"scale"`, and short component type names. Loading one
through `SceneSerializer` yields actors at the origin with no components.

Treat them as **design references**. If you want to load them, write a small
converter:

```csharp
static string ConvertTemplateScene(string json)
{
    using var doc = JsonDocument.Parse(json);
    // Map "transform" → position/rotation/scale, and short type names →
    // "SexyBiscuit.Engine.<Namespace>.<Name>" via a lookup table.
    // …
}
```

or build a lookup from short name to `Type` and pre-process
`components[].type` before deserialising:

```csharp
static readonly Dictionary<string, Type> ShortNames =
    typeof(SBEngine).Assembly.GetTypes()
        .Where(t => typeof(Component).IsAssignableFrom(t) && !t.IsAbstract)
        .ToDictionary(t => t.Name, t => t);
```

---

## Prefabs

A prefab is a **single serialised actor** on disk, cached in memory after the
first read.

```csharp
using SexyBiscuit.Engine.Scene;

Prefab.Save(actor, "Prefabs/Enemy.prefab");
Prefab.SaveAsync(actor, "Prefabs/Enemy.prefab");    // fire-and-forget write

Actor e1 = Prefab.Instantiate("Prefabs/Enemy.prefab");
Actor e2 = Prefab.Instantiate("Prefabs/Enemy.prefab", new Vector2(400, 200));
Actor e3 = Prefab.Instantiate("Prefabs/Enemy.prefab", new Vector2(400, 200), MathF.PI);

Prefab.InvalidateCache("Prefabs/Enemy.prefab");
Prefab.ClearCache();
```

`Instantiate` deserialises, applies the position/rotation override, and adds the
actor to `SBEngine.Instance.SceneManager.ActiveScene` — on the **`"default"`
layer**. With no active scene it logs to stderr and returns an unparented actor.

The prefab JSON is one `ActorDto`, the same shape as an entry in a scene's
`actors` array, with the same type-name and serialisable-property rules.

### `InstantiateAs<T>` will usually throw

```csharp
var enemy = Prefab.InstantiateAs<EnemyActor>("Prefabs/Enemy.prefab");   // InvalidCastException
```

`ActorDto` records no actor subclass — `BuildActor` always constructs a plain
`Actor`. `InstantiateAs<T>` therefore only succeeds for `T = Actor`. For typed
actors, write a factory instead:

```csharp
public static EnemyActor SpawnEnemy(Scene scene, Vector2 pos)
{
    var e = new EnemyActor();                 // your constructor adds components
    e.Transform.Position = pos;
    scene.AddActor(e);
    return e;
}
```

This is generally the better pattern anyway: a C# factory can assign textures,
wire events, and set up components that do not survive serialisation.

### Pooling

Prefab instantiation allocates an actor and every component each time. For
bullets and particles, pool instead:

```csharp
public sealed class ActorPool
{
    private readonly Stack<Actor> _free = new();
    private readonly Func<Actor>  _factory;
    private readonly Scene        _scene;
    private readonly string       _layer;

    public ActorPool(Scene scene, Func<Actor> factory, string layer = "default", int prewarm = 32)
    {
        _scene = scene; _factory = factory; _layer = layer;
        for (int i = 0; i < prewarm; i++)
        {
            var a = factory();
            a.IsActive = false;
            _scene.AddActor(a, layer);
            _free.Push(a);
        }
    }

    public Actor Rent(Vector2 position)
    {
        Actor a;
        if (_free.Count > 0) a = _free.Pop();
        else { a = _factory(); _scene.AddActor(a, _layer); }

        a.Transform.Position = position;
        a.IsActive = true;
        return a;
    }

    public void Return(Actor a)
    {
        a.IsActive = false;
        _free.Push(a);
    }
}
```

Return instead of `Destroy()`. Remember that a pooled actor's `Start` runs only
once, so reset state in your own `Rent` hook.

---

## WorldStreamer

Loads and unloads chunk scene files around a tracked actor.

```csharp
var streamer = actor.AddComponent<WorldStreamer>();
streamer.ChunksDirectory  = "Scenes/Chunks/";
streamer.ChunkWidth       = 1024;
streamer.ChunkHeight      = 1024;
streamer.LoadRadius       = 2;              // chunks in each direction
streamer.TrackedActor     = player;
streamer.MaxLoadsPerFrame = 1;              // amortise the hitch
```

Each frame it computes the chunk containing `TrackedActor`, loads any chunk
files within `LoadRadius` that are not yet loaded (at most `MaxLoadsPerFrame`
per frame), and unloads those outside it. Chunk files are named by grid
coordinate inside `ChunksDirectory`.

Because it goes through the scene serialiser, the
[texture round-trip caveat](#which-properties-round-trip) applies to streamed
chunks too — use the `SpriteLoader`-style pattern for chunk content.

---

## Choosing a workflow

| Approach | Good for | Cost |
|---|---|---|
| **Code-first scene loaders** (what the demo does) | full control, assets and events wired directly, no format mismatch | scenes live in C#, rebuilt to change |
| **`SceneSerializer` files** | data-driven levels, editor round-trip | assets need the path-property pattern; type names must be qualified |
| **Prefab + factory hybrid** | many similar objects | prefabs cover layout, factories cover assets |

The demo's `MainMenuScene.Load(sm)` / `OverworldScene.Load(sm)` pattern is the
most reliable option today, and the one the tutorials use.

---

## Next

- [13. Assets](13-assets.md)
- [Tutorial 10: Scenes & Prefabs](../tutorials/10-scenes-and-prefabs.md)
