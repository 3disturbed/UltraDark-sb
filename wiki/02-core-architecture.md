# 2. Core Architecture

SexyBiscuit uses an **Actor / Component** model that will feel familiar if you
have used Unity, with one extra level in the hierarchy: **Layers**.

```
SBEngine  (the MonoGame Game host, one per process)
└── SceneManager
    ├── ActiveScene : Scene
    │   └── Layer[]              ordered by Layer.Order
    │       └── Actor[]
    │           ├── Transform    always present, index 0
    │           └── Component[]
    └── AdditiveScenes : Scene[]
```

Source: `SexyBiscuit.Engine/Core/`.

---

## SBEngine

`SBEngine` extends `Microsoft.Xna.Framework.Game`. It owns the window, the
graphics device, and four services.

```csharp
public class SBEngine : Game
{
    public static SBEngine Instance { get; }     // set in the constructor

    public GraphicsDeviceManager Graphics     { get; }
    public SpriteBatch           SpriteBatch  { get; }
    public SceneManager          SceneManager { get; }
    public AssetManager          Assets       { get; }
    public InputManager          Input        { get; }
    public AudioManager          Audio        { get; }
    public EngineConfig          Config       { get; }

    protected virtual void OnEngineReady();      // override this
    public static void Run(EngineConfig? config = null);

    // Hosting the engine inside an app that owns the device and the loop:
    public bool IsHosted { get; }
    public void InitializeHosted(GraphicsDevice gd, ContentManager content);
    public void TickHosted(float rawDeltaSeconds);
}
```

`SBEngine.Instance` is assigned in the constructor, so it is valid from the
moment you construct the engine. The services are constructed in `Initialize()`,
immediately before `OnEngineReady()` — **`OnEngineReady` is the earliest safe
place to touch `Assets`, `Input`, `Audio`, or `SceneManager`.**

An editor or tool that already owns the graphics device calls
[`InitializeHosted`](03-game-loop.md#hosting-the-engine-inside-another-app)
instead of `Run()`; it builds the same services and calls `OnEngineReady` the
same way.

### EngineConfig

```csharp
public class EngineConfig
{
    public string WindowTitle   = "SexyBiscuit Engine";
    public int    WindowWidth   = 1920;
    public int    WindowHeight  = 1080;
    public bool   Fullscreen    = false;
    public bool   VSync         = true;
    public bool   ShowCursor    = true;
    public bool   AllowResize   = true;
    public float  FixedTimestep = 1f / 60f;
    public Color  ClearColour   = Color.CornflowerBlue;
    public bool   HotReload     = true;
    public string StartScene    = "";
}
```

`Content.RootDirectory` is hard-coded to `"Assets"`, and `IsFixedTimeStep` is set
to `false` — the engine runs an uncapped update and does its own fixed-step
accumulation for `FixedUpdate`.

> `StartScene` and `HotReload` are configuration *data*: nothing in `SBEngine`
> reads them. Load your first scene yourself in `OnEngineReady`.

---

## Scene

A `Scene` is a named container of `Layer`s.

```csharp
var scene = SceneManager.CreateScene("Level1");
```

`SceneManager` calls `Scene.Destroy()` when a scene is replaced or unloaded.
**Call it yourself for a scene you built with `new Scene(...)`** — in a test, a
tool, or a headless server. It is not optional bookkeeping: components register
themselves in static registries (`MeshRenderer.All`, `Light3D.All`,
`Light2D.All`, `PlayerStart.All`, `SkinnedMeshRenderer.All`,
`ParticleSystem3D.All`) and only leave them in `OnDestroy`, so a dropped scene
stays visible to the renderer and to spawn selection forever.

Every new `Scene` is created with four default layers:

| Name | Order |
|---|---|
| `background` | −100 |
| `default` | 0 |
| `foreground` | 100 |
| `ui` | 200 |

Layers are kept sorted by `Order` ascending, and drawn in that order — **lower
order draws first, i.e. behind**.

```csharp
scene.AddLayer("particles", 50);           // between default and foreground
Layer ui = scene.GetOrCreateLayer("ui", 200);
scene.RemoveLayer("foreground");           // destroys its actors
```

### Adding and finding actors

```csharp
scene.AddActor(myActor);                   // goes to "default"
scene.AddActor(myActor, "ui");             // named layer, created if missing
var enemy = scene.AddActor<EnemyActor>();  // T : Actor, new()

Actor? boss   = scene.FindByName("Boss");
var    coins  = scene.FindByTag("Coin");             // IEnumerable<Actor>
var    enemies = scene.FindActorsOfType<EnemyActor>(); // IEnumerable<T>
```

`FindByName` returns the first match across all layers, searched in layer order.
These are linear scans — cache the result if you need it every frame.

### Destruction is deferred

```csharp
actor.Destroy();               // → scene.MarkForDestroy(actor)
actor.Destroy(delaySeconds);   // → sets LifeSpan
bool gone = actor.IsDestroyed;
```

`Destroy()` on an already-destroyed actor is a no-op.

`Destroy()` queues the actor. `FlushDestroyQueue` runs at the end of
`Scene.Update`, after every layer has updated: it hands each actor to its
layer's remove queue and then flushes that queue, so `OnDestroy` fires and the
actor leaves the scene **within the same frame**. Destroying an actor from
inside its own `Update` — or from a sibling's — is safe.

Adds are the other way round: `Layer.AddActor` queues, and `FlushPending()` at
the start of the layer's **next** `Update` admits the actor and runs its
`Start`. Both queues are drained into a scratch buffer before being walked, so
an actor whose `Start` spawns more actors (a `GameMode` spawning its controller
and pawn, say) works correctly — the newcomers land on the following frame.

---

## Layer

```csharp
public class Layer
{
    public string Name;
    public int    Order;
    public bool   Visible = true;   // false → skipped in Draw
    public bool   Active  = true;   // false → skipped in Update/FixedUpdate/LateUpdate
    public IReadOnlyList<Actor> Actors { get; }
}
```

`Visible` and `Active` are independent, which makes a layer a convenient unit for
pausing (`Active = false` on gameplay layers while the pause menu layer keeps
updating) or for hiding debug geometry.

### Draw order within a layer — read this

`Layer.Draw` sorts its actors every frame:

```csharp
var sorted = _actors
    .Where(a => a.IsActive)
    .OrderBy(a => a.Transform.LocalPosition.Y);
```

That is a **painter's algorithm on local Y** — the right default for top-down
2D, where things lower on screen should overlap things higher up. Two
consequences worth knowing:

1. It sorts on **local** Y, not world Y. A child actor parented under a moving
   parent sorts by its offset, not its on-screen position.
2. It is a fresh LINQ sort per layer per frame. For layers with thousands of
   actors, split them across layers or accept the cost.

If you want depth control independent of position, use
`SpriteRenderer.LayerDepth` together with `RenderSystem2D.SortMode =
SpriteSortMode.BackToFront` — see [4. 2D Rendering](04-rendering-2d.md).

---

## Actor

```csharp
public class Actor
{
    public string    Name     = "Actor";
    public string    Tag      = "Untagged";
    public int       Layer    = 0;          // an int tag, NOT the Layer object
    public bool      IsActive = true;
    public float     LifeSpan = 0f;         // >0 → self-destructs after this many seconds
    public bool      IsDestroyed { get; }
    public Transform Transform { get; }     // always present
    public Scene?    Scene     { get; }
    public Layer?    Layer_    { get; }     // the owning Layer object
    public uint      Id        { get; }     // process-unique, from a static counter

    public Actor?               Parent   { get; }   // null at the scene root
    public IReadOnlyList<Actor> Children { get; }
}
```

### Attachment

Actors form a tree. Attaching drives **both** transforms, which is the reason it lives
on the actor rather than on a `Transform`: a 3D actor parented through the 2D transform
alone inherits nothing, because no 3D renderer reads that transform.

```csharp
turret.AttachTo(tank);                              // keeps its world position
muzzle.AttachTo(turret, keepWorldTransform: false); // treats its transform as a local offset
turret.Detach();                                    // back to the scene root
tank.DetachChildren();                              // let the children go, keep the tank
```

```csharp
actor.Parent;                 // Actor?
actor.Children;               // IReadOnlyList<Actor>
actor.Root;                   // topmost ancestor, or itself
actor.IsActiveInHierarchy;    // false when any ancestor is inactive
actor.IsDescendantOf(other);
actor.FindChild("Turret");                  // direct children
actor.FindChild("Muzzle", recursive: true); // the whole subtree
actor.FindChildByPath("Turret/Barrel/Muzzle");
actor.Descendants();          // depth first, parents before their children
actor.HierarchyPath;          // "Tank/Turret/Muzzle"
```

Three rules worth knowing before you rely on them:

- **`keepWorldTransform` defaults to `true`.** Attaching a pickup to a moving player leaves
  it exactly where it is. Pass `false` when the child's current transform is meant to be the
  socket offset — which is what the scene loader does, because a file already stores a
  child's transform in its parent's space.
- **Destroying a parent destroys its subtree.** A turret must not be left hanging in the air
  when its tank dies. Call `DetachChildren()` first when the children really are meant to
  survive.
- **A cycle throws.** Every walk here is an unguarded loop, so a cycle is a hang rather than
  a wrong answer, and `AttachTo` refuses to create one.

Adding an actor to a scene adds everything attached to it, and `MoveActor` moves the whole
subtree — so a prefab, a duplicate or a loaded scene never leaves a child unregistered and
invisible. Scene files nest children inside their parent (see
[12. Scenes and Prefabs](12-scenes-prefabs.md)).

### Lifespan

```csharp
var bullet = new Actor("Bullet") { LifeSpan = 2.5f };   // gone in 2.5 scaled seconds
impact.Destroy(delaySeconds: 0.4f);                     // same thing, imperative
```

`LifeSpan` counts down in `InternalUpdate` on **scaled** time, so it pauses with
`Time.TimeScale`. It saves a bespoke timer component on every projectile, decal
and impact effect.

### Coroutines on an actor

```csharp
Coroutine c = actor.StartCoroutine(Blink());
actor.StopCoroutine(c);
actor.StopAllCoroutines();

IEnumerator Blink()
{
    while (true)
    {
        IsActive = !IsActive;
        yield return new WaitForSeconds(0.2f);
    }
}
```

Coroutines started this way are **owned by the actor** and cancelled
automatically in `InternalDestroy` — a coroutine cannot outlive its target.
See [3. The Game Loop](03-game-loop.md#coroutines).

> `Actor.Layer` (an `int`) and `Actor.Layer_` (the `Layer` object) are two
> different things. `Layer` is a numeric layer index used for physics raycast
> masking; `Layer_` is the container the actor lives in. The naming is
> unfortunate — expect to reach for `Layer_` more often than you would like.

### Components

```csharp
var rb = actor.AddComponent<Rigidbody2D>();     // T : Component, new()
var sr = actor.GetComponent<SpriteRenderer>();  // null if absent
if (actor.TryGetComponent<AudioSource>(out var audio)) audio.Play();
foreach (var c in actor.GetComponents<Collider2D>()) { }
bool has = actor.HasComponent<Camera2D>();
actor.RemoveComponent<ParticleEmitter>();       // calls OnDestroy first
Component c = actor.AddComponentByType(typeof(SpriteRenderer));
```

`AddComponent<T>` honours `[RequireComponent]`:

```csharp
[RequireComponent(typeof(Rigidbody2D))]
public sealed class CharacterController2D : Component { }
```

Adding a `CharacterController2D` auto-adds a `Rigidbody2D` first if one is
missing. The attribute is `AllowMultiple = true`, so a component can declare
several dependencies.

### Subclassing Actor

`Actor` exposes `protected virtual` lifecycle hooks, so gameplay objects can be
written as actor subclasses rather than components. The demo uses this style:

```csharp
public class PlayerActor : Actor
{
    private CharacterController3D _controller = null!;

    public PlayerActor() : base("Player")
    {
        Tag = "Player";
        _controller = AddComponent<CharacterController3D>();   // auto-adds Rigidbody3D
    }

    protected override void OnStart()        { }
    protected override void Update(float dt) { }
    protected override void FixedUpdate(float dt) { }
    protected override void LateUpdate(float dt)  { }
    protected override void Draw(SpriteBatch sb)  { }
    protected override void OnDestroy()      { }
}
```

Note the constructor hook is `OnStart`, not `Start` — the naming differs from
`Component`, which uses `Start`.

Collision callbacks are `public virtual` on `Actor` (not `protected`), because
the physics system calls them from outside:

```csharp
public virtual void OnCollisionEnter(CollisionData data);
public virtual void OnCollisionStay(CollisionData data);
public virtual void OnCollisionExit(CollisionData data);
public virtual void OnTriggerEnter(Actor other);
public virtual void OnTriggerStay(Actor other);
public virtual void OnTriggerExit(Actor other);
```

The base implementations forward to every enabled component, so **if you
override one, call `base` or your components stop receiving it.**

---

## Component

```csharp
public abstract class Component
{
    public Actor Actor   { get; }
    public bool  Enabled { get; set; } = true;

    public virtual void Awake();
    public virtual void Start();
    public virtual void Update(float dt);
    public virtual void FixedUpdate(float dt);
    public virtual void LateUpdate(float dt);
    public virtual void Draw(SpriteBatch sb);
    public virtual void OnDestroy();

    public virtual void OnCollisionEnter(CollisionData data);
    public virtual void OnCollisionStay(CollisionData data);
    public virtual void OnCollisionExit(CollisionData data);
    public virtual void OnTriggerEnter(Actor other);
    public virtual void OnTriggerStay(Actor other);
    public virtual void OnTriggerExit(Actor other);

    protected Transform Transform => Actor.Transform;
    public T? GetComponent<T>() where T : Component;
    public T  AddComponent<T>() where T : Component, new();
}
```

`CollisionData` carries the contact:

```csharp
public readonly struct CollisionData
{
    public Actor   Other            { get; init; }
    public Vector2 ContactPoint     { get; init; }
    public Vector2 Normal           { get; init; }
    public float   RelativeVelocity { get; init; }
}
```

---

## Lifecycle order

This is the exact order, traced through `SBEngine` → `SceneManager` → `Scene` →
`Layer` → `Actor`.

### At attach time

```
actor.AddComponent<T>()
  └── [RequireComponent] dependencies added first (recursively)
  └── component.Actor = actor
  └── component.Awake()            ← immediately, synchronously
  └── component.Start()            ← only if the actor has already started
```

**`Awake()` runs the instant you call `AddComponent`.** You cannot configure a
component before its `Awake` runs. This matters for `ScriptComponent`
(see [11. Scripting](11-scripting.md#the-scriptpath-ordering-problem)) and for
`Rigidbody2D`, which creates its physics body in `Awake`.

### At the first update after `AddActor`

`Layer.AddActor` puts the actor in a pending list. On the next
`Layer.Update`, `FlushPending()` runs:

```
layer.FlushPending()
  └── actor.InternalStart()
        ├── actor.OnStart()             (the Actor subclass hook)
        └── component.Start() for each enabled component
```

So there is always **at least one frame** between `AddActor` and `Start`.

### Per frame

```
SBEngine.Update(gameTime)
├── Input.Update(dt)
├── while (accumulator >= Config.FixedTimestep)     ← may run 0..n times
│   └── SceneManager.FixedUpdate(FixedTimestep)
│       └── scene.FixedUpdate → layer.FixedUpdate → actor.InternalFixedUpdate
│           ├── actor.FixedUpdate(dt)
│           └── component.FixedUpdate(dt) for each enabled component
├── SceneManager.Update(dt)
│   ├── ProcessPendingLoad()                        ← deferred LoadScene
│   ├── activeScene.Update(dt)
│   │   ├── layer.Update  → FlushPending() → actor.InternalUpdate
│   │   │   ├── actor.Update(dt)
│   │   │   └── component.Update(dt)
│   │   └── FlushDestroyQueue()   → removes + flushes, same frame
│   └── each additive scene .Update(dt)
├── SceneManager.LateUpdate(dt)
│   └── … actor.LateUpdate → component.LateUpdate
└── Audio.Update(dt)

SBEngine.Draw(gameTime)
├── GraphicsDevice.Clear(Config.ClearColour)
└── SceneManager.Draw(SpriteBatch)
    └── scene.Draw → layer.Draw (sorted) → actor.InternalDraw
        ├── actor.Draw(sb)
        └── component.Draw(sb)
```

Three things to take from this diagram:

1. **`FixedUpdate` runs before `Update` in a frame**, and may run zero or
   several times depending on frame time. There is no clamp on the accumulator,
   so a long stall produces a burst of catch-up steps.
2. **Destruction completes within the frame.** `FlushDestroyQueue` at the end of
   `Scene.Update` removes the actor and flushes the layer immediately, so
   `OnDestroy` has run and the actor is gone before `LateUpdate`.
3. **`SceneManager.Draw` is called without an open `SpriteBatch`.** See
   [3. The Game Loop](03-game-loop.md).

Component lists are snapshotted with `.ToArray()` before each iteration, so
adding or removing a component from inside a lifecycle callback is safe.

---

## Transform (2D)

`Transform` is a `sealed Component` created in the `Actor` constructor and
attached at index 0. Every actor has exactly one; you never add it yourself.

```csharp
// Local space — relative to the parent
transform.LocalPosition;   // Vector2
transform.LocalRotation;   // float, radians
transform.LocalScale;      // Vector2

// World space — computed lazily, cached until dirtied
transform.Position;        // Vector2
transform.Rotation;        // float, radians
transform.Scale;           // Vector2

transform.Right;           // (cos θ, sin θ)
transform.Up;              // (−sin θ, cos θ)

transform.SetParent(other.Transform, keepWorldPosition: true);
transform.Parent;          // Transform?
transform.Children;        // IReadOnlyList<Transform>

transform.GetLocalMatrix();
transform.GetWorldMatrix();
transform.TransformPoint(localPoint);
transform.InverseTransformPoint(worldPoint);
transform.LookAt(worldTarget);
transform.DistanceTo(otherTransform);
```

World values are recomputed on demand: any local write sets a dirty flag, and
the next world read recalculates and propagates the dirty flag to children.
Rotation is **radians everywhere** in 2D — `MathHelper.ToRadians` /
`ToDegrees` when you need degrees. (The editor Inspector shows degrees and
converts on write.)

---

## Transform3D

`Transform3D` is a separate component. It is **not** added automatically — a 3D
actor has both a `Transform` (from the `Actor` constructor) and a `Transform3D`
that you add.

```csharp
var actor = new Actor("Cube");
var t3d = actor.AddComponent<Transform3D>();
t3d.Position = new Vector3(0, 2, -5);
t3d.EulerAngles = new Vector3(0, 45, 0);   // degrees, converted to a quaternion
```

```csharp
t3d.LocalPosition;  t3d.LocalRotation;  t3d.LocalScale;  t3d.LocalEulerAngles;
t3d.Position;       t3d.Rotation;       t3d.Scale;       t3d.EulerAngles;
t3d.Forward;        t3d.Right;          t3d.Up;
t3d.SetParent(parentT3d, keepWorldTransform: true);
t3d.TransformPoint(p);  t3d.InverseTransformPoint(p);  t3d.TransformDirection(d);
t3d.LookAt(target, up: null);
t3d.GetLocalMatrix();   t3d.GetWorldMatrix();
```

Rotation is a `Quaternion`; `EulerAngles` are **degrees** (the opposite of the
2D `Transform`, which is radians). Euler order is yaw-pitch-roll via
`Quaternion.CreateFromYawPitchRoll(y, x, z)`.

---

## SceneManager

```csharp
public class SceneManager
{
    public Scene? ActiveScene { get; }
    public IReadOnlyList<Scene> AdditiveScenes { get; }

    public event Action<Scene>? OnSceneLoaded;
    public event Action<Scene>? OnSceneUnloaded;

    public Scene CreateScene(string name = "Scene");
    public Scene AdoptScene(Scene scene);
    public void  LoadScene(string scenePath);
    public void  LoadSceneAdditive(string scenePath);
    public Task  LoadSceneAsync(string scenePath, Action<float>? onProgress, bool additive);
    public void  UnloadScene(string sceneName);
    public void  DontDestroyOnLoad(Actor actor);
}
```

### `LoadScene` does not read the file — Verified

`LoadScene(path)` sets a pending flag. On the next `Update`,
`ProcessPendingLoad` destroys the current scene and creates a **new empty
`Scene` named after the file's base name**. It never opens the file.

```csharp
// What actually happens inside ProcessPendingLoad:
var scene = new Scene(Path.GetFileNameWithoutExtension(path));
```

So `LoadScene("Scenes/Level2.scene")` gives you an empty scene called
`"Level2"`. This is by design for a code-first workflow — the demo builds each
scene from a static loader class:

```csharp
public static class MainMenuScene
{
    public static void Load(SceneManager sm)
    {
        var scene = sm.CreateScene("MainMenu");
        // … build the scene in code …
    }
}
```

To actually load a scene *file*, deserialise it and hand it to `AdoptScene`:

```csharp
using SexyBiscuit.Engine.Scene;

var loaded = SceneSerializer.LoadFromFile("Scenes/Level2.scene");
SceneManager.AdoptScene(loaded);
```

### AdoptScene

```csharp
public Scene AdoptScene(Scene scene);
```

Makes an already-built `Scene` the active one: destroys whatever was active
before (raising `OnSceneUnloaded`), re-adds every `DontDestroyOnLoad` actor,
and raises `OnSceneLoaded`. It is the path for a scene you constructed
yourself — deserialised from a file, generated procedurally, or assembled by a
tool. `CreateScene` only makes an empty one.

More in [12. Scenes & Prefabs](12-scenes-prefabs.md#loading-a-scene-file).

`LoadSceneAsync` awaits an empty `Task.Run` and then calls `LoadScene` —
it reports progress `0f` then `1f`, and does no background work.

### DontDestroyOnLoad

```csharp
SceneManager.DontDestroyOnLoad(musicActor);
```

Registered actors are re-added to the `"default"` layer of every scene created
by `CreateScene` or by a pending `LoadScene`. They are not re-added to additive
scenes.

---

## Next

- [3. The Game Loop](03-game-loop.md) — what you must drive yourself.
- [12. Scenes & Prefabs](12-scenes-prefabs.md) — the on-disk format.
