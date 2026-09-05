# Tutorial 12 — Particles & Post-FX

**You will build:** an effects library — explosions, trails, muzzle flashes —
plus 2D lighting and a post-process chain. **Time:** ~30 minutes.

Builds on [Tutorial 11](11-saving-and-loading.md).

---

## 1. ParticleEmitter

A CPU particle system that draws through the same `SpriteBatch` as your sprites,
so particles are in **world space** and respect the camera.

```csharp
using SexyBiscuit.Engine.Rendering;

var fx = actor.AddComponent<ParticleEmitter>();

fx.MaxParticles    = 300;      // fixed pool size
fx.EmitRate        = 60f;      // particles per second
fx.Loop            = true;
fx.ParticleTexture = White;    // the 1×1 pixel from Tutorial 1

fx.MinLifetime = 0.3f;  fx.MaxLifetime = 1.2f;
fx.MinVelocity = new Vector2(-80, -220);
fx.MaxVelocity = new Vector2( 80, -120);

fx.StartColor = Color.Orange;
fx.EndColor   = Color.Transparent;      // fades out over its lifetime

fx.MinStartSize = 6f;  fx.MaxStartSize = 12f;  fx.EndSize = 0f;

fx.GravityScale = 1f;
fx.WorldGravity = 980f;                 // pixels/s², applied × GravityScale
```

```csharp
fx.Play();
fx.Stop();        // stops emitting; live particles finish
fx.Clear();       // kills every live particle now
fx.Burst();       // emits BurstCount at once

fx.BurstMode  = true;
fx.BurstCount = 50;
```

`MaxParticles` is a hard pool size — the emitter never allocates beyond it, and
new particles are dropped when the pool is full. Size it for the worst case you
expect, not the average.

## 2. An effects library

Bundle each effect into a factory that spawns a self-destructing actor.

`MyGame/Systems/Effects.cs`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Systems;

public static class Effects
{
    public static Texture2D Pixel = null!;     // assign once at boot

    /// <summary>A one-shot burst that cleans itself up.</summary>
    public static Actor Burst(Scene scene, Vector2 position, Color start, Color end,
                              int count = 40, float speed = 300f, float life = 0.6f,
                              string layer = "foreground")
    {
        var actor = new Actor("Burst");
        actor.Transform.Position = position;
        actor.LifeSpan = life + 0.4f;              // outlive the slowest particle

        var fx = actor.AddComponent<ParticleEmitter>();
        fx.MaxParticles    = count;
        fx.ParticleTexture = Pixel;
        fx.Loop            = false;
        fx.BurstMode       = true;
        fx.BurstCount      = count;
        fx.MinLifetime     = life * 0.5f;
        fx.MaxLifetime     = life;
        fx.MinVelocity     = new Vector2(-speed, -speed);
        fx.MaxVelocity     = new Vector2( speed,  speed);
        fx.StartColor      = start;
        fx.EndColor        = end;
        fx.MinStartSize    = 3f;
        fx.MaxStartSize    = 7f;
        fx.EndSize         = 0f;
        fx.GravityScale    = 0.4f;

        scene.AddActor(actor, layer);
        fx.Burst();
        return actor;
    }

    public static Actor Explosion(Scene scene, Vector2 position)
        => Burst(scene, position,
                 new Color(255, 220, 120), new Color(180, 40, 20, 0),
                 count: 80, speed: 420f, life: 0.75f);

    public static Actor Blood(Scene scene, Vector2 position)
        => Burst(scene, position,
                 new Color(200, 40, 50), new Color(90, 10, 15, 0),
                 count: 24, speed: 220f, life: 0.5f);

    public static Actor Dust(Scene scene, Vector2 position)
        => Burst(scene, position,
                 new Color(190, 180, 160, 190), new Color(190, 180, 160, 0),
                 count: 14, speed: 90f, life: 0.45f);

    /// <summary>A short-lived directional spray, e.g. a muzzle flash.</summary>
    public static Actor Muzzle(Scene scene, Vector2 position, float angleRadians)
    {
        var actor = new Actor("Muzzle");
        actor.Transform.Position = position;
        actor.LifeSpan = 0.3f;

        var dir   = new Vector2(MathF.Cos(angleRadians), MathF.Sin(angleRadians));
        var spread = new Vector2(-dir.Y, dir.X) * 90f;

        var fx = actor.AddComponent<ParticleEmitter>();
        fx.MaxParticles    = 16;
        fx.ParticleTexture = Pixel;
        fx.Loop            = false;
        fx.BurstMode       = true;
        fx.BurstCount      = 12;
        fx.MinLifetime     = 0.05f;
        fx.MaxLifetime     = 0.15f;
        fx.MinVelocity     = dir * 400f - spread;
        fx.MaxVelocity     = dir * 700f + spread;
        fx.StartColor      = new Color(255, 240, 190);
        fx.EndColor        = new Color(255, 140, 60, 0);
        fx.MinStartSize    = 2f;
        fx.MaxStartSize    = 5f;
        fx.EndSize         = 0f;
        fx.GravityScale    = 0f;

        scene.AddActor(actor, "foreground");
        fx.Burst();
        return actor;
    }
}
```

```csharp
Effects.Pixel = White;      // once, in OnEngineReady
```

`Actor.LifeSpan` handles cleanup, so no effect ever needs a bespoke timer or a
destroy callback. Set it slightly longer than the slowest particle's lifetime.

## 3. Continuous emitters

For a rocket trail or a torch, keep one looping emitter parented to the object:

```csharp
public sealed class Trail : Component
{
    public Color Colour = new(255, 200, 120);
    public float Rate   = 90f;

    private ParticleEmitter _fx = null!;

    public override void Start()
    {
        _fx = AddComponent<ParticleEmitter>();
        _fx.MaxParticles    = 120;
        _fx.ParticleTexture = Effects.Pixel;
        _fx.EmitRate        = Rate;
        _fx.Loop            = true;
        _fx.MinLifetime     = 0.15f;
        _fx.MaxLifetime     = 0.45f;
        _fx.MinVelocity     = new Vector2(-24, -24);
        _fx.MaxVelocity     = new Vector2( 24,  24);
        _fx.StartColor      = Colour;
        _fx.EndColor        = Colour * 0f;
        _fx.MinStartSize    = 4f;
        _fx.MaxStartSize    = 8f;
        _fx.EndSize         = 0f;
        _fx.GravityScale    = 0f;
        _fx.Play();
    }

    public void SetEmitting(bool on) { if (on) _fx.Play(); else _fx.Stop(); }
}
```

`Stop()` rather than destroying the emitter lets the existing particles finish
their lives instead of vanishing.

## 4. Performance

Particles cost one `sb.Draw` each. A few thousand on screen is fine; tens of
thousands is not.

- **Cap `MaxParticles` per effect.** It is a hard pool, so this is your budget.
- **Cap concurrent effects.** Track them and refuse to spawn beyond a limit:

```csharp
public static class EffectBudget
{
    public const int MaxConcurrent = 24;
    private static readonly List<Actor> Live = new();

    public static bool TrySpawn(Func<Actor> factory)
    {
        Live.RemoveAll(a => a.IsDestroyed);
        if (Live.Count >= MaxConcurrent) return false;
        Live.Add(factory());
        return true;
    }
}
```

- **Reuse one texture.** Every distinct `Texture2D` in a batch forces a
  draw-call break, so a single pixel or a single atlas keeps particles in one
  batch.
- **Watch `MemoryViewer`.** A climbing `Gen0` count means per-frame allocation.

## 5. 2D lighting

`Lighting2D` renders a light map and composites it over the scene.

```csharp
using SexyBiscuit.Engine.Rendering;

// in Game
private Lighting2D? _lighting;

protected override void OnEngineReady()
{
    _lighting = new Lighting2D();
    _lighting.Initialize(GraphicsDevice);
    _lighting.AmbientColor = new Color(26, 28, 42);
    _lighting.Downsample   = 1;             // raise to 2 for a cheaper, softer map
    // …
}
```

Lights and shadow casters are components that register themselves:

```csharp
var lamp = new Actor("Lamp");
lamp.Transform.Position = new Vector2(640, 400);

var light = lamp.AddComponent<Light2D>();
light.Type         = Light2DType.Point;
light.Color        = new Color(255, 215, 150);
light.Intensity    = 1.5f;
light.Radius       = 320f;
light.Falloff      = 2f;
light.CastsShadows = true;
scene.AddActor(lamp);

var wall = CreateBox("Wall", new Vector2(400, 500), new Vector2(160, 40), Color.Gray);
wall.AddComponent<ShadowCaster2D>().SetBox(160, 40);
scene.AddActor(wall);
```

Draw it between the world pass and the UI pass — lights should affect the world,
not the HUD:

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);

    var scene = SceneManager.ActiveScene;
    if (scene == null) return;

    var ui = scene.GetLayer("ui");
    if (ui != null) ui.Visible = false;
    Renderer2D.RenderScene(SpriteBatch, scene, Camera);
    if (ui != null) ui.Visible = true;

    // Lighting, over the world only.
    if (_lighting is { Enabled: true } && Camera != null)
    {
        _lighting.BuildLightMap(Camera.GetViewMatrix(GraphicsDevice));
        _lighting.Composite(SpriteBatch);
    }

    // UI and overlays, unlit.
    SpriteBatch.Begin(samplerState: SamplerState.LinearClamp);
    ui?.Draw(SpriteBatch);
    Gizmos.Flush(SpriteBatch, GraphicsDevice);
    DebugOverlay.Draw(SpriteBatch);
    SpriteBatch.End();
}
```

```csharp
_lighting.Enabled  = false;        // skip the pass entirely
_lighting.LightMap;                // RenderTarget2D?, useful for debugging
_lighting.Dispose();               // in UnloadContent
```

`Light2D.All` and `ShadowCaster2D.All` are the static registries the pass reads.
Before optimising anything else, set `Downsample = 2` — a half-resolution light
map is usually indistinguishable and roughly a quarter of the cost.

### A torch that flickers

```csharp
public sealed class Flicker : Component
{
    public float Base      = 1.4f;
    public float Amount    = 0.25f;
    public float Frequency = 9f;

    private Light2D? _light;
    private float    _t;

    public override void Start() => _light = GetComponent<Light2D>();

    public override void Update(float dt)
    {
        if (_light == null) return;
        _t += dt * Frequency;

        // Two out-of-phase sines read as organic; one reads as a machine.
        float n = MathF.Sin(_t) * 0.6f + MathF.Sin(_t * 2.37f) * 0.4f;
        _light.Intensity = Base + n * Amount;
    }
}
```

## 6. Post-processing

`RenderSystem2D` applies an ordered chain of `Effect` passes to the finished
scene via ping-pong render targets.

```csharp
using SexyBiscuit.Engine.Rendering;

var grade = Content.Load<Effect>("Shaders/ColourGrade");    // MGCB pipeline

Renderer2D.PostProcessPasses.Add(new PostProcessPass(grade, e =>
{
    e.Parameters["Saturation"]?.SetValue(1.15f);
    e.Parameters["Contrast"]?.SetValue(1.05f);
    e.Parameters["Vignette"]?.SetValue(0.35f);
}));
```

The `ConfigureEffect` callback runs once per pass per frame, so it is the right
place for animated uniforms:

```csharp
float pulse = 0f;
Renderer2D.PostProcessPasses.Add(new PostProcessPass(damageEffect, e =>
{
    e.Parameters["Intensity"]?.SetValue(pulse);
}));

// elsewhere
health.Damaged += _ =>
{
    pulse = 1f;
    Tween.Create().TweenValue(() => pulse, v => pulse = v, 0f, 0.5f, EaseType.OutQuad).Play();
};
```

**`AssetManager` cannot load `Effect`** — it handles `Texture2D`, `SoundEffect`,
`string` and `byte[]`. Shaders go through the MonoGame content pipeline
(`Content.Load<Effect>`), or you construct one from a compiled `.mgfxo` byte
array yourself.

A minimal grayscale shader, `Content/Shaders/Grayscale.fx`:

```hlsl
sampler2D Input : register(s0);
float Intensity;

float4 MainPS(float2 uv : TEXCOORD0) : COLOR0
{
    float4 c = tex2D(Input, uv);
    float  g = dot(c.rgb, float3(0.299, 0.587, 0.114));
    c.rgb = lerp(c.rgb, g.xxx, Intensity);
    return c;
}

technique Post { pass P0 { PixelShader = compile ps_3_0 MainPS(); } }
```

## 7. Per-layer render targets

```csharp
Renderer2D.UsePerLayerRenderTargets = true;
```

Each named layer is drawn into its own transparent target and composited in
layer order. Use it when you need a per-layer effect — a blurred background, a
layer-specific tint. It costs one full-screen target per layer, so leave it off
unless you need it.

## 8. A complete impact

Everything from Tutorials 9 and 12, at one call site:

```csharp
using MyGame.Systems;

public sealed class Explosive : Component
{
    public float Radius = 120f;
    public int   Damage = 40;

    public void Detonate()
    {
        var scene = Actor.Scene;
        if (scene == null) return;
        var origin = Transform.Position;

        // Visual
        Effects.Explosion(scene, origin);
        Juice.Shake(1f, 0.4f);
        Juice.HitStop(0.06f, 0.05f);
        Sfx.Play("Assets/Audio/explosion.wav");

        // A brief flash of light
        var flash = new Actor("Flash");
        flash.Transform.Position = origin;
        flash.LifeSpan = 0.25f;
        var light = flash.AddComponent<Light2D>();
        light.Color     = new Color(255, 200, 130);
        light.Radius    = Radius * 2.5f;
        light.Intensity = 3f;
        scene.AddActor(flash, "foreground");

        Tween.Create()
             .TweenFloat(light, nameof(Light2D.Intensity), 0f, 0.25f, EaseType.OutQuad)
             .Play();

        // Damage, falling off with distance
        PhysicsSystem2D.Instance.CircleCast(origin, Radius, -1, out Actor[] caught);
        foreach (var target in caught)
        {
            float d = Vector2.Distance(origin, target.Transform.Position);
            int dmg = (int)(Damage * (1f - SBMath.Clamp01(d / Radius)));
            if (dmg > 0) target.GetComponent<Health>()?.Damage(dmg, Actor);
        }

        Actor.Destroy();
    }
}
```

---

## Checkpoint

You have:

- A reusable effects library with automatic cleanup via `LifeSpan`
- Continuous trails, and a budget to keep them bounded
- 2D lighting with shadow casters and a flickering torch
- A post-process chain, and where shaders actually come from

## Exercises

1. Add a `Weather` emitter for rain: a wide spawn band, high downward velocity,
   `GravityScale = 0`.
2. Add a chromatic-aberration post pass that scales with player damage.
3. Give `Light2D` a `Spot` type torch that follows the player's aim direction.

---

**Next:** [Tutorial 13 — 3D Basics](13-3d-basics.md)
