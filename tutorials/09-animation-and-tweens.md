# Tutorial 9 — Animation & Tweens

**You will build:** sprite animation driven by movement state, a tween library
of reusable game-feel effects, and a hit-reaction that ties them together.
**Time:** ~35 minutes.

Builds on [Tutorial 8](08-audio.md).

---

## 1. Sprite animation

`SpriteAnimator` drives `SpriteRenderer.FrameIndex` from a spritesheet. It
declares `[RequireComponent(typeof(SpriteRenderer))]`, so adding it adds the
renderer too.

Frames are indexed left-to-right, top-to-bottom.

```csharp
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Rendering;

var sprite = player.AddComponent<SpriteRenderer>();
sprite.Texture     = Assets.Load<Texture2D>("Assets/Sprites/hero.png");
sprite.FrameWidth  = 32;
sprite.FrameHeight = 32;
sprite.Pivot       = new Vector2(0.5f, 1.0f);      // feet on the ground

var anim = player.AddComponent<SpriteAnimator>();

anim.AddClip(new AnimationClip
{
    Name = "idle", FrameWidth = 32, FrameHeight = 32,
    StartFrame = 0, EndFrame = 3, Fps = 8f, Loop = true,
});
anim.AddClip(new AnimationClip
{
    Name = "run", FrameWidth = 32, FrameHeight = 32,
    StartFrame = 8, EndFrame = 15, Fps = 14f, Loop = true,
});
anim.AddClip(new AnimationClip
{
    Name = "jump", FrameWidth = 32, FrameHeight = 32,
    StartFrame = 16, EndFrame = 17, Fps = 10f, Loop = false,
});
anim.AddClip(new AnimationClip
{
    Name = "attack", FrameWidth = 32, FrameHeight = 32,
    StartFrame = 24, EndFrame = 29, Fps = 18f, Loop = false,
});

anim.Play("idle");
```

```csharp
anim.Play("run");                    // no-op if "run" is already playing
anim.Play("run", restart: true);     // force a restart
anim.PlayOnce("attack", onComplete: () => anim.Play("idle"));
anim.Stop();  anim.Pause();  anim.Resume();

anim.CurrentClipName;  anim.CurrentFrame;  anim.IsPlaying;
anim.OnClipFinished += name => { };   // non-looping clips only
```

**`Play` throws `KeyNotFoundException`** for an unknown clip name, so guard when
clip names come from data:

```csharp
if (anim.Clips.ContainsKey(name)) anim.Play(name);
```

## 2. Driving animation from state

For a handful of states, a plain `Update` is clearer and easier to debug than a
state-machine asset.

`MyGame/Components/PlayerAnimation.cs`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Components;

public sealed class PlayerAnimation : Component
{
    private SpriteAnimator        _anim = null!;
    private SpriteRenderer        _sprite = null!;
    private Rigidbody2D           _rb = null!;
    private CharacterController2D _cc = null!;

    private bool _locked;      // true while a non-interruptible clip plays

    public override void Start()
    {
        _anim   = GetComponent<SpriteAnimator>()!;
        _sprite = GetComponent<SpriteRenderer>()!;
        _rb     = GetComponent<Rigidbody2D>()!;
        _cc     = GetComponent<CharacterController2D>()!;

        _anim.OnClipFinished += _ => _locked = false;
        _anim.Play("idle");
    }

    public override void Update(float dt)
    {
        // Face the direction of travel.
        float vx = _rb.LinearVelocity.X;
        if (MathF.Abs(vx) > 4f)
            _sprite.Effects = vx < 0f ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

        if (_locked) return;      // do not interrupt an attack

        if (!_cc.IsGrounded)          _anim.Play("jump");
        else if (MathF.Abs(vx) > 12f) _anim.Play("run");
        else                          _anim.Play("idle");
    }

    /// <summary>Plays a clip that must finish before movement animation resumes.</summary>
    public void PlayLocked(string clip)
    {
        _locked = true;
        _anim.Play(clip, restart: true);
    }
}
```

The `_locked` flag is the whole trick: without it, moving during an attack
cancels the attack animation on the next frame.

## 3. AnimatorController

For a genuinely complex or data-driven state graph, `AnimatorController` gives
you parameters and transitions. It requires a `SpriteAnimator`.

```csharp
using SexyBiscuit.Engine.Animation;

var ctrl = player.AddComponent<AnimatorController>();

// Every parameter must be declared before you set it, with the right type.
ctrl.Parameters["Speed"]    = new AnimatorParameter { Name = "Speed",    Type = ParameterType.Float };
ctrl.Parameters["Grounded"] = new AnimatorParameter { Name = "Grounded", Type = ParameterType.Bool, Value = true };
ctrl.Parameters["Attack"]   = new AnimatorParameter { Name = "Attack",   Type = ParameterType.Trigger };

var idle   = new AnimatorState { Name = "Idle",   Clip = "idle" };
var run    = new AnimatorState { Name = "Run",    Clip = "run" };
var jump   = new AnimatorState { Name = "Jump",   Clip = "jump" };
var attack = new AnimatorState { Name = "Attack", Clip = "attack" };

idle.Transitions.Add(new AnimatorTransition
{
    Target = "Run",
    Conditions = { new TransitionCondition { Parameter = "Speed", Op = ConditionOperator.Greater, Value = 12f } },
});
run.Transitions.Add(new AnimatorTransition
{
    Target = "Idle",
    Conditions = { new TransitionCondition { Parameter = "Speed", Op = ConditionOperator.Less, Value = 12f } },
});
attack.Transitions.Add(new AnimatorTransition
{
    Target = "Idle", HasExitTime = true, ExitTime = 1f,     // when the clip completes
});

// A virtual node whose transitions are checked first, from any state.
var any = new AnimatorState { Name = "AnyState", Clip = "" };
any.Transitions.Add(new AnimatorTransition
{
    Target = "Attack",
    Conditions = { new TransitionCondition { Parameter = "Attack", Op = ConditionOperator.TrueValue } },
});

ctrl.States["Idle"]     = idle;
ctrl.States["Run"]      = run;
ctrl.States["Jump"]     = jump;
ctrl.States["Attack"]   = attack;
ctrl.States["AnyState"] = any;
ctrl.AnyStateSource     = "AnyState";     // set before Start() runs
```

```csharp
ctrl.SetFloat("Speed", MathF.Abs(rb.LinearVelocity.X));
ctrl.SetBool("Grounded", cc.IsGrounded);
ctrl.SetTrigger("Attack");
string? state = ctrl.CurrentState;
```

Points that matter:

- Conditions on one transition are **ANDed**. For OR, add two transitions to the
  same target.
- `AnyStateSource` names a state whose transitions are evaluated **first, every
  frame, from wherever you are**. The node itself is never entered.
- **Triggers reset only when a transition fires**, not at end of frame. A
  `SetTrigger` whose conditions are not yet met stays latched and fires later —
  usually what you want for an attack buffered during a landing.
- `ConditionOperator` is `Equals`, `NotEquals`, `Greater`, `Less`, `TrueValue`,
  `FalseValue`.

For four states, the `Update`-based version in section 2 is less code and
easier to debug. Reach for `AnimatorController` when the graph is data-driven.

## 4. Tweens

`Tween` animates anything over time. `SBEngine.Update` calls
`Tween.UpdateAll(Time.DeltaTime)` for you, between `SceneManager.Update` and
`LateUpdate` — so tweens run on **scaled** time and freeze with
`Time.TimeScale = 0`.

```csharp
using SexyBiscuit.Engine.Animation;

Tween.Create().TweenPosition(t, new Vector2(400, 200), 0.6f, EaseType.OutCubic).Play();
Tween.Create().TweenRotation(t, MathF.PI, 0.5f, EaseType.InOutSine).Play();
Tween.Create().TweenScale(t, Vector2.One * 1.4f, 0.15f, EaseType.OutBack).Play();
Tween.Create().TweenColor(spriteRenderer, Color.Red, 0.2f).Play();

Tween.Create()
     .TweenValue(() => _alpha, v => _alpha = v, target: 0f, duration: 1f, EaseType.OutQuad)
     .Play();

Tween.Create()
     .TweenFloat(Audio.Music, nameof(AudioBus.Volume), 0.2f, 1.5f, EaseType.OutQuad)
     .Play();
```

Steps appended to one `Tween` run **in sequence**:

```csharp
Tween.Create()
     .TweenScale(t, Vector2.One * 1.3f, 0.08f, EaseType.OutQuad)
     .TweenScale(t, Vector2.One,        0.22f, EaseType.OutBounce)
     .OnComplete(() => Debug.WriteLine("pop"))
     .Play();
```

```csharp
Tween.Create()
     .TweenPosition(t, up,   0.8f, EaseType.InOutSine)
     .TweenPosition(t, down, 0.8f, EaseType.InOutSine)
     .Loop(-1)                         // -1 = forever
     .Play();

Tween.Create().Delay(0.5f).TweenPosition(t, target, 1f).Play();
```

### Lifetime — the one rule

A tween holds a strong reference to what it animates. Bind it and kill it:

```csharp
var tw = Tween.Create();
tw.BoundActor = actor;
tw.TweenPosition(actor.Transform, target, 1f).Play();
```

```csharp
public override void OnDestroy() => Tween.KillAllFor(Actor);
Tween.KillAll();                     // on a scene change
```

Make `Tween.KillAllFor(Actor)` part of your teardown habit. It is the easiest
leak in the engine to create and the easiest to prevent.

### Sequences

```csharp
Tween.Sequence()
     .Append(Tween.Create().TweenPosition(t, mid, 0.4f, EaseType.OutQuad))
     .Join  (Tween.Create().TweenColor(sr, Color.Yellow, 0.4f))     // runs alongside
     .AppendInterval(0.2f)
     .AppendCallback(() => Sfx.Play("Assets/Audio/land.wav"))
     .Append(Tween.Create().TweenScale(t, Vector2.One, 0.2f, EaseType.OutBounce))
     .OnComplete(() => actor.Destroy())
     .Play();
```

`Append` runs after the previous step; `Join` runs at the same time as it.

### Easing

`EaseType` covers `Linear`, `Spring`, and `In`/`Out`/`InOut` variants of `Quad`,
`Cubic`, `Quart`, `Quint`, `Sine`, `Expo`, `Circ`, `Back`, `Bounce`, `Elastic`.

Practical picks:

| Effect | Ease |
|---|---|
| UI slide, most motion | `OutQuad` |
| Camera moves | `OutCubic` |
| Pop-in, emphasis | `OutBack` |
| Landing, impact | `OutBounce` |
| Looping idle | `InOutSine` |
| Anything "springy" | `OutElastic` |

`Easing.Evaluate(EaseType.OutBack, t)` gives you the raw curve if you want to
apply it yourself.

## 5. A game-feel library

Collect the effects you reuse into one static class.
`MyGame/Systems/Juice.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Systems;

public static class Juice
{
    /// <summary>Squash-and-stretch pop, e.g. on pickup or landing.</summary>
    public static void Pop(Actor actor, float amount = 0.3f, float duration = 0.28f)
    {
        var t    = actor.Transform;
        var rest = t.LocalScale;

        var tw = Tween.Create();
        tw.BoundActor = actor;
        tw.TweenScale(t, rest * (1f + amount), duration * 0.3f, EaseType.OutQuad)
          .TweenScale(t, rest,                 duration * 0.7f, EaseType.OutBounce)
          .Play();
    }

    /// <summary>Landing squash: wide and short, then back.</summary>
    public static void Squash(Actor actor, float amount = 0.25f, float duration = 0.24f)
    {
        var t    = actor.Transform;
        var rest = t.LocalScale;
        var flat = new Vector2(rest.X * (1f + amount), rest.Y * (1f - amount));

        var tw = Tween.Create();
        tw.BoundActor = actor;
        tw.TweenScale(t, flat, duration * 0.35f, EaseType.OutQuad)
          .TweenScale(t, rest, duration * 0.65f, EaseType.OutBack)
          .Play();
    }

    /// <summary>Two-colour hit flash.</summary>
    public static void Flash(Actor actor, Color colour, float duration = 0.14f)
    {
        var sr = actor.GetComponent<SpriteRenderer>();
        if (sr == null) return;
        var rest = sr.Tint;

        var tw = Tween.Create();
        tw.BoundActor = actor;
        tw.TweenColor(sr, colour, duration * 0.3f)
          .TweenColor(sr, rest,   duration * 0.7f)
          .Play();
    }

    /// <summary>Shake the main camera. Trauma decays quadratically for punch.</summary>
    public static void Shake(float intensity = 0.6f, float duration = 0.25f)
    {
        SBEngine.Instance.SceneManager.ActiveScene?
            .FindByName("Main Camera")?
            .GetComponent<Camera2D>()?
            .Shake(intensity, duration);
    }

    /// <summary>Brief slow motion — the classic impact emphasis.</summary>
    public static void HitStop(float scale = 0.05f, float seconds = 0.06f)
    {
        Time.TimeScale = scale;
        SBEngine.Instance.Timers.SetTimer(seconds, () => Time.TimeScale = 1f,
                                          looping: false, useUnscaledTime: true);
    }

    /// <summary>Fade an actor's sprite out, then destroy it.</summary>
    public static void FadeOutAndDestroy(Actor actor, float duration = 0.4f)
    {
        var sr = actor.GetComponent<SpriteRenderer>();
        if (sr == null) { actor.Destroy(); return; }

        var tw = Tween.Create();
        tw.BoundActor = actor;
        tw.TweenColor(sr, sr.Tint * 0f, duration, EaseType.InQuad)
          .OnComplete(actor.Destroy)
          .Play();
    }
}
```

`HitStop` needs `useUnscaledTime: true`, or the timer that restores time never
fires — it is counting on a clock you just stopped. This is the kind of bug that
takes an hour the first time.

## 6. Putting it together

```csharp
using MyGame.Systems;

public sealed class HitReaction : Component
{
    public override void Start()
    {
        var health = GetComponent<Health>();
        if (health == null) return;

        health.Damaged += _ =>
        {
            Juice.Flash(Actor, Color.White, 0.12f);
            Juice.Shake(0.35f, 0.18f);
            Juice.HitStop(0.08f, 0.05f);
            Sfx.Play("Assets/Audio/hit.wav", 0.9f);
        };

        health.Died += () =>
        {
            Juice.Shake(0.8f, 0.35f);
            Juice.FadeOutAndDestroy(Actor, 0.35f);
        };
    }

    public override void OnDestroy() => Tween.KillAllFor(Actor);
}
```

Four lines of effect per event is the whole difference between a game that feels
inert and one that feels good. None of it changes the simulation.

## 7. Floating damage numbers

```csharp
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

public void SpawnDamageNumber(Scene scene, Vector2 worldPos, int amount)
{
    var actor = new Actor($"Dmg{amount}");
    actor.Transform.Position = worldPos;
    actor.LifeSpan = 1.0f;                       // automatic cleanup

    var wc = actor.AddComponent<WorldCanvas>();
    wc.Canvas.Font = Font;
    wc.Offset      = new Vector2(0, -20);

    var label = wc.Canvas.AddWidget<Label>();
    label.Text      = amount.ToString();
    label.TextColor = new Color(255, 210, 120);
    label.Size      = new Vector2(60, 24);
    label.Alignment = TextAlignment.Center;

    scene.AddActor(actor, "foreground");

    float driftX = SBMath.RandomRange(-24f, 24f);

    var tw = Tween.Create();
    tw.BoundActor = actor;
    tw.TweenPosition(actor.Transform, worldPos + new Vector2(driftX, -56f), 0.9f, EaseType.OutCubic)
      .Play();
}
```

`LifeSpan` handles the cleanup, so the tween does not need an `OnComplete`
destroy — and nothing leaks if the tween is killed early.

## 8. Skeletal animation

For 3D, `SkeletalAnimator` samples bone channels into a matrix palette.

```csharp
using SexyBiscuit.Engine.Animation;

var skel = actor.AddComponent<SkeletalAnimator>();
skel.Skeleton = bones;      // List<Bone>
skel.Clips    = clips;      // List<SkeletalClip>

skel.Play("walk", loop: true);
skel.CrossFade("run", blendTime: 0.25f);
skel.Stop();

Matrix[] palette = skel.BonePalette;
effect.Parameters["Bones"]?.SetValue(palette);
```

Pair it with `SkinnedMeshRenderer`, which imports bone indices and weights and
feeds the palette to your shader:

```csharp
using SexyBiscuit.Engine.Rendering;

var skin = actor.AddComponent<SkinnedMeshRenderer>();
if (!skin.LoadModel("Assets/Models/hero.fbx", GraphicsDevice))
    Debug.WriteLine("import failed, or over MaxBones (72)");
skin.Materials.Add(heroMaterial);
```

`Start` resolves the actor's `SkeletalAnimator`, so putting both on the same
actor is all the wiring needed. Your `Material3D.Shader` still has to do the
skinning — `BasicEffect` cannot — reading `Bones`, `BlendIndices` and
`BlendWeights`.

---

## Checkpoint

You have:

- Sprite animation driven by movement, with non-interruptible clips
- A reusable game-feel library: pop, squash, flash, shake, hit-stop, fade-out
- Tween lifetime discipline
- Floating damage numbers combining `LifeSpan`, `WorldCanvas` and tweens

## Exercises

1. Add a landing `Squash` triggered when `IsGrounded` goes false → true.
2. Add a coin that bobs forever with `InOutSine` and `Loop(-1)`.
3. Add a `Juice.Trail` that spawns fading copies of the sprite behind a dash.

---

**Next:** [Tutorial 10 — Scenes & Prefabs](10-scenes-and-prefabs.md)
