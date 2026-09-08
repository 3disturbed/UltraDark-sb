# Tutorial 10 — Scenes & Prefabs

**You will build:** multiple levels with a working loader, spawn factories,
object pooling, and a scene-transition fade. **Time:** ~35 minutes.

Builds on [Tutorial 9](09-animation-and-tweens.md).

---

## 1. `LoadScene` does not read the file

This is the fact that shapes the whole tutorial.

```csharp
// SceneManager.ProcessPendingLoad, in full:
var scene = new Scene(Path.GetFileNameWithoutExtension(path));
```

`SceneManager.LoadScene("Scenes/Level2.scene")` gives you an **empty scene named
"Level2"**. It never opens the file. `SceneSerializer.LoadFromFile` *does* read
the file, but returns a detached `Scene`.

`SceneManager.AdoptScene(scene)` is the piece that joins them — it installs an
already-built scene as the active one, doing everything a scene load should:

```csharp
SceneManager.AdoptScene(SceneSerializer.LoadFromFile("Scenes/Level2.scene"));
```

So you have two viable workflows, and one call that connects them.

## 2. Workflow A — code-first scenes

The approach the demo project uses, and the one these tutorials use. A static
loader per scene:

`MyGame/Scenes/GameScene.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using MyGame.Components;
using MyGame.UI;

namespace MyGame.Scenes;

public static class GameScene
{
    public static void Load(Game game, int levelIndex = 1)
    {
        var scene = game.SceneManager.CreateScene($"Level{levelIndex}");

        var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
        cameraActor.Transform.Position = new Vector2(640, 360);
        var camera = cameraActor.AddComponent<Camera2D>();
        game.SetCamera(camera);
        scene.AddActor(cameraActor);

        var player = Factories.CreatePlayer(game, new Vector2(200, 600));
        scene.AddActor(player, "default");

        camera.Follow.Target   = player;
        camera.Follow.Deadzone = new Vector2(140, 90);

        LevelGeometry.Build(game, scene, levelIndex);

        var canvas = game.CreateCanvas(scene, "HUD");
        game.Hud = new Hud(canvas, game.WhiteTexture, player.GetComponent<Health>()!);
        game.Pause = new PauseMenu(game, canvas);

        game.Music.Play($"Assets/Audio/level{levelIndex}.wav");
    }
}
```

**Advantages:** total control, assets and events wired directly, no format
mismatch, refactorable, greppable. **Cost:** levels live in C# and need a
rebuild to change.

## 3. Workflow B — scene files

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
          "position": [200.0, 600.0],
          "rotation": 0.0,
          "scale": [1.0, 1.0],
          "components": [
            {
              "type": "SexyBiscuit.Engine.Rendering.SpriteRenderer, SexyBiscuit.Engine",
              "properties": { "Tint": "#5AC8FFFF", "LayerDepth": 0.5 }
            },
            {
              "type": "MyGame.Components.SpriteLoader, MyGame",
              "properties": { "TexturePath": "Assets/Sprites/player.png" }
            }
          ]
        }
      ]
    }
  ]
}
```

Three rules you cannot ignore:

**Component type names must be namespace-qualified.** `Type.GetType("Camera2D")`
returns null and the component is skipped with a console warning. Write
`"SexyBiscuit.Engine.Rendering.Camera2D"`, or the assembly-qualified form for
your own types.

**Textures do not round-trip.** Only primitives, `string`, `bool`, `enum`,
`Vector2/3/4`, `Quaternion` and `Color` are serialised. A saved
`SpriteRenderer` keeps its `Tint` and `Pivot` and loses its `Texture`.

**The bundled `Templates/*/Scenes/*.scene` files use a different shape.** They
write `"transform": {x, y, …}` and short type names, which this serialiser does
not read. Treat them as design references.

### The asset-path pattern

The standard workaround for rule two — a serialisable `string` plus a `Start`
that resolves it:

`MyGame/Components/SpriteLoader.cs`:

```csharp
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Components;

/// <summary>
/// Holds a texture path through serialisation and resolves it at Start,
/// because Texture2D itself does not round-trip.
/// </summary>
public sealed class SpriteLoader : Component
{
    public string TexturePath { get; set; } = "";

    public override void Start()
    {
        if (string.IsNullOrWhiteSpace(TexturePath)) return;

        var sr = GetComponent<SpriteRenderer>() ?? AddComponent<SpriteRenderer>();
        sr.Texture = SBEngine.Instance.Assets.Load<Texture2D>(TexturePath);
    }
}
```

Do the same for audio clips, prefab references and anything else that is not a
value type.

### The loader bridge

`SceneManager.AdoptScene` connects the two halves:

```csharp
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;

namespace MyGame;

public static class SceneLoader
{
    /// <summary>Reads a .scene file and installs it as the active scene.</summary>
    public static Scene Load(SceneManager sm, string path)
        => sm.AdoptScene(SceneSerializer.LoadFromFile(path));

    public static void Save(Scene scene, string path)
        => SceneSerializer.SaveToFile(scene, path);
}
```

```csharp
var scene = SceneLoader.Load(SceneManager, "Scenes/Level1.scene");
```

`AdoptScene` destroys the previously active scene (raising `OnSceneUnloaded`),
re-adds every `DontDestroyOnLoad` actor, installs the new scene as
`ActiveScene`, and raises `OnSceneLoaded` — everything a scene load should do.
`LoadScene` is the one to avoid for file-backed scenes.

One ordering note: the actors in a deserialised scene were built by
`BuildActor`, which already ran `AddComponent` — and therefore every
component's `Awake` — before the actor belonged to a scene. **Component work
that needs scene context belongs in `Start`**, which fires when the adopted
scene flushes the actor.

### Round-tripping from code

The pragmatic hybrid: build a level in code once, save it, then iterate on the
JSON.

```csharp
#if DEBUG
if (Input.IsKeyPressed(Keys.F9))
{
    SceneLoader.Save(SceneManager.ActiveScene!, "Scenes/Level1.scene");
    Debug.WriteLine("scene saved");
}
#endif
```

## 4. Factories

Whichever workflow you pick, a factory beats a prefab for anything that needs
assets or event wiring.

`MyGame/Factories.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;
using MyGame.Components;

namespace MyGame;

public static class Factories
{
    public static Actor CreatePlayer(Game game, Vector2 position)
    {
        var actor = new Actor("Player") { Tag = "Player", Layer = Layers.Player };
        actor.Transform.Position   = position;              // BEFORE the rigidbody
        actor.Transform.LocalScale = new Vector2(40, 56);

        var sr = actor.AddComponent<SpriteRenderer>();
        sr.Texture = game.WhiteTexture;
        sr.Tint    = new Color(90, 200, 255);

        var rb = actor.AddComponent<Rigidbody2D>();
        rb.FreezeRotation = true;

        actor.AddBox(new Vector2(40, 56));                  // AddBox rebuilds the fixture

        var cc = actor.AddComponent<CharacterController2D>();
        cc.MoveSpeed = 320f;
        cc.JumpForce = 780f;

        actor.AddComponent<PlatformerInput>();
        actor.AddComponent<Health>().Max = 100;
        actor.AddComponent<HitReaction>();
        return actor;
    }

    public static Actor CreateEnemy(Game game, Vector2 position)
    {
        var actor = new Actor("Enemy") { Tag = "Enemy", Layer = Layers.Enemy };
        actor.Transform.Position   = position;
        actor.Transform.LocalScale = new Vector2(32, 32);

        var sr = actor.AddComponent<SpriteRenderer>();
        sr.Texture = game.WhiteTexture;
        sr.Tint    = new Color(230, 90, 90);

        var rb = actor.AddComponent<Rigidbody2D>();
        rb.FreezeRotation = true;
        actor.AddBox(new Vector2(32, 32));

        actor.AddComponent<Health>().Max = 30;
        actor.AddComponent<HitReaction>();
        actor.AddScript("Scripts/Chase.js");
        return actor;
    }

    public static Actor CreateBullet(Game game)
    {
        var actor = new Actor("Bullet") { Tag = "Bullet" };
        actor.Transform.LocalScale = new Vector2(8, 8);
        actor.LifeSpan = 2.0f;                              // automatic cleanup

        var sr = actor.AddComponent<SpriteRenderer>();
        sr.Texture = game.WhiteTexture;
        sr.Tint    = new Color(255, 230, 140);
        return actor;
    }
}
```

Note the ordering discipline: **position before `Rigidbody2D`** (the body is
created in `Awake` at the current position), and `AddBox` rather than a raw
`AddComponent<BoxCollider2D>` (the fixture is built in `Awake` from the
properties as they stand).

## 5. Prefabs

A prefab is a **single serialised actor** on disk, JSON-cached after first read.

```csharp
using SexyBiscuit.Engine.Scene;

Prefab.Save(enemyActor, "Prefabs/Enemy.prefab");
Prefab.SaveAsync(enemyActor, "Prefabs/Enemy.prefab");     // fire-and-forget write

Actor e1 = Prefab.Instantiate("Prefabs/Enemy.prefab");
Actor e2 = Prefab.Instantiate("Prefabs/Enemy.prefab", new Vector2(400, 200));
Actor e3 = Prefab.Instantiate("Prefabs/Enemy.prefab", new Vector2(400, 200), MathF.PI);

Prefab.InvalidateCache("Prefabs/Enemy.prefab");
Prefab.ClearCache();
```

`Instantiate` adds the actor to the active scene's **`"default"` layer**. With
no active scene it logs to stderr and returns an unparented actor.

**`InstantiateAs<T>` almost always throws.** The prefab format records no actor
subclass, so `BuildActor` always constructs a plain `Actor`:

```csharp
var e = Prefab.InstantiateAs<EnemyActor>("Prefabs/Enemy.prefab");   // InvalidCastException
```

Use a factory for typed actors.

**When to use which:**

| | Prefab | Factory |
|---|---|---|
| Layout / transform / value properties | ✅ | ✅ |
| Textures, clips, event wiring | ❌ | ✅ |
| Editable without a rebuild | ✅ | ❌ |
| Typed actor subclasses | ❌ | ✅ |

The hybrid works well: a prefab for layout, plus a `SpriteLoader`-style
component that resolves assets in `Start`.

## 6. Pooling

`Destroy()` costs a two-frame removal plus component teardown. For bullets,
particles and anything spawned several times a second, pool instead.

```csharp
using SexyBiscuit.Engine.Core;

var bullets = new ActorPool(
    scene,
    factory:   () => Factories.CreateBullet(game),
    layerName: "projectiles",
    prewarm:   64);

Actor b = bullets.Spawn();
b.Transform.Position = muzzle;
b.LifeSpan = 0f;                       // cancel automatic destruction while pooled

// … on hit, or when the bullet's own lifetime expires …
bullets.Despawn(b);

Debug.WriteLine($"{bullets.CountActive} live, {bullets.CountInactive} free");
```

Pooled actors are added to the scene once and toggled with `IsActive`, so
`Start` runs a single time per instance. **Reset per-spawn state yourself right
after `Spawn()`** — velocity, health, timers, tint.

A bullet component that returns itself to its pool:

```csharp
public sealed class Bullet : Component
{
    public Vector2   Velocity;
    public float     Life = 2f;
    public ActorPool? Pool;

    private float _remaining;

    public void Arm(Vector2 position, Vector2 velocity)
    {
        Transform.Position = position;
        Velocity   = velocity;
        _remaining = Life;
    }

    public override void Update(float dt)
    {
        Transform.Position += Velocity * dt;
        _remaining -= dt;
        if (_remaining <= 0f) Recycle();
    }

    public override void OnTriggerEnter(Actor other)
    {
        if (other.Tag == "Bullet" || other.Tag == "Player") return;
        other.GetComponent<Health>()?.Damage(10, Actor);
        Recycle();
    }

    private void Recycle()
    {
        if (Pool != null) Pool.Despawn(Actor);
        else              Actor.Destroy();
    }
}
```

`CountCreated` climbing past your prewarm during play means the prewarm is too
small; `CountActive` climbing without bound means you are missing `Despawn`
calls.

## 7. Transitions

`CreateScene` destroys the current scene immediately, so fade *out* first, swap,
then fade *in*.

`MyGame/Systems/SceneTransition.cs`:

```csharp
using System.Collections;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

namespace MyGame.Systems;

public static class SceneTransition
{
    /// <summary>Fades to black, runs `swap`, then fades back in.</summary>
    public static void Go(Game game, Action swap, float duration = 0.35f)
    {
        var scene  = game.SceneManager.ActiveScene!;
        var canvas = game.CreateCanvas(scene, "Transition");

        // Stretch on both axes covers the screen and keeps covering it through a resize,
        // so there is no size to keep in step with the window.
        var fade = canvas.Root.Add(new UiNode
        {
            WidthMode = SizeMode.Stretch, HeightMode = SizeMode.Stretch,
            Background = Color.Black, Opacity = 0f, Order = 1000,
        });

        game.Coroutines.Start(Run(game, fade, swap, duration));
    }

    private static IEnumerator Run(Game game, UiNode fade, Action swap, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.UnscaledDeltaTime;              // works even while paused
            fade.Opacity = SBMath.Clamp01(t / duration);
            yield return null;
        }

        swap();                                        // destroys the old scene

        // The old canvas died with the old scene, so build a fresh overlay.
        var canvas = game.CreateCanvas(game.SceneManager.ActiveScene!, "Transition");
        var fadeIn = canvas.Root.Add(new UiNode
        {
            WidthMode = SizeMode.Stretch, HeightMode = SizeMode.Stretch,
            Background = Color.Black, Opacity = 1f, Order = 1000,
        });

        t = 0f;
        while (t < duration)
        {
            t += Time.UnscaledDeltaTime;
            fadeIn.Opacity = 1f - SBMath.Clamp01(t / duration);
            yield return null;
        }

        canvas.RemoveWidget(fadeIn);
    }
}
```

```csharp
SceneTransition.Go(game, () => GameScene.Load(game, levelIndex: 2));
```

`yield return null` waits one frame. `Time.UnscaledDeltaTime` means the fade
still runs during a pause.

## 8. Persisting actors across scenes

```csharp
SceneManager.DontDestroyOnLoad(musicActor);
```

Registered actors are re-added to the `"default"` layer of every scene created
by `CreateScene` or by a pending `LoadScene`. They are **not** re-added to
additive scenes.

Good candidates: the physics driver, an audio manager actor, a persistent
game-state holder. Better still for session state: a
[`GameInstance`](../wiki/22-gameplay-framework.md#gameinstance), covered in
Tutorial 14.

## 9. Additive scenes

```csharp
SceneManager.LoadSceneAdditive("Scenes/UILayer.scene");
SceneManager.UnloadScene("UILayer");
foreach (var s in SceneManager.AdditiveScenes) { }
```

Additive scenes update and draw alongside the active one. Bear in mind that,
like `LoadScene`, `LoadSceneAdditive` creates an **empty** scene named after the
file, and `AdoptScene` only replaces the *active* scene — so an additive scene
built from a file still has to be populated by hand.

## 10. World streaming

For a large contiguous world:

```csharp
using SexyBiscuit.Engine.Scene;

var streamer = systemsActor.AddComponent<WorldStreamer>();
streamer.ChunksDirectory  = "Scenes/Chunks/";
streamer.ChunkWidth       = 1024;
streamer.ChunkHeight      = 1024;
streamer.LoadRadius       = 2;
streamer.TrackedActor     = player;
streamer.MaxLoadsPerFrame = 1;              // amortise the hitch
```

Each frame it finds the chunk containing `TrackedActor`, loads chunks within
`LoadRadius` that are not yet loaded, and unloads those outside it. Because it
goes through the scene serialiser, the texture caveat applies — use the
`SpriteLoader` pattern in chunk content.

---

## Checkpoint

You have:

- `AdoptScene`, the call that makes `.scene` files actually load
- Factories with the correct component-ordering discipline
- Pooling for high-churn actors
- A coroutine-driven scene transition
- `DontDestroyOnLoad` for cross-scene actors

## Exercises

1. Add a level-complete trigger that transitions to the next level.
2. Build a level in code, save it with F9, then edit the JSON and load it back.
3. Pool the enemies as well as the bullets, and reset `Health` on spawn.

---

**Next:** [Tutorial 11 — Saving & Loading](11-saving-and-loading.md)
