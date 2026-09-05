# Tutorial 4 — Writing Components

**You will build:** health and damage, pickups, a spawner, and the habits that
keep a component-based codebase from turning to soup. **Time:** ~30 minutes.

Builds on [Tutorial 3](03-input-and-movement.md).

---

## 1. The lifecycle, precisely

```csharp
public abstract class Component
{
    public Actor Actor   { get; }
    public bool  Enabled { get; set; } = true;

    public virtual void Awake();              // ← during AddComponent, synchronously
    public virtual void Start();              // ← first update after AddActor
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

### `Awake` runs inside `AddComponent`

This is the single most important fact about components in this engine:

```csharp
private void AttachComponent(Component c)
{
    c.Actor = this;
    _components.Add(c);
    c.Awake();                  // ← immediately
    if (_started) c.Start();
}
```

So this does nothing useful:

```csharp
var body = actor.AddComponent<Rigidbody2D>();   // Awake already created the body
actor.Transform.Position = spawn;               // …at the old position
```

**The rule:** configure the actor *before* adding components that read that
configuration, and put your own initialisation in `Start`, not `Awake`.

### `Start` runs one frame later

`Layer.AddActor` queues the actor. `FlushPending()` at the start of the layer's
next `Update` calls `InternalStart`, which runs `Actor.OnStart` and then every
component's `Start`. So `GetComponent` calls that need siblings belong in
`Start` — by then every component the constructor added exists.

```csharp
public override void Start()
{
    _sprite = GetComponent<SpriteRenderer>();     // safe: all siblings attached
    _health = GetComponent<Health>();
}
```

### Which update?

| Callback | Use it for |
|---|---|
| `Update` | gameplay logic, input, timers |
| `FixedUpdate` | anything that must be framerate-independent — physics forces |
| `LateUpdate` | anything that must read the *final* positions — cameras, attachments |
| `Draw` | custom rendering; runs inside the scene's `SpriteBatch` |

## 2. Health and damage

`MyGame/Components/Health.cs`:

```csharp
using SexyBiscuit.Engine.Core;

namespace MyGame.Components;

public sealed class Health : Component
{
    public int  Max          = 100;
    public bool DestroyOnDeath = true;
    public float InvulnerableAfterHit = 0.4f;

    public int  Current   { get; private set; }
    public bool IsDead    => Current <= 0;
    public bool IsInvulnerable => _invulnerable > 0f;

    public event Action<int, int>? Changed;   // (current, max)
    public event Action<Actor?>?   Damaged;   // instigator
    public event Action?           Died;

    private float _invulnerable;

    public override void Awake() => Current = Max;

    public override void Update(float dt)
    {
        if (_invulnerable > 0f) _invulnerable -= dt;
    }

    public void Damage(int amount, Actor? instigator = null)
    {
        if (IsDead || IsInvulnerable || amount <= 0) return;

        Current = Math.Max(0, Current - amount);
        _invulnerable = InvulnerableAfterHit;

        Changed?.Invoke(Current, Max);
        Damaged?.Invoke(instigator);

        if (!IsDead) return;

        Died?.Invoke();
        if (DestroyOnDeath) Actor.Destroy();
    }

    public void Heal(int amount)
    {
        if (IsDead || amount <= 0) return;
        Current = Math.Min(Max, Current + amount);
        Changed?.Invoke(Current, Max);
    }

    public void Reset()
    {
        Current = Max;
        _invulnerable = 0f;
        Changed?.Invoke(Current, Max);
    }
}
```

`Awake` is the right place for `Current = Max` here: it depends on nothing else,
and it means `Current` is valid the instant the component exists.

Note the **invulnerability window**. Without it, a player standing in a damage
trigger loses a hit point per frame. Almost every action game needs this, and
it belongs in `Health` rather than in every damage source.

### Reacting to damage

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Components;

/// <summary>Flashes the sprite red when the actor takes damage.</summary>
public sealed class DamageFlash : Component
{
    public Color FlashColour = Color.Red;
    public float Duration    = 0.12f;

    private SpriteRenderer? _sprite;
    private Color _base;
    private Health? _health;

    public override void Start()
    {
        _sprite = GetComponent<SpriteRenderer>();
        _health = GetComponent<Health>();
        if (_sprite != null) _base = _sprite.Tint;
        if (_health != null) _health.Damaged += OnDamaged;
    }

    private void OnDamaged(Actor? _)
    {
        if (_sprite == null) return;

        var tween = Tween.Create();
        tween.BoundActor = Actor;                       // dies with the actor
        tween.TweenColor(_sprite, FlashColour, Duration * 0.3f)
             .TweenColor(_sprite, _base,       Duration * 0.7f)
             .Play();
    }

    public override void OnDestroy()
    {
        if (_health != null) _health.Damaged -= OnDamaged;   // always unsubscribe
        Tween.KillAllFor(Actor);
    }
}
```

Two habits on display, both worth adopting permanently:

- **Unsubscribe in `OnDestroy`.** A C# `event` holds a strong reference; a
  subscriber that outlives its publisher is a leak.
- **Bind and kill tweens.** `tween.BoundActor = Actor` plus
  `Tween.KillAllFor(Actor)` stops a tween writing into a destroyed actor.

## 3. Triggers and pickups

Collision callbacks arrive on both the `Actor` and every enabled `Component`.
`Actor`'s base implementations forward to components — **so if you override one
on an `Actor` subclass, call `base`.**

```csharp
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;

namespace MyGame.Components;

public sealed class HealthPickup : Component
{
    public int    Amount    = 25;
    public string TargetTag = "Player";
    public string? PickupSound = "Assets/Audio/pickup.wav";

    public override void OnTriggerEnter(Actor other)
    {
        if (other.Tag != TargetTag) return;

        var health = other.GetComponent<Health>();
        if (health == null || health.Current >= health.Max) return;

        health.Heal(Amount);

        if (PickupSound != null)
            SBEngine.Instance.Audio.PlayOneShot(PickupSound);

        Actor.Destroy();
    }
}
```

Trigger callbacks come from the physics system, so the actor needs a collider
with `IsTrigger = true` and a body. Physics is [Tutorial 6](06-physics-platformer.md);
for now, a proximity check works and needs nothing:

```csharp
public sealed class ProximityPickup : Component
{
    public float  Radius    = 32f;
    public string TargetTag = "Player";

    private Actor? _target;

    public override void Start()
        => _target = Actor.Scene?.FindByTag(TargetTag).FirstOrDefault();

    public override void Update(float dt)
    {
        if (_target == null || _target.IsDestroyed) return;
        if (Vector2.Distance(Transform.Position, _target.Transform.Position) > Radius) return;

        GetComponent<HealthPickup>()?.OnTriggerEnter(_target);
    }
}
```

Caching the target in `Start` matters: `FindByTag` is a linear scan across every
layer, so calling it per frame per pickup is quadratic work for nothing.

## 4. Spawning

`Actor.LifeSpan` gives short-lived actors automatic cleanup with no timer
component:

```csharp
var puff = CreateBox("Puff", position, new Vector2(12, 12), Color.White);
puff.LifeSpan = 0.4f;         // destroys itself, on scaled time
scene.AddActor(puff, "foreground");

impact.Destroy(delaySeconds: 0.8f);   // the same thing, imperatively
```

A spawner, using the engine's `TimerManager`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;

namespace MyGame.Components;

public sealed class Spawner : Component
{
    public Func<Actor>? Factory;
    public float  Interval   = 2f;
    public int    MaxAlive   = 12;
    public float  Radius     = 320f;
    public string SpawnLayer = "default";

    private readonly List<Actor> _alive = new();
    private TimerHandle _timer;

    public override void Start()
    {
        _timer = SBEngine.Instance.Timers.SetTimer(Interval, SpawnOne, looping: true);
    }

    public override void OnDestroy()
        => SBEngine.Instance.Timers.Clear(_timer);

    private void SpawnOne()
    {
        _alive.RemoveAll(a => a.IsDestroyed);
        if (Factory == null || _alive.Count >= MaxAlive) return;

        var scene = Actor.Scene;
        if (scene == null) return;

        double angle = SBMath.Random.NextDouble() * MathHelper.TwoPi;
        var offset = new Vector2(MathF.Cos((float)angle), MathF.Sin((float)angle)) * Radius;

        var spawned = Factory();
        spawned.Transform.Position = Transform.Position + offset;
        scene.AddActor(spawned, SpawnLayer);
        _alive.Add(spawned);
    }
}
```

```csharp
var spawner = new Actor("Spawner");
spawner.Transform.Position = new Vector2(640, 360);
var s = spawner.AddComponent<Spawner>();
s.Factory  = () =>
{
    var e = CreateBox("Enemy", Vector2.Zero, new Vector2(32, 32), new Color(230, 90, 90));
    e.Tag = "Enemy";
    e.AddComponent<Health>().Max = 30;
    return e;
};
s.Interval = 1.5f;
scene.AddActor(spawner);
```

`Timers.Clear` in `OnDestroy` is not optional — a looping timer holds a delegate
that closes over `this`, so an uncleared timer keeps the whole component alive
and keeps spawning into a scene that no longer exists.

## 5. `[RequireComponent]`

```csharp
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

[RequireComponent(typeof(SpriteRenderer))]
[RequireComponent(typeof(Health))]
public sealed class Enemy : Component
{
    public override void Start()
    {
        var sprite = GetComponent<SpriteRenderer>()!;   // guaranteed by the attribute
        var health = GetComponent<Health>()!;
        health.Died += () => Score.Add(10);
    }
}
```

`AddComponent<T>` walks `[RequireComponent]` first and adds anything missing, so
the dependency exists before `Enemy.Awake` runs. It is `AllowMultiple`, so
declare as many as you need.

## 6. Actor subclass or component?

Both work. The rule of thumb:

| Use a **component** when | Use an **Actor subclass** when |
|---|---|
| the behaviour is reusable across different objects | the object is a distinct *kind* of thing |
| you want to mix and match | you want a constructor that assembles a fixed set of components |
| something else needs to `GetComponent` it | you want typed access via `FindActorsOfType<T>` |

The demo project uses actor subclasses for its main characters:

```csharp
public class PlayerActor : Actor
{
    private CharacterController3D _controller = null!;

    public PlayerActor() : base("Player")
    {
        Tag = "Player";
        _controller = AddComponent<CharacterController3D>();   // auto-adds Rigidbody3D
    }

    protected override void OnStart() { }           // note: OnStart, not Start
    protected override void Update(float dt) { }
}
```

Two naming traps:

- `Actor` uses `protected override void OnStart()`. `Component` uses
  `public override void Start()`. The wrong one compiles and never runs.
- `Actor`'s collision callbacks are `public virtual` and forward to components.
  Call `base` when you override them.

A hybrid works well: an actor subclass that assembles components in its
constructor and holds no logic of its own.

## 7. Coroutines

For anything that unfolds over time, a coroutine reads better than a state
machine of floats.

```csharp
using System.Collections;
using SexyBiscuit.Engine.Core;

public sealed class BossIntro : Component
{
    public override void Start() => Actor.StartCoroutine(Intro());

    private IEnumerator Intro()
    {
        yield return new WaitForSeconds(1f);
        Roar();
        yield return new WaitUntil(() => !RoarPlaying);
        yield return new WaitForSeconds(0.5f);
        BeginPhaseOne();
    }
}
```

`Actor.StartCoroutine` registers the actor as the owner, and `InternalDestroy`
cancels every coroutine that actor started — so a coroutine can never outlive
its target. Yield instructions: `WaitForSeconds`,
`WaitForSecondsRealtime` (ignores `Time.TimeScale`), `WaitUntil`, `WaitWhile`.

## 8. Component design habits

**Public fields for tuning, properties for computed state.** Fields are what a
designer or a future you will edit; properties express what the component knows.

**Cache sibling lookups in `Start`.** `GetComponent` is a linear scan.

**Guard against nulls.** `GetComponent<T>()` returns null when absent — the
`?.` and `is { }` patterns keep the code short:

```csharp
if (GetComponent<Health>() is { } health) health.Damage(10);
```

**Prefer events to polling.** `health.Died += …` beats checking `IsDead` in
every interested component's `Update`.

**Unsubscribe and clean up in `OnDestroy`.** Events, timers, coroutines and
tweens all outlive their actor otherwise.

**One responsibility per component.** `Health`, `DamageFlash` and `HealthBar`
are three components, not one — and any of them can be dropped on an object
that does not want the others.

---

## Checkpoint

You have:

- `Health` with events and an invulnerability window
- `DamageFlash`, wired through events and tweens
- Pickups, a spawner, and automatic cleanup with `LifeSpan`
- A working sense of `Awake` vs `Start`, and component vs actor subclass

## Exercises

1. Write `Lifetime` — destroy after N seconds *and* fade the sprite out over the
   last half second.
2. Write `FollowTarget` — move toward a tagged actor at a fixed speed, using
   `SBMath.Damp` for smoothing.
3. Give `Health` a `Shield` that absorbs damage before `Current` drops.

---

**Next:** [Tutorial 5 — JavaScript Scripting](05-javascript-scripting.md)
