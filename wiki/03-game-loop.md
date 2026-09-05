# 3. The Game Loop

The most important page in the wiki. `SBEngine` drives most of the frame for you — time, input, physics, the scene
graph, tweens, timers, coroutines, audio and the 3D render pass. It leaves the
2D `SpriteBatch`, networking, and the debug tooling for your game. Knowing which
is which saves an afternoon.

> **This engine is under active development.** Verify the table below against
> `SexyBiscuit.Engine/Engine.cs` before you trust it; the
> [re-verification recipe](#re-verifying-this-page) at the bottom takes ten
> seconds.

---

## What `SBEngine` does per frame

From `SexyBiscuit.Engine/Engine.cs`:

```csharp
protected override void Update(GameTime gameTime)
{
    Time.Advance((float)gameTime.ElapsedGameTime.TotalSeconds);

    float dt         = Time.DeltaTime;          // scaled by Time.TimeScale
    float unscaledDt = Time.UnscaledDeltaTime;

    Input.Update(unscaledDt);                   // menus stay live while paused
    GameInstance.InternalTick(dt);

    float step = Config.FixedTimestep;
    Time.FixedDeltaTime = step * Time.TimeScale;

    _fixedAccumulator += dt;
    int steps = 0;
    while (_fixedAccumulator >= step && steps < Config.MaxFixedStepsPerFrame)
    {
        // Physics steps BEFORE FixedUpdate.
        if (Config.EnablePhysics2D) PhysicsSystem2D.Instance.FixedStep(step);
        if (Config.EnablePhysics3D) PhysicsSystem3D.Instance.FixedStep(step);

        SceneManager.FixedUpdate(step);
        _fixedAccumulator -= step;
        steps++;
    }
    if (_fixedAccumulator > step * Config.MaxFixedStepsPerFrame)
        _fixedAccumulator = 0f;                 // give up on catching up

    SceneManager.Update(dt);

    Tween.UpdateAll(dt);
    Timers.Tick(dt, unscaledDt);
    Coroutines.Tick(dt, unscaledDt);

    SceneManager.LateUpdate(dt);
    Audio.Update(dt);

    base.Update(gameTime);
}

protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);

    if (Config.Enable3D && SceneManager.ActiveScene != null)
        Renderer3D.Render(SceneManager.ActiveScene);   // 3D pass

    SceneManager.Draw(SpriteBatch);                    // 2D pass — no SpriteBatch.Begin
    base.Draw(gameTime);
}
```

### Physics steps before `FixedUpdate` — Verified

Worth internalising, because it changes how forces behave:

```
loop { PhysicsSystem2D.FixedStep(step)  →  SceneManager.FixedUpdate(step) }
```

A force applied in `FixedUpdate` is **simulated on the next step**, and the
transform your `FixedUpdate` reads is the result of the step that just ran. That
is a coherent ordering — react to the world, then push on it — but it means a
`Rigidbody2D.AddImpulse` in `FixedUpdate` shows up one step later.

### Services the engine constructs for you

`Initialize()` builds these before calling `OnEngineReady()`:

| Property | Type | Notes |
|---|---|---|
| `Assets` | `AssetManager` | |
| `Input` | `InputManager` | pumped on **unscaled** time |
| `Audio` | `AudioManager` | |
| `SceneManager` | `SceneManager` | |
| `Timers` | `TimerManager` | also `TimerManager.Instance` |
| `Coroutines` | `CoroutineRunner` | also `CoroutineRunner.Instance` |
| `Renderer2D` | `RenderSystem2D` | already `Initialize`d |
| `Renderer3D` | `RenderSystem3D` | already `Initialize`d |
| `GameInstance` | `GameInstance` | from `Config.GameInstanceFactory`, else the base class |

So **do not construct your own `RenderSystem2D`** — use `Renderer2D`.

---

## Who drives what

| System | Driven by the engine? | Who calls it |
|---|---|---|
| `Time.Advance` | ✅ | — |
| `InputManager.Update` | ✅ (unscaled time) | — |
| `GameInstance.Tick` | ✅ | — |
| `PhysicsSystem2D.FixedStep` | ✅ when `Config.EnablePhysics2D` | — |
| `PhysicsSystem3D.FixedStep` | ✅ when `Config.EnablePhysics3D` | — |
| `SceneManager` Fixed/Update/Late/Draw | ✅ | — |
| `Tween.UpdateAll` | ✅ | — |
| `TimerManager.Tick` | ✅ | — |
| `CoroutineRunner.Tick` | ✅ | — |
| `AudioManager.Update` | ✅ | — |
| `RenderSystem3D.Render` | ✅ when `Config.Enable3D` | — |
| **`SpriteBatch.Begin`/`End` around the 2D scene draw** | ❌ | **you** |
| `NetworkManager.Tick` | ❌ | **you** |
| `ScriptHotReload.Update` | ❌ | **you** |
| `Gizmos.Update` / `Flush` | ❌ | **you** |
| `DebugOverlay.Update` / `Draw` | ❌ | **you** |
| `Profiler.BeginFrame` / `EndFrame` | ❌ | **you** |
| `NetworkDiagnostics` / `MemoryViewer` | ❌ | **you** |
| `SteamManager.Update` | ❌ | **you** |
| `LODGroup.Update` | ❌ | **you** |

Turn a simulation off rather than leaving it idling: `EnablePhysics3D = false`
in a 2D game, `EnablePhysics2D = false` in a 3D one.

### The `SpriteBatch` gap

`SceneManager.Draw(SpriteBatch)` walks the scene and calls each component's
`Draw(SpriteBatch)` — including `SpriteRenderer.Draw`, which calls `sb.Draw(...)`.
No one calls `SpriteBatch.Begin()` first, so MonoGame throws:

```
InvalidOperationException: Begin must be called successfully before you can call Draw.
```

The `Renderer2D` the engine builds for you is exactly the right tool; it just is
not invoked by `SBEngine.Draw`. Override `Draw` and route through it.

---

## The recommended bootstrap

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Debug;
using SexyBiscuit.Engine.Networking;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scripting;

namespace MyGame;

public class Game : SBEngine
{
    private ScriptHotReload? _hotReload;
    private Camera2D?        _camera;

    public Game() : base(new EngineConfig
    {
        WindowTitle     = "My Game",
        WindowWidth     = 1280,
        WindowHeight    = 720,
        Enable3D        = false,       // pure 2D: skip the 3D pass
        EnablePhysics3D = false,       // …and the Bepu simulation
        EnablePhysics2D = true,
    }) { }

    // -----------------------------------------------------------------------
    protected override void OnEngineReady()
    {
        // Renderer2D / Renderer3D are already constructed and initialised.
        if (Config.HotReload)
            _hotReload = new ScriptHotReload("Scripts");

        PhysicsSystem2D.Instance.Gravity = new Vector2(0f, 30f);

        BuildFirstScene();
    }

    private void BuildFirstScene()
    {
        var scene = SceneManager.CreateScene("Main");

        var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
        _camera = cameraActor.AddComponent<Camera2D>();
        scene.AddActor(cameraActor);

        // … the rest of the scene …
    }

    // -----------------------------------------------------------------------
    protected override void Update(GameTime gameTime)
    {
        Profiler.BeginFrame();

        Profiler.Begin("Engine");
        base.Update(gameTime);   // time, input, physics, scenes, tweens, timers,
        Profiler.End("Engine");  // coroutines, audio

        float dt = Time.DeltaTime;

        NetworkManager.Instance?.Tick(Time.UnscaledDeltaTime);   // never pause the network
        _hotReload?.Update();

#if DEBUG || DEVELOPMENT
        Gizmos.Update(dt);
        DebugOverlay.Update(dt);
        NetworkDiagnostics.Update(dt);
        MemoryViewer.Update();
#endif
        // SteamManager.Instance?.Update();

        Profiler.EndFrame();
    }

    // -----------------------------------------------------------------------
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        if (Config.Enable3D) Renderer3D.Render(scene);

        // 2D world pass, through the camera. UI is drawn separately below.
        var ui = scene.GetLayer("ui");
        if (ui != null) ui.Visible = false;
        Renderer2D.RenderScene(SpriteBatch, scene, _camera);
        if (ui != null) ui.Visible = true;

        // Screen-space pass: UI + debug overlays.
        SpriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        ui?.Draw(SpriteBatch);
#if DEBUG || DEVELOPMENT
        Gizmos.Flush(SpriteBatch, GraphicsDevice);
        DebugOverlay.Draw(SpriteBatch);
#endif
        SpriteBatch.End();

        // Deliberately no base.Draw(gameTime): it would run the 3D pass again
        // and issue an unbatched SceneManager.Draw.
    }

    protected override void UnloadContent()
    {
        _hotReload?.Dispose();
        base.UnloadContent();   // GameInstance, timers, coroutines, Renderer3D
    }
}
```

The whole of your frame is now those two overrides. Everything else the engine
handles.

## Hosting the engine inside another app

`SBEngine` normally owns the window: `Run()` calls MonoGame's `Initialize`,
which builds every service, and takes over the process message loop. That is no
use to an editor, a tool, or a test harness that already owns the device and the
loop — which is why the editor could not start until `InitializeHosted` existed.

```csharp
public bool IsHosted { get; }
public void InitializeHosted(GraphicsDevice graphicsDevice, ContentManager content);
public void TickHosted(float rawDeltaSeconds);
```

`InitializeHosted` brings up the same services against a device the caller
already owns — `SpriteBatch`, `Assets`, `Input`, `Audio`, `SceneManager`,
`Timers`, `Coroutines`, `Renderer2D`, `Renderer3D`, `GameInstance` — and then
calls `OnEngineReady()`, exactly as the standalone path does. It is idempotent:
a second call while `IsHosted` returns early.

```csharp
// In your host's Initialize:
_engine = new SBEngine(new EngineConfig { /* … */ });
_engine.InitializeHosted(GraphicsDevice, Content);

// In your host's Update:
_engine.TickHosted((float)gameTime.ElapsedGameTime.TotalSeconds);

// In your host's Draw:
_engine.Renderer3D.Render(scene);
_engine.Renderer2D.RenderScene(_spriteBatch, scene, camera);
```

`TickHosted` advances the engine in the **same order as the standalone loop** —
time, game instance, the physics + `FixedUpdate` accumulator, `Update`, tweens,
timers, coroutines, `LateUpdate`, audio — with one deliberate omission:

> **`TickHosted` does not pump input.** The host owns the keyboard and mouse and
> decides when the game should see them — which is how an editor keeps WASD out
> of the game while you are typing in a panel. Call `Input.Update(dt)` yourself
> when it should.

```csharp
if (viewportHasFocus) _engine.Input.Update(dt);
_engine.TickHosted(dt);
```

`SceneManager.Update`, `FixedUpdate`, `LateUpdate` and `Draw` are `public` for
the same reason — a host that wants finer control than `TickHosted` gives can
drive the scene directly. That is what the editor's play mode does, which is
also why it runs *only* `Update`.

---

## Timing

```csharp
Time.DeltaTime            // scaled seconds since the last frame
Time.UnscaledDeltaTime    // real seconds, ignoring TimeScale
Time.FixedDeltaTime       // one fixed step, scaled
Time.TimeSinceStartup
Time.RealtimeSinceStartup
Time.FrameCount
Time.TimeScale            // 0 pauses, 0.5 slow motion, 2 double speed
Time.MaximumDeltaTime     // clamp on one frame's raw delta, default 0.1s
Time.Fps                  // smoothed over roughly half a second
```

`Time.Advance` is called first thing in `SBEngine.Update`, so `Time` is valid
everywhere in your frame. Lifecycle callbacks still receive `dt` directly —
either is correct; `dt` is the more explicit choice inside a component,
`Time.DeltaTime` the more convenient one in a static helper.

`Time.MaximumDeltaTime` clamps the raw delta before scaling, so a breakpoint or
a window drag cannot produce a single enormous step.

### Pausing

```csharp
Time.TimeScale = 0f;
```

`Update`, `FixedUpdate` and `LateUpdate` still run — they just receive `dt == 0`.
Input keeps running on unscaled time, so menus stay responsive. Systems that
must keep moving while paused should read `Time.UnscaledDeltaTime`.

Physics and tweens are inside the engine's own scaled path, so they pause with
it. Anything **you** pump must decide for itself:

```csharp
NetworkManager.Instance?.Tick(Time.UnscaledDeltaTime);   // never pause the network
```

An alternative for a pause menu that must freeze only gameplay:

```csharp
foreach (var layer in scene.Layers)
    if (layer.Name != "ui")
        layer.Active = false;         // Visible stays true, so the world still renders
```

---

## Timers

`TimerManager` is engine-owned and ticked between `Update` and `LateUpdate`.

```csharp
var timers = SBEngine.Instance.Timers;      // or TimerManager.Instance

TimerHandle h = timers.SetTimer(2.5f, () => Debug.WriteLine("boom"));
TimerHandle r = timers.SetTimer(0.5f, Fire, looping: true);
timers.SetTimerForNextTick(() => SpawnWave());       // deferred to the next tick

timers.Pause(h);
timers.Resume(h);
timers.Clear(h);
timers.ClearAll();

bool  live = timers.IsActive(h);
float left = timers.GetRemaining(h);
int   n    = timers.ActiveCount;
```

`SetTimer` takes a `useUnscaledTime` option for timers that must keep running
while `Time.TimeScale == 0`.

Prefer a timer over a hand-rolled `_cooldown -= dt` when the delay is one-shot
and the callback is self-contained — it removes state from your component.

## Coroutines

`CoroutineRunner` is engine-owned and ticked alongside timers.

```csharp
using SexyBiscuit.Engine.Core;

IEnumerator SpawnWaves()
{
    for (int wave = 1; wave <= 5; wave++)
    {
        SpawnWave(wave);
        yield return new WaitForSeconds(8f);
        yield return new WaitUntil(() => EnemiesAlive == 0);
    }
    yield return new WaitWhile(() => BossIntroPlaying);
    StartBossFight();
}
```

```csharp
Coroutine c = SBEngine.Instance.Coroutines.Start(SpawnWaves(), owner: this);
SBEngine.Instance.Coroutines.Stop(c);
SBEngine.Instance.Coroutines.StopAllFor(this);      // e.g. from Component.OnDestroy
```

Yield instructions: `WaitForSeconds`, `WaitForSecondsRealtime` (ignores
`TimeScale`), `WaitUntil(predicate)`, `WaitWhile(predicate)`. Passing an
`owner` and calling `StopAllFor(owner)` in `OnDestroy` is the discipline that
keeps coroutines from outliving their actors.

---

## Re-verifying this page

```bash
# Which systems does the loop actually pump?
sed -n '/protected override void Update(GameTime/,/^    }/p' SexyBiscuit.Engine/Engine.cs
sed -n '/protected override void Draw(GameTime/,/^    }/p'   SexyBiscuit.Engine/Engine.cs

# Is physics stepped by the engine?
grep -rn --include='*.cs' 'FixedStep(' SexyBiscuit.Engine | grep -v 'Physics/PhysicsSystem'

# Are tweens pumped by the engine?
grep -rn --include='*.cs' 'Tween.UpdateAll' SexyBiscuit.Engine
```

A hit in `Engine.cs` for either means the engine drives it; **no** output means
that system is yours to pump, and you should add it to your `Update`.

---

## Checklist for a new project

- [ ] Subclass `SBEngine`; build the first scene in `OnEngineReady`.
- [ ] Use the engine's `Renderer2D` / `Renderer3D` — do not construct your own.
- [ ] Override `Draw`; call `Renderer2D.RenderScene`; do **not** call `base.Draw`.
- [ ] Turn off the simulations you do not use: `Enable3D`, `EnablePhysics2D`,
      `EnablePhysics3D`.
- [ ] Call `NetworkManager.Instance?.Tick(dt)` if you use networking, on
      **unscaled** time.
- [ ] Construct `ScriptHotReload` and call `Update()` if you use JS scripts.
- [ ] Call `SteamManager.Instance?.Update()` if you called `SteamManager.Init`.
- [ ] Pump `Gizmos` / `DebugOverlay` / `Profiler` if you want them, guarded by
      `#if DEBUG || DEVELOPMENT`.

---

## Next

- [4. 2D Rendering](04-rendering-2d.md)
- [22. Gameplay Framework](22-gameplay-framework.md)
- [Tutorial 1: Hello, Window](../tutorials/01-hello-window.md)
