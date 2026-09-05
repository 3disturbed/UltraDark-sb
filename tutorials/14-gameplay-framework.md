# Tutorial 14 — The Gameplay Framework

**You will build:** a deathmatch with a game mode, spawn points, respawns, a
scoreboard and persistent session state. **Time:** ~45 minutes.

Builds on [Tutorial 13](13-3d-basics.md), but the ideas apply to 2D too.

---

## 1. What this layer is for

`SexyBiscuit.Engine.Gameplay` is an Unreal-shaped layer over the actor/component
core:

```
GameInstance          one per process, outlives every scene
└── GameMode          the rules of the current match (an Actor)
    ├── GameState     match state and the player list (an Actor)
    └── PlayerController[]              (Actors)
        ├── PlayerState                score, name, team (an Actor)
        └── Pawn                       the thing in the world it possesses
            └── Character              a Pawn with a CharacterController3D
```

Use it when your game has a match, players, respawns and a scoreboard. Skip it
for a puzzle game or a linear single-player experience — plain actors are fine
and simpler.

The core idea worth stealing even if you skip the rest: **a `Controller`
possesses a `Pawn`**. Player and AI drive the same character class through the
same interface, so an enemy and a player can be literally the same pawn type.

## 2. GameInstance — session state

The one object that survives scene changes. Save data, the player profile,
matchmaking state.

`MyGame/MyGameInstance.cs`:

```csharp
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Save;

namespace MyGame;

public sealed class MyGameInstance : GameInstance
{
    public string PlayerName = "Player";
    public int    TotalKills;
    public int    MatchesPlayed;

    public override void OnStart()
    {
        PlayerName    = PlayerPrefs.GetString("player.name", "Player");
        TotalKills    = PlayerPrefs.GetInt("stats.kills");
        MatchesPlayed = PlayerPrefs.GetInt("stats.matches");
    }

    public override void OnShutdown()
    {
        PlayerPrefs.SetString("player.name", PlayerName);
        PlayerPrefs.SetInt("stats.kills", TotalKills);
        PlayerPrefs.SetInt("stats.matches", MatchesPlayed);
        PlayerPrefs.Save();
    }

    public override void Tick(float dt) { }
}
```

Install it through `EngineConfig`:

```csharp
public Game() : base(new EngineConfig
{
    WindowTitle         = "Arena",
    GameInstanceFactory = () => new MyGameInstance(),
})
{ }
```

`SBEngine.Initialize` runs the factory, then `InternalInit()` and
`InternalStart()` — **all before `OnEngineReady`**. `Tick` runs every frame
before scenes update; `OnShutdown` runs in `UnloadContent`.

```csharp
var gi = (MyGameInstance)GameInstance.Current;    // or SBEngine.Instance.GameInstance
gi.TotalKills++;

GameInstance.Current.WorldChanged.Add(scene => Debug.WriteLine($"now in {scene.Name}"));
```

`Started`, `ShuttingDown` and `WorldChanged` are `SBEvent`s — see section 8.

## 3. Subsystems

Modular services with a real lifetime, scoped to the session or to a scene.
Better than a `static` singleton: testable, discoverable, and torn down properly.

```csharp
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Save;

namespace MyGame.Systems;

public sealed class AudioSettings : GameInstanceSubsystem
{
    public float Master { get; private set; } = 1f;

    public override bool ShouldCreate() => true;

    public override void Initialize()
    {
        Master = PlayerPrefs.GetFloat("vol.master", 1f);
        Apply();
    }

    public override void Deinitialize() => PlayerPrefs.Save();

    public void Set(float v)
    {
        Master = v;
        PlayerPrefs.SetFloat("vol.master", v);
        Apply();
    }

    private void Apply() => SBEngine.Instance.Audio.Master.Volume = Master;
}
```

```csharp
var audio = GameInstance.Current.GetSubsystem<AudioSettings>();
audio?.Set(0.6f);

foreach (var s in GameInstance.Current.Subsystems.All)
    Debug.WriteLine($"{s.GetType().Name}: {s.IsInitialized}");
```

`Get<T>()` creates the subsystem on first request (when `ShouldCreate()` returns
true) and caches it. `WorldSubsystem` is the scene-scoped variant, with a
`World` property.

## 4. Pawn and Character

A `Pawn` is something a `Controller` can drive.

```csharp
pawn.Controller;                  // Controller?
pawn.IsPlayerControlled;          // Controller is PlayerController
pawn.ControlRotation;             // (pitch, yaw, roll) degrees
pawn.UseControllerRotationYaw;

pawn.AddMovementInput(worldDirection, scale);   // accumulate intent
pawn.ConsumeMovementInput();                    // take it, once per frame
pawn.PendingMovementInput;

pawn.AddControllerYawInput(degrees);
pawn.AddControllerPitchInput(degrees, minPitch, maxPitch);
```

The **accumulate/consume** split is the important part. A controller adds
intent; the pawn decides how movement happens. That is what lets the same pawn
work under a player controller or an AI controller with no changes.

`Character` is a `Pawn` already wired to a `CharacterController3D`:

```csharp
using SexyBiscuit.Engine.Gameplay;

var hero = new Character("Hero")
{
    WalkSpeed        = 5f,
    SprintMultiplier = 1.8f,
    OrientToMovement = true,
    RotationSpeed    = 720f,      // degrees/second
    EyeHeight        = 1.7f,
};

hero.Jumped.Add(() => Sfx.Play("Assets/Audio/jump.wav"));
hero.Landed.Add(() => Juice.Shake(0.25f, 0.15f));
```

```csharp
hero.IsSprinting = true;        // applies WalkSpeed × SprintMultiplier
hero.IsGrounded;
hero.Movement;                  // the CharacterController3D
hero.Transform3D;               // cached
hero.Jump();                    // virtual — override for a double jump
```

`Movement` and `Transform3D` are resolved in `OnStart`, so they are valid from
your first `Update`, not from the constructor.

A custom pawn is just as easy:

```csharp
public sealed class Drone : Pawn
{
    public float Speed = 8f;

    protected override void Update(float dt)
    {
        Vector3 move = ConsumeMovementInput();
        if (move.LengthSquared() <= 0f) return;

        var t = GetComponent<Transform3D>()!;
        t.Position += SBMath.SafeNormalize(move) * Speed * dt;
    }
}
```

## 5. PlayerController

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Input;

namespace MyGame.Gameplay;

public sealed class ArenaPlayerController : PlayerController
{
    protected override void SetupInput(InputManager input)
    {
        // Called once in OnStart. Load a custom map, bind UI, etc.
    }

    protected override void OnPlayerTick(Pawn pawn, InputManager input, float dt)
    {
        float x = input.GetAxis("MoveX");
        float y = input.GetAxis("MoveY");

        // Movement relative to where the player is looking.
        var yaw     = Matrix.CreateRotationY(MathHelper.ToRadians(pawn.ControlRotation.Y));
        var forward = Vector3.Transform(Vector3.Forward, yaw);
        var right   = Vector3.Transform(Vector3.Right,   yaw);

        pawn.AddMovementInput(forward, y);
        pawn.AddMovementInput(right,   x);

        if (pawn is Character c)
        {
            c.IsSprinting = input.IsHeld("Dodge");
            if (input.IsPressed("Jump")) c.Jump();
        }

        if (input.IsPressed("Attack")) Fire(pawn);
    }

    private void Fire(Pawn pawn)
    {
        var ray = GetCursorRay();
        if (ray is not { } r) return;

        if (PhysicsSystem3D.Instance.Raycast(r.origin, r.direction, 200f, out var hit))
            hit.Actor?.GetComponent<Health>()?.Damage(20, pawn);
    }
}
```

```csharp
pc.PlayerIndex     = 0;
pc.PlayerState     = state;
pc.ViewCamera      = camera3D;
pc.InputEnabled    = false;          // disable during a cutscene
pc.LookSensitivity = 0.15f;
pc.GamepadLookSpeed = 180f;
pc.InvertLookY     = true;
```

The base `Update` applies look input to the pawn's `ControlRotation` via
`ApplyLookInput`, then calls `OnPlayerTick`. **Override `OnPlayerTick`, not
`Update`**, so you keep that behaviour. Override `ApplyLookInput` to change how
looking feels.

## 6. GameMode

The rules of the current match. It is an `Actor`, so you add it to the scene.

```csharp
using SexyBiscuit.Engine.Gameplay;
using MyGame.Systems;

namespace MyGame.Gameplay;

public sealed class DeathmatchMode : GameMode
{
    public DeathmatchMode()
    {
        PawnFactory             = () => new Character("Fighter") { WalkSpeed = 6f };
        PlayerControllerFactory = () => new ArenaPlayerController();
        SpawnLayer              = "default";
        RespawnDelay            = 3f;
        ScoreToWin              = 20;
        TimeLimit               = 300f;
        AutoStartLocalPlayer    = true;
    }

    protected override void OnPreStart()
    {
        // Runs before the match is set up: seed the level, load the map.
    }

    protected override void OnMatchStart()
    {
        ((Game)SBEngine.Instance).Music.Play("Assets/Audio/arena.wav");
    }

    protected override void OnMatchEnd(PlayerState? winner)
    {
        Debug.WriteLine(winner == null ? "Draw" : $"{winner.PlayerName} wins");

        if (GameInstance.Current is MyGameInstance gi) gi.MatchesPlayed++;
    }
}
```

```csharp
var scene = SceneManager.CreateScene("Arena");
scene.AddActor(new DeathmatchMode());

GameMode.Current;                                        // the active mode
GameMode.Current!.StartMatch();
GameMode.Current!.EndMatch(winner);
GameMode.Current!.MatchStarted.Add(ShowCountdown);
GameMode.Current!.MatchEnded.Add(w => ShowResults(w));

PlayerController pc = GameMode.Current!.SpawnPlayer(playerIndex: 1);   // local co-op
```

Override `SpawnDefaultPawnFor(controller)` for class-based pawns, and
`ChoosePlayerStart(controller)` for spawn selection.

## 7. Spawn points

```csharp
using SexyBiscuit.Engine.Gameplay;

var spawn = new Actor("Spawn_Red");
spawn.AddComponent<Transform3D>().Position = new Vector3(-10, 0, 0);
spawn.AddComponent<PlayerStart>().TeamId = 0;
scene.AddActor(spawn);
```

`PlayerStart.All` is the registry `ChoosePlayerStart` reads; `Position` and
`Yaw` come from the actor's `Transform3D`.

Farthest-from-enemies selection, which is what most shooters actually do:

```csharp
protected override PlayerStart? ChoosePlayerStart(PlayerController controller)
{
    var enemies = Controllers
        .Where(c => c != controller && c.ControlledPawn != null)
        .Select(c => c.ControlledPawn!.GetComponent<Transform3D>()!.Position)
        .ToList();

    if (enemies.Count == 0)
        return PlayerStart.All.FirstOrDefault();

    return PlayerStart.All
        .OrderByDescending(s => enemies.Min(e => Vector3.Distance(s.Position, e)))
        .FirstOrDefault();
}
```

## 8. GameState, PlayerState, SBEvent

```csharp
GameState gs = GameMode.Current!.GameState;
gs.ElapsedTime;
gs.MatchState;                  // WaitingToStart, InProgress, …
gs.Players;                     // List<PlayerState>
gs.MatchStateChanged.Add(st => Debug.WriteLine($"match state: {st}"));

foreach (var p in gs.GetScoreboard())        // sorted
    Debug.WriteLine($"{p.PlayerName}: {p.Score}");
```

```csharp
var ps = pc.PlayerState!;
ps.PlayerName = "Ada";
ps.TeamId     = 0;
ps.PingMs     = NetworkManager.Instance?.Ping ?? 0;
ps.IsBot      = false;

ps.AddScore(1);
ps.ScoreChanged.Add(score => scoreLabel.Text = score.ToString());
```

The framework broadcasts through `SBEvent` / `SBEvent<T>` rather than C#
`event`s:

```csharp
var onDeath = new SBEvent<Actor>();
onDeath.Add(a => Debug.WriteLine($"{a.Name} died"));
onDeath.Broadcast(actor);
onDeath.Remove(handler);
onDeath.Clear();

onDeath += handler;      // operator sugar
onDeath -= handler;
```

Two properties that matter:

- **Broadcast iterates a snapshot**, so a handler that unsubscribes mid-broadcast
  still receives the current call and none after it.
- **An exception in one handler is logged and swallowed**, so the rest still
  run. Convenient — and a reason not to rely on exceptions escaping a broadcast.

`SBEvent` holds strong references. Always `Remove` or `Clear` in teardown.

## 9. A scoreboard

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

namespace MyGame.UI;

public sealed class Scoreboard
{
    private readonly Panel _panel;
    private readonly List<Label> _rows = new();
    private readonly GameState _state;

    public Scoreboard(Game game, Canvas canvas, GameState state)
    {
        _state = state;

        _panel = canvas.AddWidget<Panel>();
        _panel.Position          = new Vector2(340, 140);
        _panel.Size              = new Vector2(600, 420);
        _panel.BackgroundTexture = game.WhiteTexture;
        _panel.BackgroundColor   = new Color(10, 12, 22, 225);
        _panel.LayoutMode        = PanelLayoutMode.Vertical;
        _panel.Padding           = 8f;
        _panel.Visible           = false;

        _panel.AddChild(new Label
        {
            Text = "SCOREBOARD", Size = new Vector2(584, 36),
            Alignment = TextAlignment.Center, TextColor = Color.White,
        });

        for (int i = 0; i < 8; i++)
        {
            var row = new Label { Size = new Vector2(584, 28), TextColor = Color.LightGray };
            _rows.Add(row);
            _panel.AddChild(row);
        }

        state.MatchStateChanged.Add(_ => Refresh());
    }

    public void SetVisible(bool visible)
    {
        _panel.Visible = visible;
        if (visible) Refresh();
    }

    private void Refresh()
    {
        var ordered = _state.GetScoreboard().ToList();
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i < ordered.Count)
            {
                var p = ordered[i];
                _rows[i].Text = $"{i + 1,2}.  {p.PlayerName,-20} {p.Score,4}   {p.PingMs,3}ms";
                _rows[i].TextColor = p.IsBot ? Color.Gray : Color.White;
            }
            else
            {
                _rows[i].Text = "";
            }
        }
    }
}
```

```csharp
if (Input.IsHeld("Scoreboard")) _scoreboard.SetVisible(true);
else                            _scoreboard.SetVisible(false);
```

## 10. Respawns

```csharp
using System.Collections;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;

namespace MyGame.Gameplay;

/// <summary>Handles death: score the killer, then respawn after a delay.</summary>
public sealed class RespawnHandler : Component
{
    public float Delay = 3f;

    private PlayerController? _controller;

    public override void Start()
    {
        _controller = Actor as PlayerController
                   ?? (Actor as Pawn)?.Controller as PlayerController;

        var health = GetComponent<Health>();
        if (health != null) health.Died += OnDied;
    }

    private void OnDied()
    {
        if (_controller == null) return;
        SBEngine.Instance.Coroutines.Start(Respawn(), this);
    }

    private IEnumerator Respawn()
    {
        var pawn = _controller!.ControlledPawn;
        _controller.UnPossess();
        pawn?.Destroy();

        yield return new WaitForSeconds(Delay);

        var fresh = GameMode.Current?.SpawnDefaultPawnFor(_controller);
        if (fresh != null) _controller.Possess(fresh);
    }

    public override void OnDestroy()
        => SBEngine.Instance.Coroutines.StopAllFor(this);
}
```

Un-possessing before destroying the pawn is the part people forget —
`Controller.OnDestroy` un-possesses automatically, but a pawn destroyed while
still possessed leaves the controller pointing at a dead actor for a frame.

## 11. Bots

Because AI and players share the `Controller`/`Pawn` split, a bot is a different
controller on the same pawn:

```csharp
using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Gameplay;

public static PlayerState SpawnBot(GameMode mode, string name)
{
    var scene = mode.Scene!;          // valid once the GameMode actor is in a scene

    var pawn = new Character(name) { WalkSpeed = 5f };
    scene.AddActor(pawn);

    var ai = new AIController { SightRadius = 25f, FieldOfView = 120f };
    scene.AddActor(ai);
    ai.Possess(pawn);

    var state = new PlayerState { PlayerName = name, IsBot = true };
    scene.AddActor(state);
    mode.GameState.AddPlayer(state);

    return state;
}
```

The bot's behaviour tree is [Tutorial 15](15-ai.md). What matters here is that
the pawn, the spawn logic and the scoreboard are all unchanged.

## 12. Putting it together

```csharp
public sealed class ArenaGame : SBEngine
{
    public ArenaGame() : base(new EngineConfig
    {
        WindowTitle         = "Arena",
        Enable3D            = true,
        GameInstanceFactory = () => new MyGameInstance(),
    }) { }

    protected override void OnEngineReady()
    {
        Renderer3D.AmbientLight = new Color(38, 42, 54);
        LoadArena();
    }

    private void LoadArena()
    {
        var scene = SceneManager.CreateScene("Arena");

        var cam = new Actor("Main Camera") { Tag = "MainCamera3D" };
        cam.AddComponent<Transform3D>().Position = new Vector3(0, 4, 10);
        cam.AddComponent<Camera3D>();
        scene.AddActor(cam);

        var sun = new Actor("Sun");
        sun.AddComponent<Transform3D>().LookAt(new Vector3(0.3f, -1f, 0.2f));
        sun.AddComponent<Light3D>().Type = LightType.Directional;
        scene.AddActor(sun);

        PhysicsSystem3D.Instance.AddStaticBox(new Vector3(0, -0.5f, 0),
                                              Quaternion.Identity,
                                              new Vector3(40f, 0.5f, 40f));

        foreach (var (pos, team) in new[]
                 { (new Vector3(-12, 0, 0), 0), (new Vector3(12, 0, 0), 1),
                   (new Vector3(0, 0, -12), 0), (new Vector3(0, 0, 12), 1) })
        {
            var s = new Actor("Spawn");
            s.AddComponent<Transform3D>().Position = pos;
            s.AddComponent<PlayerStart>().TeamId = team;
            scene.AddActor(s);
        }

        // GameMode spawns the local player itself when AutoStartLocalPlayer is set.
        var mode = new DeathmatchMode();
        scene.AddActor(mode);

        for (int i = 0; i < 3; i++) SpawnBot(mode, $"Bot {i + 1}");
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);
        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        Renderer3D.Render(scene);
        Renderer2D.RenderScene(SpriteBatch, scene, null);

        SpriteBatch.Begin();
        scene.GetLayer("ui")?.Draw(SpriteBatch);
        SpriteBatch.End();
    }
}
```

---

## Checkpoint

You have:

- `GameInstance` for session state, with subsystems for services
- `GameMode` driving spawns, respawns and match end
- `Pawn`/`Controller` possession shared by players and bots
- A scoreboard driven by `GameState` and `SBEvent`

## Exercises

1. Add a team-deathmatch mode: score by `TeamId`, and spawn on team spawn points.
2. Add a `MatchTimer` HUD label reading `GameState.ElapsedTime` against `TimeLimit`.
3. Add a `WorldSubsystem` that tracks pickups and respawns them on a timer.

---

**Next:** [Tutorial 15 — AI](15-ai.md)
