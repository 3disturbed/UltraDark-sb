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
          "class": "Character",
          "tag": "Player",
          "layer": 1,
          "active": true,
          "position": [400.0, 300.0],
          "rotation": 0.0,
          "scale": [1.0, 1.0],
          "components": [
            {
              "type": "SpriteRenderer",
              "properties": {
                "Tint": "#FFFFFFFF",
                "LayerDepth": 0.5,
                "Pivot": [0.5, 0.5],
                "TexturePath": "Assets/Sprites/player.png"
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
| `actors[].class` | string | the `Actor` subclass (`GameMode`, `Character`, a project class); omitted for a plain `Actor` |
| `actors[].properties` | object | public get/set properties declared by that subclass |
| `actors[].layer` | int | the numeric `Actor.Layer`, used for raycast masks |
| `actors[].active` | bool | default `true` |
| `actors[].position` | `[x, y]` | **local** 2D position |
| `actors[].rotation` | float | **local** 2D rotation, radians |
| `actors[].scale` | `[x, y]` | **local** 2D scale |
| `actors[].position3` | `[x, y, z]` | local `Transform3D` position; absent when the actor has none |
| `actors[].rotation3` | `[x, y, z, w]` | local `Transform3D` rotation quaternion |
| `actors[].scale3` | `[x, y, z]` | local `Transform3D` scale |
| `actors[].lifeSpan` | float? | `Actor.LifeSpan`; absent when the actor lives indefinitely |
| `components[].type` | string | a type name, short by default — see below |
| `components[].properties` | object | property name → value |
| `actors[].children` | array | actors attached to this one, same shape, written last |

### Attachment

A child is nested inside its parent, after `components`, and a layer's `actors` list holds
**only roots** — an actor appears in the file exactly once:

```json
{
  "name": "Tank",
  "position": [100.0, 0.0],
  "components": [],
  "children": [
    {
      "name": "Turret",
      "position": [0.0, 10.0],
      "components": [],
      "children": [
        { "name": "Muzzle", "position": [0.0, 15.0], "components": [] }
      ]
    }
  ]
}
```

Every transform in the file is already **local**, so loading attaches with
`keepWorldTransform: false`: the offsets are applied exactly as written rather than rebased,
and `Muzzle` above ends up at world `[100, 25]`. Adding the root to a scene adds the whole
subtree, so nothing has to walk it — see [Attachment](02-core-architecture.md#attachment).

A prefab is one entry from this array, so a prefab carries its subtree too.

Property names are matched **case-insensitively**
(`PropertyNameCaseInsensitive = true`).

### Type names

The serialiser writes **short type names** (`"Camera2D"`, `"SpriteRenderer"`) and
resolves them across every loaded assembly, including a project's hot-reloaded
game assembly. A full name (`SexyBiscuit.Engine.Rendering.Camera2D`) or an
assembly-qualified one from an older file still resolves — the assembly
qualifier is ignored, so a scene saved against one engine build loads against
the next. Only when two loaded types share a short name does the writer fall
back to the full name. `SceneSerializerOptions.TypeNames` (`Short`, `Full`,
`AssemblyQualified`) changes what `Serialize` / `SaveToFile` write.

A component whose type cannot be resolved — a game class that was renamed, or is
not loaded yet — is kept as a `MissingComponent` placeholder carrying the type
name and its properties, and is written back out unchanged, so nothing is lost;
loading logs one warning per placeholder. An actor whose `class` cannot be
resolved loads as a plain `Actor` with a `MissingActorClass` marker and keeps its
components.

Actor subclasses record their class (`"class": "Character"`). On load the
subclass is constructed, so components its constructor adds are filled in from
the file rather than duplicated, and `Prefab.InstantiateAs<T>` works for a prefab
saved from a `T`.

### Which properties round-trip

`GetSerializableProperties` keeps public instance properties with a **public
getter and a public setter** whose type is serialisable: primitives, `string`,
`bool`, `enum`, `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Color`,
`Material3D`, and `List<T>` of any of those. Properties marked `[SceneIgnore]` are
skipped, as are read-only runtime values such as a triangle count.

GPU and audio resources (`Texture2D`, `SoundEffect`, `Effect`, `Model`) are not
serialisable, so the components that hold them expose a **path twin** that is:

| Component | Path property | Resolved |
|---|---|---|
| `SpriteRenderer` | `TexturePath` | on `Start`, or at once when set while attached |
| `AudioSource` | `ClipPath` | on `Start` |
| `MeshRenderer` | `ModelPath`, `AlbedoTexturePath` (a proxy for the first material's albedo map) | on the first `Draw`, when a device is in hand |
| `Material3D` | `AlbedoMapPath`, `NormalMapPath`, `MetallicMapPath`, `RoughnessMapPath`, `EmissiveMapPath`, `ShaderPath` | `ResolveTextures()` after the scene loads |

Paths are relative to the project root (`ProjectPaths.Root`) and load through the
running host's `AssetManager.Current`; with no host running (headless tools,
tests) the path is kept and resolved later. Set the path and the asset follows.
Your own components need the same pattern — a `string` path plus a `Start` that
resolves it:

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

`SceneManager.LoadScene(path)` reads the file on the next `Update` when it
exists (`.scene` or `.json`, resolved against `ProjectPaths.Root`) and falls back
to an empty scene with a warning when it does not. `SceneSerializer.LoadFromFile`
+ `SceneManager.AdoptScene` is the immediate, synchronous route, and what the
editor uses:

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

### The bundled `.scene` templates

The files in `Templates/*/Scenes/*.scene` use this format with short type names,
which the serialiser reads, so a template project's default scene loads as-is.
The remaining mismatch is the camera tag (`"MainCamera"` rather than
`"MainCamera3D"`); see [21. Gotchas](21-gotchas.md#three-different-camera-tags).

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

### `InstantiateAs<T>`

```csharp
var enemy = Prefab.InstantiateAs<EnemyActor>("Prefabs/Enemy.prefab");
```

Works when the prefab was saved from an `EnemyActor`: the `class` field records
the subclass and `BuildActor` constructs it. A prefab saved from a plain `Actor`
still throws `InvalidCastException` for any other `T`. A C# factory remains the
better pattern when instances need assets, events or components wired in code:

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
| **`SceneSerializer` files** | data-driven levels, editor round-trip, the Assistant's tools | assets load through the path twins; scenes only reach disk when saved |
| **Prefab + factory hybrid** | many similar objects | prefabs cover layout, factories cover assets |

The demo's `MainMenuScene.Load(sm)` / `OverworldScene.Load(sm)` pattern is the
most reliable option today, and the one the tutorials use.

---

## Next

- [13. Assets](13-assets.md)
- [25. AI Assistant & MCP](25-ai-assistant-mcp.md) — building scenes by talking to Claude
- [Tutorial 10: Scenes & Prefabs](../tutorials/10-scenes-and-prefabs.md)
