# 6. Physics

Namespace: `SexyBiscuit.Engine.Physics`

Two independent engines:

| | 2D | 3D |
|---|---|---|
| Library | Aether.Physics2D 2.1.0 | BepuPhysics 2.4.0 |
| Entry point | `PhysicsSystem2D.Instance` | `PhysicsSystem3D.Instance` |
| Pose source | `Actor.Transform` | `Actor.GetComponent<Transform3D>()` |
| Default gravity | `(0, 9.8)` — Y **down** | `(0, −9.81, 0)` — Y **up** |

> **Both are stepped by `SBEngine`** inside its fixed-timestep loop, gated on
> `Config.EnablePhysics2D` / `Config.EnablePhysics3D` (both default `true`).
> Physics steps **before** `SceneManager.FixedUpdate`, so a force applied in
> `FixedUpdate` is simulated on the *next* step. Turn off the simulation you do
> not use. See [3. The Game Loop](03-game-loop.md).

---

## Units — read this first

`PhysicsSystem2D.FixedStep` copies Aether body positions **straight onto**
`Transform.Position` with no scaling:

```csharp
actor.Transform.Position = PhysicsConvert.ToXna(body.Position);
actor.Transform.Rotation = body.Rotation;
```

So **1 world unit = 1 physics metre**. With the default gravity of 9.8, an actor
at `Position = (400, 300)` is 400 metres from the origin and falls at
9.8 m/s² — visually about one pixel per second if you draw 1 unit = 1 pixel.

You have two workable conventions. Pick one and stay with it.

### Convention A — metre-scale world (recommended for physics games)

Positions are metres. A character is ~1.8 units tall. Scale up at render time
with the camera:

```csharp
camera.Zoom = 64f;                    // 64 pixels per metre
PhysicsSystem2D.Instance.Gravity = new Vector2(0, 9.8f);
```

Colliders then take natural values (`BoxCollider2D.Size = new Vector2(1f, 1.8f)`),
which is what Aether's solver is tuned for. Sprite pivots and `SpriteRenderer`
sizes need scaling to match — set `Transform.LocalScale` to
`1f / pixelsPerUnit` on sprite actors, or author art at 1 unit per sprite.

### Convention B — pixel-scale world

Positions are pixels. Scale gravity to match so falling looks right:

```csharp
PhysicsSystem2D.Instance.Gravity = new Vector2(0, 9.8f * 64f);   // ≈ 627 px/s²
camera.Zoom = 1f;
```

Simpler to wire to sprite art, but Aether's solver tolerances, default
`LinearDamping`, and sleep thresholds are all metre-tuned, so stacked bodies are
jitterier and fast objects tunnel more readily. Compensate with a smaller
`Config.FixedTimestep` (e.g. `1f/120f`) and heavier damping.

3D uses BepuPhysics, which is likewise metre-scale, with gravity
`(0, −9.81, 0)`. 3D scenes should always use Convention A.

---

# 2D Physics

## PhysicsSystem2D

```csharp
PhysicsSystem2D physics = PhysicsSystem2D.Instance;     // process-wide singleton
physics.Gravity = new Vector2(0f, 20f);
World world = physics.World;                            // the raw Aether world
physics.FixedStep(dt);                                  // SBEngine calls this for you
```

`FixedStep` calls `World.Step(dt)` and then writes every registered body's
position and rotation back to its actor's `Transform`.

`Instance` is a static singleton created eagerly and **never reset**. Bodies
survive scene changes unless their components are destroyed — `Rigidbody2D`
removes its own body in `OnDestroy`, so destroying actors properly is what keeps
the world clean.

## Rigidbody2D

```csharp
var rb = actor.AddComponent<Rigidbody2D>();
rb.Mass            = 1f;
rb.GravityScale    = 1f;
rb.LinearVelocity  = new Vector2(0f, 0f);
rb.AngularVelocity = 0f;
rb.LinearDamping   = 0.1f;
rb.AngularDamping  = 0.1f;
rb.FreezeRotation  = true;      // top-down characters usually want this
rb.IsKinematic     = false;     // true → moved by code, unaffected by forces
```

Forces:

```csharp
rb.AddForce(new Vector2(0, -500));                    // continuous, mass-dependent
rb.AddForceAtPoint(force, worldPoint);                // applies torque too
rb.AddImpulse(new Vector2(0, -8));                    // instantaneous velocity change
rb.AddTorque(2f);
```

`Body? Body => _body` exposes the raw Aether body if you need something the
wrapper does not cover.

> **`Rigidbody2D` creates its Aether body in `Awake`**, which runs the instant
> you call `AddComponent`. Set the actor's `Transform.Position` **before**
> adding the rigidbody, or the body starts at the old position.

```csharp
var actor = new Actor("Crate");
actor.Transform.Position = new Vector2(4, 2);      // FIRST
var rb = actor.AddComponent<Rigidbody2D>();        // body created here, at (4,2)
```

## Collider2D

Three shapes, all deriving from `Collider2D`:

```csharp
var box = actor.AddComponent<BoxCollider2D>();
box.Size   = new Vector2(1f, 1.8f);
box.Offset = Vector2.Zero;

var circle = actor.AddComponent<CircleCollider2D>();
circle.Radius = 0.5f;
circle.Offset = Vector2.Zero;

var poly = actor.AddComponent<PolygonCollider2D>();
poly.Points = new[]
{
    new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0f, 0.5f),
};
```

Shared:

```csharp
collider.IsTrigger = true;                  // a sensor: reports overlaps, no response
collider.Material  = new PhysicsMaterial2D
{
    Friction    = 0.3f,
    Restitution = 0.0f,      // bounciness
    Density     = 1.0f,
};
```

A collider needs a body, and `Collider2D.Awake` adds a `Rigidbody2D` for you if
the actor has none — so either order works:

```csharp
actor.Transform.Position = spawn;
actor.AddComponent<Rigidbody2D>();        // optional: Collider2D.Awake would add it
actor.AddComponent<BoxCollider2D>();
```

### The fixture is built at `Awake` — Verified

This trips up everybody once. `Collider2D.Awake` calls `CreateFixture(body)`
**immediately**, using the collider's properties as they are at that moment.
`AddComponent` runs `Awake` synchronously, so this does nothing:

```csharp
var box = actor.AddComponent<BoxCollider2D>();   // fixture built here, Size = (1,1)
box.Size = new Vector2(1f, 1.8f);                // ← too late, purely cosmetic
```

The collision shape stays a 1×1 box. The same applies to
`CircleCollider2D.Radius`, `PolygonCollider2D.Points`, `Offset`, `IsTrigger`,
and `Material`.

Rebuild the fixture after configuring it. Both methods are `public override`, so
this is legal and it is the cleanest fix available today:

```csharp
public static class ColliderExtensions
{
    /// Destroys and re-creates the fixture so shape changes take effect.
    public static T Rebuild<T>(this T collider) where T : Collider2D
    {
        collider.OnDestroy();     // removes the stale fixture from the body
        collider.Awake();         // re-creates it from the current properties
        return collider;
    }
}
```

```csharp
var box = actor.AddComponent<BoxCollider2D>();
box.Size      = new Vector2(1f, 1.8f);
box.IsTrigger = false;
box.Material  = new PhysicsMaterial2D { Friction = 0.1f };
box.Rebuild();                                   // now the fixture matches
```

Wrap it in a factory so you never forget:

```csharp
static BoxCollider2D AddBox(Actor a, Vector2 size, bool trigger = false)
{
    var c = a.AddComponent<BoxCollider2D>();
    c.Size = size;
    c.IsTrigger = trigger;
    return c.Rebuild();
}
```

For static geometry, add a `Rigidbody2D` with `IsKinematic = true` and never move
it, or create the body directly:

```csharp
var body = PhysicsSystem2D.Instance.CreateBody(actor, BodyType.Static);
```

## Collision callbacks

`PhysicsSystem2D` subscribes to Aether's `BeginContact` / `EndContact` /
`PreSolve` and dispatches to actors and their components. Contacts are
reference-counted per actor pair, so `Enter` fires on the first contact and
`Exit` on the last.

```csharp
public sealed class Damageable : Component
{
    public override void OnCollisionEnter(CollisionData data)
    {
        if (data.Other.Tag != "Bullet") return;
        if (data.RelativeVelocity > 5f) ApplyDamage();
    }

    public override void OnTriggerEnter(Actor other)
    {
        if (other.Tag == "Pickup") other.Destroy();
    }
}
```

`CollisionData` gives you `Other`, `ContactPoint`, `Normal` and
`RelativeVelocity`. Trigger callbacks receive only the other `Actor`.

Available on both `Actor` (as `public virtual`) and `Component` (as
`public virtual`). The `Actor` base implementations forward to components, so
**call `base` when you override on an `Actor`**.

## Queries

```csharp
// Closest hit along a ray. layerMask is a bitmask over Actor.Layer; -1 = everything.
if (PhysicsSystem2D.Instance.Raycast(
        origin: transform.Position,
        direction: Vector2.UnitY,
        distance: 2f,
        out RaycastHit2D hit,
        layerMask: -1))
{
    Debug.WriteLine($"{hit.Actor?.Name} at {hit.Point}, normal {hit.Normal}, {hit.Distance}u away");
}

// Overlap queries — return every actor whose fixture overlaps the shape.
PhysicsSystem2D.Instance.CircleCast(centre, radius: 3f, layerMask: -1, out Actor[] inRange);
PhysicsSystem2D.Instance.BoxCast(centre, halfExtents, angle: 0f, layerMask: -1, out Actor[] inBox);
```

`layerMask` tests `(layerMask & (1 << actor.Layer)) != 0`, using the **integer**
`Actor.Layer` — not the `Layer` object the actor lives in. Assign layer indices
deliberately:

```csharp
static class Layers
{
    public const int Default    = 0;
    public const int Player     = 1;
    public const int Enemy      = 2;
    public const int Ground     = 3;
    public const int GroundMask = 1 << Ground;
    public const int HostileMask = (1 << Enemy);
}

player.Layer = Layers.Player;
ground.Layer = Layers.Ground;

physics.Raycast(pos, Vector2.UnitY, 0.2f, out var hit, Layers.GroundMask);
```

## CharacterController2D

A kinematic-ish character mover built on `Rigidbody2D` (auto-added via
`[RequireComponent]`).

```csharp
var cc = actor.AddComponent<CharacterController2D>();   // adds Rigidbody2D
cc.MoveSpeed           = 300f;
cc.JumpForce           = 700f;
cc.SlopeAngleLimit     = 45f;
cc.GroundCheckDistance = 0.1f;
```

Drive it from `FixedUpdate`:

```csharp
public override void FixedUpdate(float dt)
{
    var input = SBEngine.Instance.Input;
    _cc.Move(input.GetAxis("MoveX"));           // −1..1
    if (input.IsPressed("Jump") && _cc.IsGrounded)
        _cc.Jump();
}
```

Read-only state: `IsGrounded`, `IsOnSlope`, `SlopeAngle`. Grounding uses a
downward raycast of `GroundCheckDistance`, so those defaults are in **metres**
— scale them if you use pixel-scale physics.

## Building collision from a tilemap

There is no automatic tilemap collider. Walk a collision layer once at load
time and emit static boxes:

```csharp
void BuildTileColliders(Scene scene, TilemapData map, string layerName)
{
    var layer = map.Layers.First(l => l.Name == layerName);

    for (int y = 0; y < map.Height; y++)
    for (int x = 0; x < map.Width;  x++)
    {
        if (layer.Tiles[y * map.Width + x] == 0) continue;

        var tile = new Actor($"Tile_{x}_{y}") { Tag = "Ground", Layer = Layers.Ground };
        tile.Transform.Position = new Vector2(
            (x + 0.5f) * map.TileWidth,
            (y + 0.5f) * map.TileHeight);

        var rb = tile.AddComponent<Rigidbody2D>();
        rb.IsKinematic = true;

        var col = tile.AddComponent<BoxCollider2D>();
        col.Size = new Vector2(map.TileWidth, map.TileHeight);

        scene.AddActor(tile, "background");
    }
}
```

For large maps, merge runs of adjacent solid tiles into single wide boxes before
creating actors — one body per tile is expensive.

---

# 3D Physics

## PhysicsSystem3D

A BepuPhysics `Simulation` wrapper. Gravity is baked into the pose-integrator
callbacks at construction as `(0, −9.81, 0)`.

```csharp
PhysicsSystem3D.Instance.FixedStep(dt);            // SBEngine calls this for you
Simulation sim = PhysicsSystem3D.Instance.Simulation;   // raw Bepu handle
```

`FixedStep` advances the simulation and writes body poses back onto each actor's
`Transform3D`.

## Rigidbody3D

```csharp
var rb = actor.AddComponent<Rigidbody3D>();
rb.Mass            = 70f;
rb.LinearVelocity  = Vector3.Zero;
rb.AngularVelocity = Vector3.Zero;
rb.LinearDamping   = 0.05f;
rb.AngularDamping  = 0.05f;
rb.IsKinematic     = false;

rb.AddForce(new Vector3(0, 500, 0));
rb.AddImpulse(new Vector3(0, 6, 0));
rb.AddTorque(new Vector3(0, 2, 0));

BodyHandle handle = rb.Handle;    // valid when rb.HasHandle
```

In `Awake`, `Rigidbody3D` looks for a `Collider3D` on the actor and calls
`RegisterShape`. **If there is no collider it falls back to a sphere of radius
0.5** — so a body with no collider still simulates, as a ball.

## Collider3D

```csharp
var box = actor.AddComponent<BoxCollider3D>();
box.HalfExtents = new Vector3(0.5f, 0.9f, 0.5f);

var sphere = actor.AddComponent<SphereCollider3D>();
sphere.Radius = 0.5f;

var capsule = actor.AddComponent<CapsuleCollider3D>();
capsule.Radius = 0.4f;
capsule.Length = 1.2f;      // cylindrical section, excluding the caps
```

Shared: `IsTrigger`, `Friction` (default 0.5), `Restitution` (default 0).

`Collider3D.Awake` adds a `Rigidbody3D` if the actor has none, and
`Rigidbody3D.Awake` calls `collider.RegisterShape(...)`. So adding just the
collider is enough — but the **same "shape is built at `Awake`" problem as 2D
applies**: `Radius`, `Length` and `HalfExtents` are read at registration time,
before you get a chance to set them.

Rebuild by removing the body and re-running `Awake` on the rigidbody:

```csharp
var t3d = actor.AddComponent<Transform3D>();
t3d.Position = spawn;

var col = actor.AddComponent<CapsuleCollider3D>();   // registers a 0.5 × 1 capsule
col.Radius = 0.4f;
col.Length = 1.2f;

var rb = actor.GetComponent<Rigidbody3D>()!;          // added by the collider
PhysicsSystem3D.Instance.RemoveBody(actor);           // drop the stale body
rb.Awake();                                           // re-register with the new shape
rb.Mass = 70f;
```

Set shape properties before the rebuild, and body properties (`Mass`,
`LinearDamping`, velocities) after — those are cached and re-applied by
`ApplyCachedValues`.

## Static geometry

```csharp
PhysicsSystem3D.Instance.AddStaticBox(
    position: new Vector3(0, -0.5f, 0),
    rotation: Quaternion.Identity,
    halfExtents: new Vector3(50f, 0.5f, 50f));

PhysicsSystem3D.Instance.AddStaticMesh(vertices, indices, position);
```

## Direct body creation

```csharp
BodyHandle h1 = PhysicsSystem3D.Instance.AddBox(actor, halfExtents, mass);
BodyHandle h2 = PhysicsSystem3D.Instance.AddSphere(actor, radius, mass);
BodyHandle h3 = PhysicsSystem3D.Instance.AddCapsule(actor, radius, length, mass);
PhysicsSystem3D.Instance.RemoveBody(actor);
PhysicsSystem3D.Instance.TryGetHandle(actor, out var handle);
```

## Raycasting

```csharp
if (PhysicsSystem3D.Instance.Raycast(origin, direction, 100f, out RaycastHit3D hit, layerMask: -1))
{
    Debug.WriteLine($"{hit.Actor?.Name} at {hit.Point} ({hit.Distance}m)");
}
```

Combined with `Camera3D.ScreenToWorldRay`, this is your mouse picker.

## CharacterController3D

```csharp
var cc = actor.AddComponent<CharacterController3D>();   // adds Rigidbody3D
cc.MoveSpeed    = 5f;
cc.JumpSpeed    = 8f;
cc.StepUpHeight = 0.3f;
cc.SlopeLimit   = 45f;
cc.SnapDistance = 0.1f;
```

```csharp
public override void Update(float dt)
{
    var input = SBEngine.Instance.Input;
    var move = new Vector3(input.GetAxis("MoveX"), 0f, input.GetAxis("MoveY")) * cc.MoveSpeed;
    cc.Move(move);
    if (input.IsPressed("Jump") && cc.IsGrounded) cc.Jump();
}
```

`Move` takes a **velocity**, not a displacement — do not multiply by `dt`.
Read-only: `IsGrounded`, `IsOnSlope`, `SlopeAngle`. Ground detection and step-up
use downward raycasts against the Bepu simulation.

---

## Debugging physics

Draw collider outlines with [Gizmos](17-debugging.md):

```csharp
public override void Update(float dt)
{
    if (GetComponent<BoxCollider2D>() is { } box)
        Gizmos.DrawBox2D(Transform.Position + box.Offset, box.Size, Color.Lime);

    if (GetComponent<CircleCollider2D>() is { } circle)
        Gizmos.DrawCircle(new Vector3(Transform.Position, 0f), circle.Radius, 32, Color.Cyan);
}
```

Remember to pump `Gizmos.Update(dt)` and `Gizmos.Flush(sb, gd)`.

---

## Common problems

| Symptom | Cause |
|---|---|
| Nothing falls | `Config.EnablePhysics2D` is false, or the actor has no `Rigidbody2D`. |
| Everything falls impossibly slowly | Pixel-scale positions with metre-scale gravity. Pick a convention. |
| Body spawns at the origin | `Rigidbody2D` was added before `Transform.Position` was set. |
| Collider is the wrong size | Shape properties were set after `AddComponent`. Call `Rebuild()`. |
| Body simulates as a ball (3D) | No `Collider3D` when `Rigidbody3D.Awake` ran — the sphere fallback. |
| Fast objects pass through walls | Lower `Config.FixedTimestep`, or thicken static colliders. |
| Raycast never hits | `layerMask` does not include the target's `Actor.Layer` integer. |
| Character sinks into slopes | `SlopeAngleLimit` / `SlopeLimit` too low, or ground-check distance in the wrong units. |

---

## Next

- [7. Input](07-input.md)
- [Tutorial 6: A Physics Platformer](../tutorials/06-physics-platformer.md)
