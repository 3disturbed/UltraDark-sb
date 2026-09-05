# Tutorial 19 — Build a Complete Game

**Biscuit Blaster** — a twin-stick wave shooter, start to finish: menu, waves,
enemies, weapons, upgrades, score, save, and a shipped build.
**Time:** ~3 hours.

This pulls together every tutorial. Nothing here is new API — it is the same
pieces, assembled.

---

## The design

> Survive waves of biscuits. Move with WASD, aim with the mouse, shoot
> automatically. Enemies drop crumbs; spend crumbs between waves on upgrades.
> Die and your high score is saved.

Deliberately small, deliberately complete. A finished small game teaches more
than an unfinished large one.

## Project layout

```
MyGame/
├── Program.cs
├── Game.cs                    the bootstrap from Tutorial 1
├── Layers.cs
├── ScriptExtensions.cs        AddScript
├── PhysicsExtensions.cs       AddBox / Rebuild
├── UserData.cs                per-user save paths
├── Components/
│   ├── Health.cs  TopDownMovement.cs  MouseAim.cs
│   ├── Weapon.cs  Bullet.cs  EnemyBrain.cs
│   ├── CrumbPickup.cs  HitReaction.cs
├── Systems/
│   ├── Juice.cs  Effects.cs  Sfx.cs  MusicDirector.cs
│   ├── WaveDirector.cs  Progress.cs
├── UI/
│   ├── Hud.cs  UpgradeScreen.cs  GameOverScreen.cs
├── Scenes/
│   ├── MainMenuScene.cs  ArenaScene.cs
├── Assets/  Scripts/  Scenes/
```

---

## Step 1 — Bootstrap

Start from [Tutorial 1](01-hello-window.md)'s `Game.cs`, with these additions:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Debug;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using MyGame.Systems;
using MyGame.UI;

namespace MyGame;

public class Game : SBEngine
{
    public Camera2D?      Camera        { get; private set; }
    public SpriteFont?    Font          { get; private set; }
    public MusicDirector  Music         { get; private set; } = null!;
    public WaveDirector?  Waves         { get; set; }
    public Hud?           Hud           { get; set; }
    public Texture2D      WhiteTexture  => White;

    private Texture2D? _white;
    private Texture2D  White => _white ??= MakePixel();

    public Game() : base(new EngineConfig
    {
        WindowTitle           = "Biscuit Blaster",
        WindowWidth           = 1280,
        WindowHeight          = 720,
        ClearColour           = new Color(14, 15, 24),
        VSync                 = true,
        Enable3D              = false,       // pure 2D: skip the 3D render pass
        EnablePhysics3D       = false,       // …and the Bepu simulation
        EnablePhysics2D       = false,       // this game uses distance checks, not bodies
        FixedTimestep         = 1f / 120f,
        MaxFixedStepsPerFrame = 8,
    }) { }

    protected override void OnEngineReady()
    {
        UserData.Install();

        try   { Font = Content.Load<SpriteFont>("Fonts/ui"); }
        catch { Font = null; }

        Effects.Pixel = White;
        Music = new MusicDirector(Audio);

        Audio.Master.Volume = PlayerPrefs.GetFloat("vol.master", 1f);
        Audio.Music.Volume  = PlayerPrefs.GetFloat("vol.music",  0.6f);
        Audio.SFX.Volume    = PlayerPrefs.GetFloat("vol.sfx",    1f);

        Renderer2D.SamplerState = SamplerState.PointClamp;

        Scenes.MainMenuScene.Load(this);
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);   // time, input, scenes, tweens, timers, coroutines, audio

        float dt = Time.DeltaTime;

#if DEBUG || DEVELOPMENT
        Gizmos.Update(dt);
        DebugOverlay.Update(dt);
#endif
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        var ui = scene.GetLayer("ui");
        if (ui != null) ui.Visible = false;
        Renderer2D.RenderScene(SpriteBatch, scene, Camera);
        if (ui != null) ui.Visible = true;

        SpriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        ui?.Draw(SpriteBatch);
#if DEBUG || DEVELOPMENT
        Gizmos.Flush(SpriteBatch, GraphicsDevice);
        DebugOverlay.Draw(SpriteBatch);
#endif
        SpriteBatch.End();
    }

    protected override void UnloadContent()
    {
        PlayerPrefs.Save();
        base.UnloadContent();
    }

    // ---- helpers ---------------------------------------------------------
    public void SetCamera(Camera2D c) => Camera = c;

    private Texture2D MakePixel()
    {
        var t = new Texture2D(GraphicsDevice, 1, 1);
        t.SetData(new[] { Color.White });
        return t;
    }

    public Actor CreateBox(string name, Vector2 pos, Vector2 size, Color colour)
    {
        var a = new Actor(name);
        a.Transform.Position   = pos;
        a.Transform.LocalScale = size;
        var sr = a.AddComponent<SpriteRenderer>();
        sr.Texture = White;
        sr.Tint    = colour;
        sr.Pivot   = new Vector2(0.5f, 0.5f);
        return a;
    }

    public Canvas CreateCanvas(Scene scene, string name = "UI")
    {
        var actor  = new Actor(name);
        var canvas = actor.AddComponent<Canvas>();
        canvas.Font                = Font;
        canvas.ScaleMode           = CanvasScaleMode.PixelPerfect;
        canvas.ReferenceResolution = new Vector2(Config.WindowWidth, Config.WindowHeight);
        scene.AddActor(actor, "ui");
        return canvas;
    }

    public Button MakeButton(string text, Vector2 pos, Vector2 size, Action onClick)
    {
        var b = new Button
        {
            Text = text, TextColor = Color.White,
            Position = pos, Size = size,
            NormalTexture = White, Tint = new Color(48, 54, 78),
        };
        b.OnClick      += onClick;
        b.OnHoverEnter += () => b.Tint = new Color(72, 82, 118);
        b.OnHoverExit  += () => b.Tint = new Color(48, 54, 78);
        return b;
    }
}
```

Both physics simulations are **off**. This game uses distance checks rather than
bodies — cheaper, simpler, and it avoids the collider-rebuild dance entirely.
Turning them off means the engine never creates or steps a world it would not
use.

## Step 2 — Progress

`MyGame/Systems/Progress.cs`:

```csharp
using SexyBiscuit.Engine.Save;

namespace MyGame.Systems;

/// <summary>Run state plus the persisted high score.</summary>
public static class Progress
{
    public static int   Score;
    public static int   Crumbs;
    public static int   Wave = 1;
    public static float RunTime;

    // Upgrade levels, spent between waves.
    public static int FireRateLevel = 0;
    public static int DamageLevel   = 0;
    public static int SpeedLevel    = 0;
    public static int HealthLevel   = 0;

    public static int  HighScore => PlayerPrefs.GetInt("highscore");
    public static bool IsNewHigh => Score > HighScore;

    public static void ResetRun()
    {
        Score = Crumbs = 0;
        Wave = 1;
        RunTime = 0f;
        FireRateLevel = DamageLevel = SpeedLevel = HealthLevel = 0;
    }

    public static void CommitHighScore()
    {
        if (!IsNewHigh) return;
        PlayerPrefs.SetInt("highscore", Score);
        PlayerPrefs.Save();
    }

    // Upgrade curves — every 8 crumbs, ~15% better.
    public static float FireInterval => 0.28f * MathF.Pow(0.85f, FireRateLevel);
    public static int   BulletDamage => 10 + DamageLevel * 4;
    public static float MoveSpeed    => 260f + SpeedLevel * 26f;
    public static int   MaxHealth    => 100 + HealthLevel * 25;
    public static int   UpgradeCost(int level) => 8 + level * 6;
}
```

Keeping the curves in one place means you can balance the whole game by editing
four expressions.

## Step 3 — The player

`MyGame/Components/Weapon.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using MyGame.Systems;

namespace MyGame.Components;

public sealed class Weapon : Component
{
    public ActorPool? BulletPool;
    public float      BulletSpeed = 780f;
    public float      Spread      = 0.05f;      // radians

    private float _cooldown;

    public override void Update(float dt)
    {
        _cooldown -= dt;
        if (_cooldown > 0f) return;
        _cooldown = Progress.FireInterval;

        Fire();
    }

    private void Fire()
    {
        if (BulletPool == null) return;

        float angle = Transform.Rotation + SBMath.RandomRange(-Spread, Spread);
        var   dir   = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var   muzzle = Transform.Position + dir * 24f;

        var bullet = BulletPool.Spawn();
        var b = bullet.GetComponent<Bullet>()!;
        b.Arm(muzzle, dir * BulletSpeed, Progress.BulletDamage);

        Effects.Muzzle(Actor.Scene!, muzzle, angle);
        Sfx.Play("Assets/Audio/shoot.wav", 0.35f, 0.15f);
    }
}
```

`MyGame/Components/Bullet.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using MyGame.Systems;

namespace MyGame.Components;

public sealed class Bullet : Component
{
    public ActorPool? Pool;
    public float      Life = 1.4f;

    private Vector2 _velocity;
    private int     _damage;
    private float   _remaining;

    public void Arm(Vector2 position, Vector2 velocity, int damage)
    {
        Transform.Position = position;
        Transform.Rotation = MathF.Atan2(velocity.Y, velocity.X);
        _velocity  = velocity;
        _damage    = damage;
        _remaining = Life;
    }

    public override void Update(float dt)
    {
        Transform.Position += _velocity * dt;

        _remaining -= dt;
        if (_remaining <= 0f) { Recycle(); return; }

        // Cheap proximity hit test — no physics bodies for bullets.
        var scene = Actor.Scene;
        if (scene == null) return;

        foreach (var enemy in scene.FindByTag("Enemy"))
        {
            if (enemy.IsDestroyed) continue;
            if (Vector2.DistanceSquared(enemy.Transform.Position, Transform.Position) > 28f * 28f)
                continue;

            enemy.GetComponent<Health>()?.Damage(_damage, Actor);
            Effects.Blood(scene, Transform.Position);
            Recycle();
            return;
        }
    }

    private void Recycle()
    {
        if (Pool != null) Pool.Despawn(Actor);
        else              Actor.Destroy();
    }
}
```

A distance check beats a physics body for bullets: no fixture churn, no
`Rebuild()`, and it is trivially fast for the counts involved.

`MyGame/Factories.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using MyGame.Components;
using MyGame.Systems;

namespace MyGame;

public static class Factories
{
    public static Actor CreatePlayer(Game game, Vector2 position)
    {
        var a = game.CreateBox("Player", position, new Vector2(26, 26),
                               new Color(120, 210, 255));
        a.Tag   = "Player";
        a.Layer = Layers.Player;

        a.AddComponent<TopDownMovement>().MaxSpeed = Progress.MoveSpeed;
        a.AddComponent<MouseAim>();
        a.AddComponent<Weapon>();

        var hp = a.AddComponent<Health>();
        hp.Max = Progress.MaxHealth;
        hp.InvulnerableAfterHit = 0.6f;

        a.AddComponent<HitReaction>();
        return a;
    }

    public static Actor CreateEnemy(Game game, Vector2 position, int wave)
    {
        int   hp    = 20 + wave * 6;
        float speed = 70f + wave * 5f;

        var a = game.CreateBox("Enemy", position, new Vector2(24, 24),
                               new Color(235, 110, 90));
        a.Tag   = "Enemy";
        a.Layer = Layers.Enemy;

        var brain = a.AddComponent<EnemyBrain>();
        brain.Speed        = speed;
        brain.TouchDamage  = 8 + wave;

        var health = a.AddComponent<Health>();
        health.Max = hp;
        health.Died += () =>
        {
            Progress.Score  += 10 + wave;
            Progress.Crumbs += 1;
            Effects.Explosion(a.Scene!, a.Transform.Position);
            Juice.Shake(0.25f, 0.15f);
            game.Hud?.Refresh();
        };

        a.AddComponent<HitReaction>();
        return a;
    }

    public static Actor CreateBullet(Game game)
    {
        var a = game.CreateBox("Bullet", Vector2.Zero, new Vector2(7, 3),
                               new Color(255, 238, 170));
        a.Tag = "Bullet";
        a.AddComponent<Bullet>();
        return a;
    }
}
```

## Step 4 — The enemy

`MyGame/Components/EnemyBrain.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using MyGame.Systems;

namespace MyGame.Components;

public sealed class EnemyBrain : Component
{
    public float Speed        = 80f;
    public int   TouchDamage  = 10;
    public float TouchRadius  = 26f;
    public float Separation   = 22f;

    private Actor? _target;
    private float  _retarget;

    public override void Start() => FindTarget();

    public override void Update(float dt)
    {
        _retarget -= dt;
        if (_retarget <= 0f) FindTarget();
        if (_target is null or { IsDestroyed: true }) return;

        var toTarget = _target.Transform.Position - Transform.Position;
        float dist = toTarget.Length();

        // Steer toward the player, plus a small push away from neighbours so
        // the swarm spreads instead of stacking into one sprite.
        var steer = dist > 0.01f ? toTarget / dist : Vector2.Zero;
        steer += AvoidNeighbours();

        Transform.Position += SBMath.SafeNormalize(steer) * Speed * dt;
        Transform.Rotation = MathF.Atan2(steer.Y, steer.X);

        if (dist <= TouchRadius)
            _target.GetComponent<Health>()?.Damage(TouchDamage, Actor);
    }

    private Vector2 AvoidNeighbours()
    {
        var push = Vector2.Zero;
        var scene = Actor.Scene;
        if (scene == null) return push;

        foreach (var other in scene.FindByTag("Enemy"))
        {
            if (ReferenceEquals(other, Actor) || other.IsDestroyed) continue;

            var away = Transform.Position - other.Transform.Position;
            float d = away.Length();
            if (d > 0.01f && d < Separation) push += away / d * (1f - d / Separation);
        }
        return push * 0.8f;
    }

    private void FindTarget()
    {
        _retarget = 0.4f;
        _target = Actor.Scene?.FindByTag("Player").FirstOrDefault();
    }
}
```

The separation term is three lines and it is the difference between a swarm and
a single overlapping blob. With more than ~60 enemies, replace the `FindByTag`
scan with a spatial hash.

## Step 5 — Waves

`MyGame/Systems/WaveDirector.cs`:

```csharp
using System.Collections;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;

namespace MyGame.Systems;

public sealed class WaveDirector : Component
{
    public Game     Game    = null!;
    public Vector2  Centre  = new(640, 360);
    public float    Radius  = 520f;

    public int  Alive      { get; private set; }
    public bool WaveActive { get; private set; }

    public event Action<int>? WaveCleared;

    private readonly List<Actor> _spawned = new();

    public override void Start() => Actor.StartCoroutine(RunWaves());

    private IEnumerator RunWaves()
    {
        yield return new WaitForSeconds(1.5f);

        while (true)
        {
            int count = 4 + Progress.Wave * 2;
            WaveActive = true;

            for (int i = 0; i < count; i++)
            {
                SpawnOne();
                yield return new WaitForSeconds(0.25f);
            }

            // Wait until the arena is clear.
            yield return new WaitWhile(() =>
            {
                _spawned.RemoveAll(a => a.IsDestroyed);
                Alive = _spawned.Count;
                return Alive > 0;
            });

            WaveActive = false;
            WaveCleared?.Invoke(Progress.Wave);
            Progress.Wave++;

            yield return new WaitForSeconds(0.5f);
        }
    }

    private void SpawnOne()
    {
        var scene = Actor.Scene;
        if (scene == null) return;

        // Spawn on a ring outside the visible arena.
        float angle = SBMath.RandomRange(0f, MathF.PI * 2f);
        var position = Centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Radius;

        var enemy = Factories.CreateEnemy(Game, position, Progress.Wave);
        scene.AddActor(enemy);
        _spawned.Add(enemy);
    }

    public override void OnDestroy() => Actor.StopAllCoroutines();
}
```

The whole wave loop is one readable coroutine: spawn, wait until clear,
announce, repeat. The same logic as a state machine of floats, without the
state machine.

## Step 6 — The arena

`MyGame/Scenes/ArenaScene.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using MyGame.Components;
using MyGame.Systems;
using MyGame.UI;

namespace MyGame.Scenes;

public static class ArenaScene
{
    public static void Load(Game game)
    {
        Progress.ResetRun();

        var scene = game.SceneManager.CreateScene("Arena");
        scene.AddLayer("projectiles", 50);

        // Camera
        var camActor = new Actor("Main Camera") { Tag = "Camera" };
        camActor.Transform.Position = new Vector2(640, 360);
        var camera = camActor.AddComponent<Camera2D>();
        camera.Follow.LerpSpeed = 4f;
        camera.Follow.Deadzone  = new Vector2(80, 60);
        camera.Bounds = new Rectangle(0, 0, 1280, 720);
        game.SetCamera(camera);
        scene.AddActor(camActor);

        // Arena floor
        var floor = game.CreateBox("Floor", new Vector2(640, 360), new Vector2(1180, 620),
                                   new Color(24, 26, 40));
        scene.AddActor(floor, "background");

        // Player
        var player = Factories.CreatePlayer(game, new Vector2(640, 360));
        scene.AddActor(player);
        camera.Follow.Target = player;

        // Bullet pool
        var bullets = new ActorPool(scene, () => Factories.CreateBullet(game),
                                    layerName: "projectiles", prewarm: 96);
        var weapon = player.GetComponent<Weapon>()!;
        weapon.BulletPool = bullets;

        // Pool wiring: every bullet needs to know how to return itself.
        foreach (var a in scene.GetLayer("projectiles")!.Actors)
            if (a.GetComponent<Bullet>() is { } b) b.Pool = bullets;

        // Waves
        var director = new Actor("WaveDirector");
        var waves = director.AddComponent<WaveDirector>();
        waves.Game = game;
        game.Waves = waves;
        scene.AddActor(director);

        // UI
        var canvas = game.CreateCanvas(scene, "HUD");
        game.Hud = new Hud(game, canvas, player.GetComponent<Health>()!, waves);

        // Between waves: shop
        waves.WaveCleared += wave => UpgradeScreen.Show(game, canvas);

        // Death: game over
        player.GetComponent<Health>()!.Died += () =>
        {
            Progress.CommitHighScore();
            GameOverScreen.Show(game, canvas);
        };

        game.Music.Play("Assets/Audio/arena.wav");
    }
}
```

One wrinkle worth noting: the bullets in the pool are created during
`ActorPool`'s prewarm, so their `Pool` reference has to be assigned afterwards.
An alternative is an `onRent` callback on the underlying `ObjectPool<T>`.

## Step 7 — The upgrade screen

`MyGame/UI/UpgradeScreen.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using MyGame.Systems;

namespace MyGame.UI;

public static class UpgradeScreen
{
    public static void Show(Game game, Canvas canvas)
    {
        Time.TimeScale = 0f;                 // freeze gameplay; UI stays live

        var panel = canvas.AddWidget<Panel>();
        panel.Position          = new Vector2(390, 160);
        panel.Size              = new Vector2(500, 400);
        panel.BackgroundTexture = game.WhiteTexture;
        panel.BackgroundColor   = new Color(14, 16, 28, 242);
        panel.LayoutMode        = PanelLayoutMode.Vertical;
        panel.Padding           = 10f;

        var header = new Label
        {
            Text      = $"WAVE {Progress.Wave - 1} CLEARED",
            Size      = new Vector2(480, 36),
            Alignment = TextAlignment.Center,
            TextColor = new Color(255, 220, 120),
        };
        panel.AddChild(header);

        var crumbs = new Label
        {
            Size = new Vector2(480, 28), Alignment = TextAlignment.Center,
            TextColor = Color.White,
        };
        panel.AddChild(crumbs);

        void Refresh() => crumbs.Text = $"{Progress.Crumbs} crumbs";
        Refresh();

        AddUpgrade(game, panel, "Fire Rate", () => Progress.FireRateLevel,
                   () => Progress.FireRateLevel++, Refresh);
        AddUpgrade(game, panel, "Damage",    () => Progress.DamageLevel,
                   () => Progress.DamageLevel++,   Refresh);
        AddUpgrade(game, panel, "Speed",     () => Progress.SpeedLevel,
                   () => { Progress.SpeedLevel++; ApplySpeed(game); }, Refresh);
        AddUpgrade(game, panel, "Max Health",() => Progress.HealthLevel,
                   () => { Progress.HealthLevel++; ApplyHealth(game); }, Refresh);

        panel.AddChild(game.MakeButton("Next Wave", Vector2.Zero, new Vector2(480, 52), () =>
        {
            canvas.RemoveWidget(panel);
            Time.TimeScale = 1f;
        }));
    }

    private static void AddUpgrade(Game game, Panel panel, string name,
                                   Func<int> level, Action buy, Action refresh)
    {
        Button? btn = null;

        void UpdateLabel()
        {
            int cost = Progress.UpgradeCost(level());
            btn!.Text = $"{name}  Lv{level()}   {cost} crumbs";
            btn.Interactable = Progress.Crumbs >= cost;
            btn.TextColor = btn.Interactable ? Color.White : Color.Gray;
        }

        btn = game.MakeButton("", Vector2.Zero, new Vector2(480, 46), () =>
        {
            int cost = Progress.UpgradeCost(level());
            if (Progress.Crumbs < cost) return;

            Progress.Crumbs -= cost;
            buy();
            Sfx.Play("Assets/Audio/upgrade.wav");
            UpdateLabel();
            refresh();
        });

        UpdateLabel();
        panel.AddChild(btn);
    }

    private static void ApplySpeed(Game game)
    {
        var player = game.SceneManager.ActiveScene?.FindByTag("Player").FirstOrDefault();
        if (player?.GetComponent<Components.TopDownMovement>() is { } m)
            m.MaxSpeed = Progress.MoveSpeed;
    }

    private static void ApplyHealth(Game game)
    {
        var player = game.SceneManager.ActiveScene?.FindByTag("Player").FirstOrDefault();
        if (player?.GetComponent<Components.Health>() is { } h)
        {
            h.Max = Progress.MaxHealth;
            h.Heal(25);
        }
    }
}
```

`Time.TimeScale = 0` freezes gameplay while input keeps running on unscaled
time, so the shop is usable with no special casing.

## Step 8 — The HUD

`MyGame/UI/Hud.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using MyGame.Components;
using MyGame.Systems;

namespace MyGame.UI;

public sealed class Hud
{
    private readonly ProgressBar _health;
    private readonly Label       _stats;
    private readonly Label       _wave;

    public Hud(Game game, Canvas canvas, Health playerHealth, WaveDirector waves)
    {
        _health = canvas.AddWidget<ProgressBar>();
        _health.Position          = new Vector2(20, 20);
        _health.Size              = new Vector2(260, 20);
        _health.MaxValue          = playerHealth.Max;
        _health.Value             = playerHealth.Current;
        _health.BackgroundTexture = game.WhiteTexture;
        _health.FillTexture       = game.WhiteTexture;
        _health.BackgroundColor   = new Color(28, 30, 44);
        _health.FillColor         = new Color(110, 220, 140);

        _stats = canvas.AddWidget<Label>();
        _stats.Position  = new Vector2(0, 20);
        _stats.Size      = new Vector2(1260, 24);
        _stats.Alignment = TextAlignment.Right;

        _wave = canvas.AddWidget<Label>();
        _wave.Position  = new Vector2(0, 48);
        _wave.Size      = new Vector2(1260, 24);
        _wave.Alignment = TextAlignment.Right;
        _wave.TextColor = new Color(180, 190, 220);

        playerHealth.Changed += (current, max) =>
        {
            _health.MaxValue  = max;
            _health.Value     = current;
            _health.FillColor = current > max * 0.5f  ? new Color(110, 220, 140)
                              : current > max * 0.25f ? new Color(235, 195, 90)
                              :                         new Color(225, 90, 90);
        };

        Refresh();
    }

    public void Refresh()
    {
        _stats.Text = $"Score {Progress.Score:N0}     Crumbs {Progress.Crumbs}";
        _wave.Text  = $"Wave {Progress.Wave}";
    }
}
```

## Step 9 — Game over

`MyGame/UI/GameOverScreen.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using MyGame.Systems;

namespace MyGame.UI;

public static class GameOverScreen
{
    public static void Show(Game game, Canvas canvas)
    {
        Time.TimeScale = 0f;

        var dim = canvas.AddWidget<Image>();
        dim.Texture = game.WhiteTexture;
        dim.Tint    = new Color(0, 0, 0, 190);
        dim.Size    = new Vector2(game.Config.WindowWidth, game.Config.WindowHeight);

        var panel = canvas.AddWidget<Panel>();
        panel.Position          = new Vector2(440, 220);
        panel.Size              = new Vector2(400, 300);
        panel.BackgroundTexture = game.WhiteTexture;
        panel.BackgroundColor   = new Color(16, 18, 30, 244);
        panel.LayoutMode        = PanelLayoutMode.Vertical;
        panel.Padding           = 12f;

        panel.AddChild(new Label
        {
            Text = "CRUMBLED", Size = new Vector2(376, 44),
            Alignment = TextAlignment.Center, TextColor = new Color(235, 110, 90),
        });

        panel.AddChild(new Label
        {
            Text = $"Score {Progress.Score:N0}   Wave {Progress.Wave}",
            Size = new Vector2(376, 28),
            Alignment = TextAlignment.Center, TextColor = Color.White,
        });

        panel.AddChild(new Label
        {
            Text = Progress.IsNewHigh ? "NEW HIGH SCORE" : $"Best {Progress.HighScore:N0}",
            Size = new Vector2(376, 28),
            Alignment = TextAlignment.Center,
            TextColor = Progress.IsNewHigh ? new Color(255, 220, 120) : Color.Gray,
        });

        panel.AddChild(game.MakeButton("Retry", Vector2.Zero, new Vector2(376, 52), () =>
        {
            Time.TimeScale = 1f;
            Scenes.ArenaScene.Load(game);
        }));

        panel.AddChild(game.MakeButton("Main Menu", Vector2.Zero, new Vector2(376, 52), () =>
        {
            Time.TimeScale = 1f;
            Scenes.MainMenuScene.Load(game);
        }));
    }
}
```

Note `Time.TimeScale = 1f` **before** loading the next scene. Leaving it at zero
would freeze the new scene too — an easy bug to ship.

## Step 10 — The main menu

```csharp
public static class MainMenuScene
{
    public static void Load(Game game)
    {
        var scene = game.SceneManager.CreateScene("MainMenu");

        var camActor = new Actor("Main Camera") { Tag = "Camera" };
        camActor.Transform.Position = new Vector2(640, 360);
        game.SetCamera(camActor.AddComponent<Camera2D>());
        scene.AddActor(camActor);

        var canvas = game.CreateCanvas(scene, "MenuUI");

        var title = canvas.AddWidget<Label>();
        title.Text      = "BISCUIT BLASTER";
        title.Position  = new Vector2(0, 140);
        title.Size      = new Vector2(1280, 64);
        title.Alignment = TextAlignment.Center;
        title.TextColor = new Color(255, 220, 120);

        var best = canvas.AddWidget<Label>();
        best.Text      = $"Best: {Progress.HighScore:N0}";
        best.Position  = new Vector2(0, 210);
        best.Size      = new Vector2(1280, 28);
        best.Alignment = TextAlignment.Center;
        best.TextColor = new Color(160, 170, 200);

        var panel = canvas.AddWidget<Panel>();
        panel.Position          = new Vector2(480, 280);
        panel.Size              = new Vector2(320, 220);
        panel.BackgroundTexture = game.WhiteTexture;
        panel.BackgroundColor   = new Color(0, 0, 0, 140);
        panel.LayoutMode        = PanelLayoutMode.Vertical;
        panel.Padding           = 12f;

        panel.AddChild(game.MakeButton("Play", Vector2.Zero, new Vector2(296, 56),
                                       () => ArenaScene.Load(game)));
        panel.AddChild(game.MakeButton("Options", Vector2.Zero, new Vector2(296, 56),
                                       () => OptionsPanel.Show(game, canvas, panel)));
        panel.AddChild(game.MakeButton("Quit", Vector2.Zero, new Vector2(296, 56),
                                       game.Exit));

        game.Music.Play("Assets/Audio/menu.wav");
    }
}
```

## Step 11 — Balance

Play it. Then tune, in this order:

1. **Time to first death.** Too fast and it is punishing; too slow and it is
   boring. Aim for wave 4–6 on a first attempt.
2. **Crumb economy.** The player should afford roughly one upgrade per wave
   early on, and face real choices later. `UpgradeCost` and the drop rate are the
   two dials.
3. **Enemy speed vs player speed.** Enemies must be catchable-up-to but not
   outrunnable forever. `70f + wave * 5f` against a base `260f` gives a long
   ramp.
4. **Fire rate ceiling.** `0.28 × 0.85^level` reaches ~0.09 s at level 10 —
   check that it still feels like shooting rather than a hose.

Use the [tuning-file pattern](17-editor-workflow.md#fast-iteration-without-the-editor)
so you can rebalance without rebuilding.

## Step 12 — Polish

Everything from [Tutorial 9](09-animation-and-tweens.md), at the right moments:

| Moment | Effect |
|---|---|
| Enemy dies | `Effects.Explosion` + `Juice.Shake(0.25f, 0.15f)` |
| Player hit | `Juice.Flash` + `Juice.HitStop(0.08f, 0.05f)` + shake |
| Wave cleared | a `Label` that tweens in and out |
| Upgrade bought | `Juice.Pop` on the button + a sound |
| Firing | `Effects.Muzzle` + pitch-varied `Sfx.Play` |
| Low health | a red vignette that pulses with `Time.TimeSinceStartup` |

Wave-cleared banner:

```csharp
waves.WaveCleared += wave =>
{
    var banner = canvas.AddWidget<Label>();
    banner.Text      = $"WAVE {wave} CLEARED";
    banner.Position  = new Vector2(0, 300);
    banner.Size      = new Vector2(1280, 48);
    banner.Alignment = TextAlignment.Center;
    banner.TextColor = new Color(255, 220, 120);
    banner.Opacity   = 0f;

    Tween.Create()
         .TweenValue(() => banner.Opacity, v => banner.Opacity = v, 1f, 0.25f, EaseType.OutQuad)
         .Delay(1.2f)
         .TweenValue(() => banner.Opacity, v => banner.Opacity = v, 0f, 0.4f, EaseType.InQuad)
         .OnComplete(() => canvas.RemoveWidget(banner))
         .Play();
};
```

## Step 13 — Ship it

Follow [Tutorial 18](18-shipping.md):

```bash
./build.sh windows-x64 win-x64
./build.sh linux-x64   linux-x64
./build.sh macos-arm64 osx-arm64
```

Then work the [test pass](18-shipping.md#12-testing-the-build) on a machine that
is not your dev box.

---

## What you built

- A complete game loop: menu → play → upgrade → die → high score → retry
- Pooled bullets, swarming enemies, coroutine-driven waves
- An economy with meaningful upgrade choices
- Persistent high score, settings and a per-user data directory
- Game feel throughout
- Cross-platform distributable builds

## Where to go next

**Make it yours.** Add weapon types, elite enemies, a boss every fifth wave,
pickups that drop instead of auto-collecting, a second player on the gamepad.

**Then rebuild it in 3D.** Everything transfers except the rendering and physics
— [Tutorial 13](13-3d-basics.md).

**Then make it multiplayer.** The wave director becomes server-authoritative,
players replicate, shooting goes through a `ServerRpc` —
[Tutorial 16](16-multiplayer.md).

**Then read the engine.** You now know what every subsystem does; the source is
short and well commented. The [wiki](../wiki/README.md) maps it, and
[page 21](../wiki/21-gotchas.md) lists everything that will surprise you.

---

**Back to:** [Tutorials index](README.md) · [Wiki](../wiki/README.md)
