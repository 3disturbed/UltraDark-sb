# 10. Animation

Namespace: `SexyBiscuit.Engine.Animation`

Four independent systems:

| System | Drives | Pumped by |
|---|---|---|
| `SpriteAnimator` | `SpriteRenderer.FrameIndex` | the scene graph ✅ |
| `AnimatorController` | `SpriteAnimator` clip selection | the scene graph ✅ |
| `SkeletalAnimator` | a `Matrix[]` bone palette | the scene graph ✅ |
| `Tween` / `TweenSequence` | any float, transform, or colour | the engine ✅ (`Tween.UpdateAll`) |

---

## SpriteAnimator

Plays frame ranges out of a spritesheet by driving `SpriteRenderer.FrameIndex`.
Declared `[RequireComponent(typeof(SpriteRenderer))]`, so adding it auto-adds the
renderer.

```csharp
var sprite = actor.AddComponent<SpriteRenderer>();
sprite.Texture     = Assets.Load<Texture2D>("Assets/Sprites/hero.png");
sprite.FrameWidth  = 32;
sprite.FrameHeight = 32;

var anim = actor.AddComponent<SpriteAnimator>();
anim.AddClip(new AnimationClip
{
    Name        = "idle",
    FrameWidth  = 32,
    FrameHeight = 32,
    StartFrame  = 0,
    EndFrame    = 3,
    Fps         = 8f,
    Loop        = true,
});
anim.AddClip(new AnimationClip
{
    Name = "run", FrameWidth = 32, FrameHeight = 32,
    StartFrame = 8, EndFrame = 15, Fps = 12f, Loop = true,
});
anim.AddClip(new AnimationClip
{
    Name = "attack", FrameWidth = 32, FrameHeight = 32,
    StartFrame = 16, EndFrame = 21, Fps = 16f, Loop = false,
});

anim.Play("idle");
```

Frames are indexed left-to-right, top-to-bottom across the sheet.

### Playback

```csharp
anim.Play("run");                    // no-op if already playing "run"
anim.Play("run", restart: true);     // force a restart from StartFrame
anim.PlayOnce("attack", onComplete: () => anim.Play("idle"));
anim.Stop();
anim.Pause();
anim.Resume();

string? clipName = anim.CurrentClipName;
AnimationClip? clip = anim.CurrentClip;
int frame  = anim.CurrentFrame;
bool going = anim.IsPlaying;

anim.OnClipFinished += name => { /* fires for non-looping clips */ };
```

`Play` **throws `KeyNotFoundException`** for an unknown clip name. Guard with
`anim.Clips.ContainsKey(name)` when clip names come from data.

`OnClipFinished` fires when a `Loop = false` clip reaches `EndFrame`. Looping
clips never fire it.

### A typical state driver

```csharp
public sealed class HeroAnimation : Component
{
    private SpriteAnimator  _anim = null!;
    private Rigidbody2D     _rb   = null!;
    private SpriteRenderer  _sr   = null!;

    public override void Start()
    {
        _anim = GetComponent<SpriteAnimator>()!;
        _rb   = GetComponent<Rigidbody2D>()!;
        _sr   = GetComponent<SpriteRenderer>()!;
        _anim.Play("idle");
    }

    public override void Update(float dt)
    {
        if (_anim.CurrentClipName == "attack" && _anim.IsPlaying) return;   // don't interrupt

        float vx = _rb.LinearVelocity.X;
        _anim.Play(MathF.Abs(vx) > 0.1f ? "run" : "idle");

        if (MathF.Abs(vx) > 0.01f)
            _sr.Effects = vx < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
    }
}
```

---

## AnimatorController

A parameter-driven state machine layered on top of `SpriteAnimator`. Declared
`[RequireComponent(typeof(SpriteAnimator))]`.

```csharp
var ctrl = actor.AddComponent<AnimatorController>();

ctrl.Parameters["Speed"]     = new AnimatorParameter { Name = "Speed",     Type = ParameterType.Float, Value = 0f };
ctrl.Parameters["Grounded"]  = new AnimatorParameter { Name = "Grounded",  Type = ParameterType.Bool,  Value = true };
ctrl.Parameters["AttackTrig"]= new AnimatorParameter { Name = "AttackTrig",Type = ParameterType.Trigger, Value = false };

var idle = new AnimatorState { Name = "Idle", Clip = "idle" };
var run  = new AnimatorState { Name = "Run",  Clip = "run"  };
var atk  = new AnimatorState { Name = "Attack", Clip = "attack" };

idle.Transitions.Add(new AnimatorTransition
{
    Target = "Run",
    Conditions = { new TransitionCondition { Parameter = "Speed", Op = ConditionOperator.Greater, Value = 0.1f } },
});
run.Transitions.Add(new AnimatorTransition
{
    Target = "Idle",
    Conditions = { new TransitionCondition { Parameter = "Speed", Op = ConditionOperator.Less, Value = 0.1f } },
});
atk.Transitions.Add(new AnimatorTransition
{
    Target = "Idle",
    HasExitTime = true,
    ExitTime    = 1f,        // fraction of the clip
});

ctrl.States["Idle"]   = idle;
ctrl.States["Run"]    = run;
ctrl.States["Attack"] = atk;
```

### `AnyStateSource` — a virtual interrupt node

```csharp
var any = new AnimatorState { Name = "AnyState", Clip = "" };
any.Transitions.Add(new AnimatorTransition
{
    Target = "Attack",
    Conditions = { new TransitionCondition { Parameter = "AttackTrig", Op = ConditionOperator.TrueValue } },
});
ctrl.States["AnyState"] = any;
ctrl.AnyStateSource     = "AnyState";      // set before Start() runs
```

`AnyStateSource` names a state whose **transitions are evaluated first, every
frame, from whatever state you are in** — Unity's AnyState. The node itself is
never entered: `Start` skips it when choosing the initial state, which is
otherwise "the first entry in `States`" (dictionary insertion order).

### Parameters

Every parameter must be **declared in `Parameters` before you set it**, with the
matching `ParameterType` — the setters throw otherwise. `ConditionOperator` is:

| Operator | Meaning |
|---|---|
| `Equals` / `NotEquals` | value comparison |
| `Greater` / `Less` | numeric comparison |
| `TrueValue` / `FalseValue` | bool / trigger must be true / false |

Set parameters from gameplay:

```csharp
ctrl.SetFloat("Speed", MathF.Abs(rb.LinearVelocity.X));
ctrl.SetBool("Grounded", cc.IsGrounded);
ctrl.SetInt("Combo", combo);
ctrl.SetTrigger("AttackTrig");     // consumed by the first transition that reads it
string? state = ctrl.CurrentState;
```

`ConditionOperator` covers the usual comparisons. `Trigger` parameters are
one-shot: they reset once a transition consumes them.

Conditions on a transition are **ANDed** — every condition must hold. For OR,
add two transitions to the same target.

Triggers are reset **only when a transition actually fires**, not at the end of
the frame. A `SetTrigger` whose transition conditions are not otherwise met
stays latched and will fire later — which is usually what you want for an attack
buffered during a landing, and occasionally a surprise. Clear it manually with
`SetBool` on the same name if you need one-frame semantics.

For a small number of states, driving `SpriteAnimator.Play` directly from an
`Update` (as in the example above) is simpler and easier to debug. Reach for
`AnimatorController` when the state graph is data-driven or genuinely complex.

---

## Tween

A chainable tween system for anything that changes over time. `SBEngine.Update`
calls `Tween.UpdateAll(Time.DeltaTime)` for you, between `SceneManager.Update`
and `LateUpdate` — so tweens run on **scaled** time and freeze with
`Time.TimeScale = 0`.

### Basic tweens

```csharp
Tween.Create()
     .TweenPosition(actor.Transform, new Vector2(400, 200), 0.6f, EaseType.OutCubic)
     .Play();

Tween.Create().TweenRotation(t, MathF.PI, 0.5f, EaseType.InOutSine).Play();
Tween.Create().TweenScale(t, new Vector2(1.4f, 1.4f), 0.15f, EaseType.OutBack).Play();
Tween.Create().TweenColor(spriteRenderer, Color.Red, 0.2f, EaseType.Linear).Play();
```

Arbitrary values, by getter/setter or by property name:

```csharp
Tween.Create()
     .TweenValue(() => _alpha, v => _alpha = v, target: 0f, duration: 1f, EaseType.OutQuad)
     .Play();

Tween.Create()
     .TweenFloat(audio.Music, nameof(AudioBus.Volume), 0.2f, 1.5f, EaseType.OutQuad)
     .Play();
```

### Chaining, looping, callbacks

Steps appended to one `Tween` run **in sequence**:

```csharp
Tween.Create()
     .TweenScale(t, Vector2.One * 1.3f, 0.1f, EaseType.OutQuad)
     .TweenScale(t, Vector2.One,        0.2f, EaseType.OutBounce)
     .OnComplete(() => Debug.WriteLine("pop finished"))
     .Play();
```

```csharp
Tween.Create()
     .TweenPosition(t, up,   0.8f, EaseType.InOutSine)
     .TweenPosition(t, down, 0.8f, EaseType.InOutSine)
     .Loop(-1)                        // -1 = forever
     .Play();

Tween.Create().Delay(0.5f).TweenPosition(t, target, 1f).Play();
```

Control:

```csharp
var tw = Tween.Create().TweenPosition(t, target, 1f).Play();
tw.Pause();
tw.Resume();
tw.Kill();
bool playing = tw.IsPlaying;
bool done    = tw.IsComplete;
```

### Lifetime — bind tweens to actors

A tween holds a strong reference to whatever it animates. If the actor is
destroyed mid-tween, the tween keeps writing to a dead transform. Bind it:

```csharp
var tw = Tween.Create();
tw.BoundActor = actor;
tw.TweenPosition(actor.Transform, target, 1f).Play();
```

```csharp
public override void OnDestroy() => Tween.KillAllFor(Actor);
Tween.KillAll();                     // e.g. on scene change
```

Make `KillAllFor` part of your actor teardown as a habit — it is the single
easiest source of leaks in this system.

### Sequences — parallel and serial

```csharp
Tween.Sequence()
     .Append(Tween.Create().TweenPosition(t, mid, 0.4f, EaseType.OutQuad))
     .Join  (Tween.Create().TweenColor(sr, Color.Yellow, 0.4f))    // runs alongside the previous
     .AppendInterval(0.2f)
     .AppendCallback(() => audio.PlayOneShot("Assets/Audio/land.wav"))
     .Append(Tween.Create().TweenScale(t, Vector2.One, 0.2f, EaseType.OutBounce))
     .OnComplete(() => actor.Destroy())
     .Loop(1)
     .Play();
```

`Append` runs after the previous step; `Join` runs at the same time as it.

### Easing

`EaseType` covers the standard set, in `In` / `Out` / `InOut` variants:

```
Linear
Quad  Cubic  Quart  Quint  Sine  Expo  Circ  Back  Bounce  Elastic
Spring
```

```csharp
float eased = Easing.Evaluate(EaseType.OutBack, t01);
```

Practical picks: `OutQuad` for most UI, `OutCubic` for camera moves,
`OutBack` for pop-in, `OutBounce` for landings, `InOutSine` for looping idles.

---

## SkeletalAnimator

3D skeletal playback producing a bone palette for a skinning shader.

**For an animated 3D character, you almost certainly want
[30. MakeChibi](30-makechibi.md) instead.** Nothing in the engine fills a `Skeleton` or a
`Clips` list — there is no rig importer — the component cannot be saved in a scene file, and
the browser engine has no counterpart at all. Everything below works only if you hand-author
the bone table and supply your own skinning shader.

```csharp
var skel = actor.AddComponent<SkeletalAnimator>();
skel.Skeleton = bones;     // List<Bone>: Name, Index, ParentIndex, BindPose, InvBindPose
skel.Clips    = clips;     // List<SkeletalClip>: Name, Duration, TicksPerSecond, Channels

skel.Play("walk", loop: true);
skel.CrossFade("run", blendTime: 0.25f);
skel.Stop();

Matrix[] palette = skel.BonePalette;
```

Sampling interpolates translation, rotation (slerp) and scale between
`AnimationKeyframe`s on each `BoneChannel`, then composes bone-local transforms
up the parent chain and multiplies by `InvBindPose`.

Upload the palette to your shader yourself:

```csharp
effect.Parameters["Bones"]?.SetValue(skel.BonePalette);
```

### Rendering a skinned mesh

`SkinnedMeshRenderer` is the render half. It imports through AssimpNet into a
`VertexPositionNormalTextureBlend` vertex format that carries `BlendIndices` and
`BlendWeights`, and feeds `SkeletalAnimator.BonePalette` to your shader.

```csharp
using SexyBiscuit.Engine.Rendering;

var skin = actor.AddComponent<SkinnedMeshRenderer>();
if (!skin.LoadModel("Assets/Models/hero.fbx", GraphicsDevice))
    Debug.WriteLine("import failed, or the model exceeds MaxBones");

skin.Materials.Add(heroMaterial);          // Material3D.Shader must do the skinning
```

```csharp
SkinnedMeshRenderer.MaxBones;              // 72
skin.BoneCount;  skin.SubMeshes;  skin.ModelPath;
skin.LocalBounds;  skin.WorldBounds;       // Bounds, for culling
SkinnedMeshRenderer.All;                   // static registry
```

`Start` resolves the actor's `SkeletalAnimator`, so put both components on the
same actor and the palette flows through automatically. The palette is padded to
`MaxBones` before upload, so your shader can declare a fixed-size array.

**Your material still needs a skinning shader.** `BasicEffect` cannot skin —
supply a `Material3D.Shader` that reads `Bones`, `BlendIndices` and
`BlendWeights`. `LoadModel` returns `false` when the model needs more than 72
bones; split the mesh or raise `MaxBones` and the shader array together.

---

## Recipes

### Screen-shake on hit, without a camera reference

```csharp
public override void OnCollisionEnter(CollisionData data)
{
    if (data.Other.Tag != "Bullet") return;

    var cam = Actor.Scene?.FindByName("Main Camera")?.GetComponent<Camera2D>();
    cam?.Shake(0.6f, 0.25f);

    var sr = GetComponent<SpriteRenderer>();
    if (sr != null)
    {
        Tween.Create()
             .TweenColor(sr, Color.Red,   0.05f)
             .TweenColor(sr, Color.White, 0.15f)
             .Play();
    }
}
```

### Floating damage number

```csharp
void SpawnDamageNumber(UiCanvas canvas, Vector2 screenPos, int amount)
{
    UiNode label = canvas.Root.Add(new UiNode
    {
        Kind        = UiKind.Label,
        Text        = amount.ToString(),
        Tint        = Color.OrangeRed,
        Positioning = PositionMode.Absolute,
        Offset      = screenPos,
        TextAlign   = AlignMode.Center,
    });

    Tween.Create()
         .TweenFloat(label, nameof(label.Opacity), 0f, 0.8f, EaseType.Linear)
         .TweenValue(() => label.Offset.Y,
                     y => label.Offset = new Vector2(label.Offset.X, y),
                     screenPos.Y - 48f, 0.8f, EaseType.OutCubic)
         .OnComplete(label.Detach)
         .Play();
}
```

`Camera2D.WorldToScreen` turns a world position into the screen one this wants. In 3D, set
`WorldFollow` and `WorldAnchor` on the node and the canvas does the projection itself — see
[9. UI](09-ui.md#following-a-world-point-without-a-world-canvas). Either way the number stays
in screen space, where it is crisp and upright; a canvas that genuinely belongs to a surface in
the scene is `UiCanvas.Space = World` instead.

### Fade a scene in

```csharp
UiNode overlay = canvas.Root.Add(new UiNode
{
    WidthMode = SizeMode.Stretch,
    HeightMode = SizeMode.Stretch,
    Background = Color.Black,
    Order = 1000,
});

Tween.Create()
     .TweenValue(() => overlay.Opacity, v => overlay.Opacity = v, 0f, 0.75f, EaseType.OutQuad)
     .OnComplete(overlay.Detach)
     .Play();
```

`Stretch` on both axes is what covers the screen, and it keeps covering it through a resize.

---

## Next

- [11. JavaScript Scripting](11-scripting.md)
- [30. MakeChibi](30-makechibi.md) — animated 3D characters that need no rig and no assets.
- [Tutorial 9: Animation & Tweens](../tutorials/09-animation-and-tweens.md)
