# Tutorial 6 — A Physics Platformer

**You will build:** a side-scrolling platformer with gravity, jumping, one-way
platforms, tilemap collision and the game feel that makes jumping enjoyable.
**Time:** ~45 minutes.

Builds on [Tutorial 4](04-components.md).

---

## 1. Units — decide this first

`PhysicsSystem2D.FixedStep` copies Aether body positions **straight onto**
`Transform.Position` with no scaling:

```csharp
actor.Transform.Position = PhysicsConvert.ToXna(body.Position);
```

So **1 world unit = 1 physics metre**. With the default gravity of `(0, 9.8)`,
an actor at `(400, 300)` is 400 metres from the origin and falls at 9.8 m/s²,
which is about one pixel per second if you draw one unit as one pixel.

Two workable conventions:

| | Convention A — metres | Convention B — pixels |
|---|---|---|
| Positions | ~1.8 units tall character | ~56 pixels tall character |
| Gravity | `(0, 9.8)` | `(0, 1800)` |
| Camera | `Zoom = 64` (px per metre) | `Zoom = 1` |
| Solver behaviour | tuned for this | jitterier stacks, more tunnelling |

This tutorial uses **Convention B**, because it matches everything built so far
and because `CharacterController2D`'s own defaults (`MoveSpeed = 300`,
`JumpForce = 700`) are pixel-scale values. The trade-off is that Aether's
tolerances and sleep thresholds are metre-tuned, so compensate with a smaller
fixed timestep and heavier damping. Convention A is the better choice for a game
built around stacking, ragdolls or rope.

## 2. Configure the physics step

`SBEngine` steps both simulations inside its fixed loop, gated on
`Config.EnablePhysics2D` / `Config.EnablePhysics3D`:

```csharp
while (_fixedAccumulator >= step && steps < Config.MaxFixedStepsPerFrame)
{
    if (Config.EnablePhysics2D) PhysicsSystem2D.Instance.FixedStep(step);
    if (Config.EnablePhysics3D) PhysicsSystem3D.Instance.FixedStep(step);

    SceneManager.FixedUpdate(step);
    _fixedAccumulator -= step;
    steps++;
}
```

Two consequences worth holding onto:

**Physics steps *before* `FixedUpdate`.** The transform your `FixedUpdate` reads
is the result of the step that just ran, and a force you apply there is
simulated on the **next** step. That is a coherent ordering — react to the world,
then push on it — but it means an impulse takes one step to show up.

**`MaxFixedStepsPerFrame` caps catch-up.** After a hitch the accumulator is
drained at most that many steps per frame, and reset if it is still too far
behind. Without that cap a slow frame cascades into a permanently slower
simulation.

Configure the clock and gravity:

```csharp
public Game() : base(new EngineConfig
{
    // …
    FixedTimestep         = 1f / 120f,   // finer step: less tunnelling at pixel scale
    MaxFixedStepsPerFrame = 8,
    EnablePhysics2D       = true,
    EnablePhysics3D       = false,       // 2D game: skip Bepu entirely
    Enable3D              = false,
})
{ }

protected override void OnEngineReady()
{
    PhysicsSystem2D.Instance.Gravity = new Vector2(0f, 1800f);   // pixels/s², Y down
    BuildScene();
}
```

A finer `FixedTimestep` is the main lever against tunnelling. At 1/120 s a
780 px/s jump moves 6.5 px per step, so a 24 px-thick platform is never skipped.

## 3. Colliders — and the trap

`Collider2D.Awake` builds its Aether fixture **immediately**, from whatever the
properties are at that moment. Since `Awake` runs inside `AddComponent`, this
silently does nothing:

```csharp
var box = actor.AddComponent<BoxCollider2D>();
box.Size = new Vector2(40, 56);          // ← too late, the fixture is already 1×1
```

`Collider2D.OnDestroy` and `Awake` are both `public override`, so rebuilding is
legal. `MyGame/PhysicsExtensions.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;

namespace MyGame;

public static class PhysicsExtensions
{
    /// <summary>Destroys and re-creates the fixture so shape changes take effect.</summary>
    public static T Rebuild<T>(this T collider) where T : Collider2D
    {
        collider.OnDestroy();
        collider.Awake();
        return collider;
    }

    /// <summary>Adds a correctly-sized box collider in one call.</summary>
    public static BoxCollider2D AddBox(this Actor actor, Vector2 size,
                                       bool isTrigger = false, PhysicsMaterial2D? material = null)
    {
        var c = actor.AddComponent<BoxCollider2D>();
        c.Size      = size;
        c.IsTrigger = isTrigger;
        c.Material  = material;
        return c.Rebuild();
    }

    /// <summary>Adds a correctly-sized circle collider in one call.</summary>
    public static CircleCollider2D AddCircle(this Actor actor, float radius, bool isTrigger = false)
    {
        var c = actor.AddComponent<CircleCollider2D>();
        c.Radius    = radius;
        c.IsTrigger = isTrigger;
        return c.Rebuild();
    }
}
```

Use these everywhere. Forgetting `Rebuild()` produces a 1×1 collider on a
56-pixel sprite, which looks like "physics is broken" and is not.

`Rigidbody2D` has the same shape of problem — it creates its body in `Awake` at
the actor's **current** position, so set the position first:

```csharp
var actor = new Actor("Crate");
actor.Transform.Position = spawn;         // FIRST
var rb = actor.AddComponent<Rigidbody2D>();
```

## 4. The level

```csharp
protected virtual void BuildScene()
{
    var scene = SceneManager.CreateScene("Level1");

    var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
    cameraActor.Transform.Position = new Vector2(640, 360);
    Camera = cameraActor.AddComponent<Camera2D>();
    scene.AddActor(cameraActor);

    // --- Static geometry -------------------------------------------------
    Solid(scene, new Vector2(640, 700), new Vector2(2400, 64));   // floor
    Solid(scene, new Vector2(300, 560), new Vector2(220, 32));
    Solid(scene, new Vector2(700, 470), new Vector2(220, 32));
    Solid(scene, new Vector2(1080, 380), new Vector2(220, 32));
    Solid(scene, new Vector2(-540, 400), new Vector2(64, 600));   // left wall

    // --- Player -----------------------------------------------------------
    var player = CreateBox("Player", new Vector2(200, 600), new Vector2(40, 56),
                           new Color(90, 200, 255));
    player.Tag   = "Player";
    player.Layer = Layers.Player;

    var rb = player.AddComponent<Rigidbody2D>();
    rb.FreezeRotation = true;
    rb.LinearDamping  = 0f;
    rb.Mass           = 1f;

    player.AddBox(new Vector2(40, 56));

    var controller = player.AddComponent<CharacterController2D>();
    controller.MoveSpeed = 320f;
    controller.JumpForce = 780f;

    player.AddComponent<PlatformerInput>();
    scene.AddActor(player, "default");

    Camera.Follow.Target    = player;
    Camera.Follow.LerpSpeed = 8f;
    Camera.Follow.Offset    = new Vector2(0, -80);
    Camera.Follow.Deadzone   = new Vector2(140, 90);
}

private Actor Solid(Scene scene, Vector2 position, Vector2 size)
{
    var actor = CreateBox("Solid", position, size, new Color(64, 76, 96));
    actor.Tag   = "Ground";
    actor.Layer = Layers.Ground;

    var rb = actor.AddComponent<Rigidbody2D>();
    rb.IsKinematic = true;              // static: never moved by forces

    actor.AddBox(size);
    scene.AddActor(actor, "background");
    return actor;
}
```

Layer indices are a raycast bitmask over the **integer** `Actor.Layer`:

```csharp
namespace MyGame;

public static class Layers
{
    public const int Default = 0;
    public const int Player  = 1;
    public const int Enemy   = 2;
    public const int Ground  = 3;

    public const int GroundMask = 1 << Ground;
    public const int EnemyMask  = 1 << Enemy;
}
```

`(layerMask & (1 << actor.Layer)) != 0` is the test, so if you never assign
`Actor.Layer`, everything is layer 0 and only `1 << 0` matches.

## 5. CharacterController2D

```csharp
var cc = player.AddComponent<CharacterController2D>();   // auto-adds Rigidbody2D
cc.MoveSpeed           = 320f;
cc.JumpForce           = 780f;
cc.SlopeAngleLimit     = 45f;
cc.GroundCheckDistance = 0.1f;      // a fraction of the collider half-height

cc.IsGrounded;  cc.IsOnSlope;  cc.SlopeAngle;    // read-only
```

Drive it from **`FixedUpdate`** — it sets velocities, which belongs on the
physics clock:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;

namespace MyGame.Components;

public sealed class PlatformerInput : Component
{
    private CharacterController2D _cc = null!;

    public override void Start() => _cc = GetComponent<CharacterController2D>()!;

    public override void FixedUpdate(float dt)
    {
        var input = SBEngine.Instance.Input;
        _cc.Move(input.GetAxis("MoveX"));
        if (input.IsPressed("Jump")) _cc.Jump();
    }
}
```

`Move` takes −1..1 and sets horizontal velocity, preserving vertical.
`Jump` zeroes downward velocity and applies an upward impulse, but **only when
grounded** — which brings us to the interesting part.

## 6. Game feel: coyote time, buffering, variable height

The raw controller is technically correct and feels bad. Three fixes, in
ascending order of importance:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;

namespace MyGame.Components;

/// <summary>
/// Platformer input with the three affordances players expect but never notice:
/// coyote time, jump buffering, and variable jump height.
/// </summary>
public sealed class PlatformerInput : Component
{
    public float CoyoteTime      = 0.10f;   // jump shortly after walking off an edge
    public float JumpBuffer      = 0.12f;   // jump pressed shortly before landing
    public float CutJumpMultiplier = 0.45f; // velocity kept when the button is released early
    public float FallGravityBoost = 1.7f;   // heavier on the way down

    private CharacterController2D _cc = null!;
    private Rigidbody2D           _rb = null!;

    private float _coyote;
    private float _buffer;
    private bool  _jumping;

    public override void Start()
    {
        _cc = GetComponent<CharacterController2D>()!;
        _rb = GetComponent<Rigidbody2D>()!;
    }

    public override void Update(float dt)
    {
        // Buffer the press on the render clock so a single-frame tap is never lost.
        var input = SBEngine.Instance.Input;
        if (input.IsPressed("Jump")) _buffer = JumpBuffer;
        else _buffer -= dt;

        // Releasing the button early cuts the jump short.
        if (input.IsReleased("Jump") && _jumping && _rb.LinearVelocity.Y < 0f)
        {
            _rb.LinearVelocity = new Vector2(_rb.LinearVelocity.X,
                                             _rb.LinearVelocity.Y * CutJumpMultiplier);
            _jumping = false;
        }
    }

    public override void FixedUpdate(float dt)
    {
        var input = SBEngine.Instance.Input;

        _cc.Move(input.GetAxis("MoveX"));

        _coyote = _cc.IsGrounded ? CoyoteTime : _coyote - dt;
        if (_cc.IsGrounded) _jumping = false;

        if (_buffer > 0f && _coyote > 0f)
        {
            // CharacterController2D.Jump requires IsGrounded, so during coyote
            // time we apply the impulse ourselves.
            _rb.LinearVelocity = new Vector2(_rb.LinearVelocity.X, 0f);
            _rb.AddImpulse(new Vector2(0f, -_cc.JumpForce));   // −Y is up on screen

            _buffer  = 0f;
            _coyote  = 0f;
            _jumping = true;

            SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/jump.wav");
        }

        // Fall faster than you rise: the single biggest improvement to jump feel.
        if (_rb.LinearVelocity.Y > 0f)
            _rb.AddForce(new Vector2(0f, PhysicsSystem2D.Instance.Gravity.Y * (FallGravityBoost - 1f) * _rb.Mass));
    }
}
```

Why each one matters:

- **Coyote time** — players press jump a frame or two after the edge. Without
  it, the game reads as unresponsive; with it, nobody notices anything except
  that it feels right. 6–8 frames is the usual window.
- **Jump buffering** — the mirror image: pressing jump just before landing.
- **Asymmetric gravity** — a jump arc that rises slowly and falls fast reads as
  weighty and gives the player more air time to aim. This is the one that turns
  "floaty" into "tight".

Remember that screen Y increases downward, so **negative Y velocity is upward**.

## 7. One-way platforms

Drop through from above, land on from below.

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;

namespace MyGame.Components;

/// <summary>
/// A platform you can jump up through and land on, and drop through by
/// holding Down and pressing Jump.
/// </summary>
public sealed class OneWayPlatform : Component
{
    public string PassengerTag = "Player";
    public float  DropDuration = 0.35f;

    private Collider2D? _collider;
    private float _disabled;

    public override void Start() => _collider = GetComponent<Collider2D>();

    public override void FixedUpdate(float dt)
    {
        if (_disabled > 0f)
        {
            _disabled -= dt;
            if (_disabled <= 0f && _collider != null) _collider.Enabled = true;
            return;
        }

        var scene = Actor.Scene;
        if (scene == null || _collider == null) return;

        foreach (var passenger in scene.FindByTag(PassengerTag))
        {
            var rb = passenger.GetComponent<Rigidbody2D>();
            if (rb == null) continue;

            float platformTop  = Transform.Position.Y - Transform.LocalScale.Y * 0.5f;
            float passengerBottom = passenger.Transform.Position.Y + passenger.Transform.LocalScale.Y * 0.5f;

            // Solid only while the passenger is above and moving downward.
            bool solid = passengerBottom <= platformTop + 4f && rb.LinearVelocity.Y >= 0f;
            _collider.Enabled = solid;

            var input = SBEngine.Instance.Input;
            if (solid && input.GetAxis("MoveY") < -0.5f && input.IsPressed("Jump"))
            {
                _collider.Enabled = false;
                _disabled = DropDuration;
            }
        }
    }
}
```

```csharp
var platform = Solid(scene, new Vector2(700, 470), new Vector2(220, 24));
platform.AddComponent<OneWayPlatform>();
```

## 8. Tilemap collision

Hand-placing colliders stops scaling around level three. `TilemapCollider2D`
generates collision from a `TilemapRenderer` layer, **merging runs of solid
tiles into maximal rectangles** — a 200×200 map becomes a few dozen boxes
instead of 40 000 fixtures.

```csharp
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;

var mapActor = new Actor("Map");
var tiles = mapActor.AddComponent<TilemapRenderer>();
tiles.Map = TilemapData.LoadFromJson("Assets/Maps/level1.json");
tiles.Map.Tileset        = Assets.Load<Texture2D>("Assets/Maps/tileset.png");
tiles.Map.TilesetColumns = 16;
tiles.LayerDepth = 0.9f;

var collider = mapActor.AddComponent<TilemapCollider2D>();
collider.LayerName   = "collision";     // empty string = the first layer
collider.IsSolid     = id => id > 0;    // predicate over tile ids
collider.Friction    = 0.3f;
collider.Restitution = 0f;
collider.MergeTiles  = true;
collider.Rebuild();

Debug.WriteLine($"{collider.FixtureCount} fixtures");
scene.AddActor(mapActor, "background");
```

`Start` calls `Rebuild()` automatically. Call it again after editing tiles —
destructible terrain, a level editor, a procedurally extended map:

```csharp
tiles.Map!.Layers[0].Tiles[y * tiles.Map.Width + x] = 0;
collider.Rebuild();
```

Maps come from [Tiled](https://www.mapeditor.org/) JSON. The importer reads
`width`, `height`, `tilewidth`, `tileheight`, `layers` and `tilesets`, and
handles both array and base64 layer data. Assign `Tileset` and `TilesetColumns`
yourself — it does not resolve image paths.

## 9. Collisions and triggers

Callbacks reach both the `Actor` and every enabled `Component`:

```csharp
public sealed class Hazard : Component
{
    public int Damage = 20;

    public override void OnTriggerEnter(Actor other)
        => other.GetComponent<Health>()?.Damage(Damage, Actor);

    public override void OnCollisionEnter(CollisionData data)
    {
        // data.Other, data.ContactPoint, data.Normal, data.RelativeVelocity
        if (data.RelativeVelocity > 400f)
            SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/impact.wav");
    }
}
```

```csharp
var spikes = CreateBox("Spikes", new Vector2(900, 676), new Vector2(120, 24),
                       new Color(220, 80, 80));
var rb = spikes.AddComponent<Rigidbody2D>();
rb.IsKinematic = true;
spikes.AddBox(new Vector2(120, 24), isTrigger: true);
spikes.AddComponent<Hazard>();
scene.AddActor(spikes);
```

Contacts are reference-counted per actor pair, so `Enter` fires on the first
contact and `Exit` on the last.

## 10. Queries

```csharp
using SexyBiscuit.Engine.Physics;

if (PhysicsSystem2D.Instance.Raycast(
        origin:    Transform.Position,
        direction: Vector2.UnitY,
        distance:  40f,
        out RaycastHit2D hit,
        layerMask: Layers.GroundMask))
{
    Debug.WriteLine($"ground {hit.Distance}px below: {hit.Actor?.Name}");
}

PhysicsSystem2D.Instance.CircleCast(Transform.Position, 120f, Layers.EnemyMask, out Actor[] near);
PhysicsSystem2D.Instance.BoxCast(centre, halfExtents, angle: 0f, Layers.EnemyMask, out Actor[] inBox);
```

A wall-slide check:

```csharp
bool TouchingWall(int direction)
{
    var origin = Transform.Position;
    var dir    = new Vector2(direction, 0f);
    return PhysicsSystem2D.Instance.Raycast(origin, dir, 24f, out _, Layers.GroundMask);
}
```

## 11. Seeing the colliders

```csharp
using SexyBiscuit.Engine.Debug;

public sealed class ColliderGizmo : Component
{
    public Color Colour = Color.Lime;

    public override void Update(float dt)
    {
        if (!Gizmos.Enabled) return;
        var p = Transform.Position;

        if (GetComponent<BoxCollider2D>() is { } box)
            Gizmos.DrawBox2D(p + box.Offset, box.Size, Colour);

        if (GetComponent<CircleCollider2D>() is { } c)
            Gizmos.DrawCircle(new Vector3(p + c.Offset, 0f), c.Radius, 24, Colour);
    }
}
```

Add it to everything while bringing up a level; delete the calls afterwards.
A collider that draws at the wrong size is the `Rebuild()` bug from section 3.

---

## Checkpoint

You have:

- Physics stepping on a correct fixed clock
- `AddBox` / `AddCircle` / `Rebuild` — colliders that are the size you asked for
- A platformer character with coyote time, jump buffering and variable height
- One-way platforms, tilemap collision, hazards, and raycast queries

## Troubleshooting

| Symptom | Cause |
|---|---|
| Nothing falls | `EnablePhysics2D` is false, or no `Rigidbody2D` on the actor |
| Everything falls impossibly slowly | gravity still `(0, 9.8)` at pixel scale |
| Collider is 1×1 | `Size` set after `AddComponent`; call `Rebuild()` |
| Body spawns at the origin | `Rigidbody2D` added before `Transform.Position` was set |
| Player sinks into the floor | ground collider missing a `Rigidbody2D` (kinematic) |
| Fast objects tunnel | lower `FixedTimestep` or thicken static colliders |
| Raycast never hits | `layerMask` does not include the target's `Actor.Layer` |
| `IsGrounded` flickers | ground ray too short — raise `GroundCheckDistance` |

## Exercises

1. Add a wall-slide: when airborne and touching a wall, clamp downward velocity.
2. Add a wall-jump that pushes away from the wall.
3. Add a moving platform (kinematic body, tween the position) and carry the
   player by adding the platform's delta to their position in `LateUpdate`.

---

**Next:** [Tutorial 7 — UI & Menus](07-ui-and-menus.md)
