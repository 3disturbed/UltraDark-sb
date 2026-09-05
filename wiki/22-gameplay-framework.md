# 22. Gameplay Framework

Namespace: `SexyBiscuit.Engine.Gameplay`

An Unreal-shaped layer over the actor/component core. It gives you a place to
put the parts of a game that always exist — session state, match rules, players,
possession, spawning — without inventing them per project.

```
GameInstance          one per process, outlives every scene
└── GameMode          the rules of the current match (an Actor)
    ├── GameState     replicated match state (an Actor)
    └── PlayerController[]   (Controllers, each an Actor)
        ├── PlayerState      score, name, team (an Actor)
        └── Pawn             the thing in the world it possesses (an Actor)
            └── Character    a Pawn with a CharacterController3D
```

Every one of these except `GameInstance` and `Subsystem` is an `Actor`, so they
live in the scene, receive `Update`, and can carry components.

Use it when your game has a match, players, respawns and a scoreboard. Skip it
for a puzzle game or a linear single-player experience — plain actors are fine.

---

## GameInstance

The one object that survives scene changes. The natural home for the player
profile, save data, matchmaking state and settings.

```csharp
public sealed class MyGameInstance : GameInstance
{
    public string PlayerName = "Player";
    public int    Coins;

    public override void OnStart()
    {
        Coins = PlayerPrefs.GetInt("coins");
        PlayerName = PlayerPrefs.GetString("name", "Player");
    }

    public override void OnShutdown()
    {
        PlayerPrefs.SetInt("coins", Coins);
        PlayerPrefs.SetString("name", PlayerName);
        PlayerPrefs.Save();
    }

    public override void Tick(float dt) { }
}
```

Install it through `EngineConfig`:

```csharp
SBEngine.Run(new EngineConfig
{
    WindowTitle         = "My Game",
    GameInstanceFactory = () => new MyGameInstance(),
});
```

`SBEngine.Initialize` calls the factory (or constructs a plain `GameInstance`),
then `InternalInit()` and `InternalStart()` — all **before** `OnEngineReady`.
`Tick` runs every frame before scenes update. `OnShutdown` runs in
`UnloadContent`.

```csharp
var gi = (MyGameInstance)GameInstance.Current;      // or SBEngine.Instance.GameInstance
gi.Coins += 10;

GameInstance.Current.Started.Add(() => Debug.WriteLine("session up"));
GameInstance.Current.ShuttingDown.Add(FlushTelemetry);
GameInstance.Current.WorldChanged.Add(scene => Debug.WriteLine($"now in {scene.Name}"));
```

Those three are [`SBEvent`](#sbevent) multicast delegates, not C# `event`s.

---

## Subsystems

Modular services with a managed lifetime, attached to a `GameInstance` (session
scope) or a `Scene` (world scope).

```csharp
public sealed class AudioSettingsSubsystem : GameInstanceSubsystem
{
    public float Master = 1f;

    public override bool ShouldCreate() => true;      // return false to skip creation

    public override void Initialize()
    {
        Master = PlayerPrefs.GetFloat("vol.master", 1f);
        SBEngine.Instance.Audio.Master.Volume = Master;
    }

    public override void Deinitialize() => PlayerPrefs.Save();

    public override void Tick(float dt) { }
}
```

```csharp
var settings = GameInstance.Current.GetSubsystem<AudioSettingsSubsystem>();
// equivalently: GameInstance.Current.Subsystems.Get<AudioSettingsSubsystem>()

foreach (Subsystem s in GameInstance.Current.Subsystems.All)
    Debug.WriteLine($"{s.GetType().Name} initialised: {s.IsInitialized}");
```

`SubsystemCollection.Get<T>()` creates the subsystem on first request (when
`ShouldCreate()` returns true) and caches it. `WorldSubsystem` is the
scene-scoped variant and exposes `World`.

Reach for a subsystem instead of a `static` singleton: it has a real lifetime,
it is testable, and it is discoverable through `Subsystems.All`.

---

## Pawn

Something in the world that a `Controller` can drive.

```csharp
public class Pawn : Actor
{
    public Controller? Controller { get; }
    public bool IsPlayerControlled { get; }        // Controller is PlayerController
    public bool IsControlled { get; }

    public Vector3 ControlRotation { get; set; }   // (pitch, yaw, roll) in degrees
    public bool UseControllerRotationYaw { get; set; } = true;

    public void AddMovementInput(Vector3 worldDirection, float scale = 1f);
    public Vector3 ConsumeMovementInput();
    public Vector3 PendingMovementInput { get; }

    public void AddControllerYawInput(float degrees);
    public void AddControllerPitchInput(float degrees, float minPitch = -89f, float maxPitch = 89f);

    public virtual void OnPossessed(Controller controller);
    public virtual void OnUnPossessed(Controller controller);
    public virtual Vector3 GetViewLocation();
}
```

The movement-input pattern is the important part: a controller **accumulates**
intent with `AddMovementInput`, and the pawn **consumes** it once per frame with
`ConsumeMovementInput`. That decouples "who decided to move" from "how movement
happens", so the same pawn works under a player controller or an AI controller.

```csharp
public sealed class Drone : Pawn
{
    public float Speed = 8f;

    protected override void Update(float dt)
    {
        Vector3 move = ConsumeMovementInput();
        if (move.LengthSquared() > 0f)
        {
            var t = GetComponent<Transform3D>()!;
            t.Position += SBMath.SafeNormalize(move) * Speed * dt;
        }
    }
}
```

---

## Character

A `Pawn` wired to a `CharacterController3D` — walking, sprinting, jumping and
orientation, ready to use.

```csharp
var hero = new Character("Hero")
{
    WalkSpeed        = 5f,
    SprintMultiplier = 1.8f,
    OrientToMovement = true,
    RotationSpeed    = 720f,      // degrees/second
    EyeHeight        = 1.7f,
};

hero.Jumped.Add(() => SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/jump.wav"));
hero.Landed.Add(() => camera.Shake(0.3f, 0.2f));

scene.AddActor(hero);
```

```csharp
hero.IsSprinting;                     // set it; WalkSpeed × SprintMultiplier is applied
hero.IsGrounded;                      // from Movement.IsGrounded
hero.Movement;                        // the CharacterController3D
hero.Transform3D;                     // cached
hero.Jump();                          // virtual — override for double jump
```

`Movement` and `Transform3D` are resolved in `OnStart`, so they are valid from
your first `Update`, not from the constructor.

---

## Controller and PlayerController

A `Controller` possesses a `Pawn`.

```csharp
controller.Possess(pawn);
controller.UnPossess();
controller.ControlledPawn;

controller.Possessed.Add(p   => Debug.WriteLine($"possessed {p.Name}"));
controller.UnPossessed.Add(p => Debug.WriteLine($"released {p.Name}"));
```

`Controller.OnDestroy` un-possesses automatically. Override `OnPossess` /
`OnUnPossess` for setup and teardown.

`PlayerController` adds input handling and a view camera:

```csharp
public sealed class MyPlayerController : PlayerController
{
    protected override void SetupInput(InputManager input)
    {
        // called once in OnStart — load a custom action map, bind UI, etc.
    }

    protected override void OnPlayerTick(Pawn pawn, InputManager input, float dt)
    {
        float x = input.GetAxis("MoveX");
        float y = input.GetAxis("MoveY");

        // Move relative to where the camera is looking.
        var yaw     = Matrix.CreateRotationY(MathHelper.ToRadians(pawn.ControlRotation.Y));
        var forward = Vector3.Transform(Vector3.Forward, yaw);
        var right   = Vector3.Transform(Vector3.Right,   yaw);

        pawn.AddMovementInput(forward, y);
        pawn.AddMovementInput(right,   x);

        if (input.IsPressed("Jump") && pawn is Character c) c.Jump();
        if (pawn is Character ch) ch.IsSprinting = input.IsHeld("Dodge");
    }
}
```

```csharp
pc.PlayerIndex     = 0;
pc.PlayerState     = state;
pc.ViewCamera      = camera3D;
pc.InputEnabled    = false;        // disable during cutscenes
pc.LookSensitivity = 0.15f;
pc.GamepadLookSpeed = 180f;
pc.InvertLookY     = true;

var ray = pc.GetCursorRay();       // (origin, direction)? for mouse picking
if (ray is { } r && PhysicsSystem3D.Instance.Raycast(r.origin, r.direction, 100f, out var hit))
    Select(hit.Actor);
```

The base `Update` applies look input to the possessed pawn's `ControlRotation`
via `ApplyLookInput` (override to change the feel), then calls `OnPlayerTick`.
Override `OnPlayerTick`, not `Update`, so you keep that behaviour.

---

## GameMode, GameState, PlayerState

`GameMode` is the rules of the current match. Add it to the scene like any actor.

```csharp
public sealed class DeathmatchMode : GameMode
{
    public DeathmatchMode()
    {
        PawnFactory             = () => new Character("Fighter") { WalkSpeed = 6f };
        PlayerControllerFactory = () => new MyPlayerController();
        ScoreToWin              = 20;
        TimeLimit               = 300f;
        RespawnDelay            = 3f;
        SpawnLayer              = "default";
        AutoStartLocalPlayer    = true;
    }

    protected override void OnPreStart() { /* before the match is set up */ }
    protected override void OnMatchStart() => ShowCountdown();
    protected override void OnMatchEnd(PlayerState? winner) => ShowResults(winner);
}
```

```csharp
var scene = SceneManager.CreateScene("Arena");
scene.AddActor(new DeathmatchMode());

GameMode.Current;                       // the active mode
GameMode.Current!.StartMatch();
GameMode.Current!.EndMatch(winner);
GameMode.Current!.MatchStarted.Add(StartMusic);
GameMode.Current!.MatchEnded.Add(w => Debug.WriteLine($"winner: {w?.PlayerName ?? "draw"}"));

PlayerController pc = GameMode.Current!.SpawnPlayer(playerIndex: 1);   // local co-op
```

Override `SpawnDefaultPawnFor(controller)` for per-class pawns, and
`ChoosePlayerStart(controller)` for spawn selection (team spawns, farthest-from-enemy,
and so on).

### PlayerStart

Mark spawn points with a component:

```csharp
var spawn = new Actor("Spawn_Red");
spawn.AddComponent<Transform3D>().Position = new Vector3(-10, 0, 0);
spawn.AddComponent<PlayerStart>().TeamId = 0;
scene.AddActor(spawn);
```

`PlayerStart.All` is the registry `ChoosePlayerStart` reads. `Position` and
`Yaw` come from the actor's `Transform3D`.

### GameState

```csharp
GameState gs = GameMode.Current!.GameState;
gs.ElapsedTime;
gs.MatchState;                       // MatchState enum: WaitingToStart, InProgress, …
gs.Players;                          // List<PlayerState>
gs.MatchStateChanged.Add(st => Debug.WriteLine($"match state: {st}"));

foreach (var p in gs.GetScoreboard())          // sorted
    Debug.WriteLine($"{p.PlayerName}: {p.Score}");
```

### PlayerState

```csharp
var ps = pc.PlayerState!;
ps.PlayerName = "Ada";
ps.PlayerId   = 3;
ps.TeamId     = 0;
ps.PingMs     = nm.Ping;
ps.IsBot      = false;

ps.AddScore(1);
ps.ScoreChanged.Add(score => hudLabel.Text = score.ToString());
```

`PlayerState` is the natural thing to replicate in a networked game — mark its
fields `[Replicated]` and give the actor a `NetworkObject`. See
[15. Networking](15-networking.md).

---

## SBEvent

The framework broadcasts through `SBEvent` / `SBEvent<T>` rather than C#
`event`s, because listeners frequently subscribe and unsubscribe during a
broadcast.

```csharp
var onDeath = new SBEvent<Actor>();

onDeath.Add(a => Debug.WriteLine($"{a.Name} died"));
onDeath.Broadcast(actor);
onDeath.Remove(handler);
onDeath.Clear();
int n = onDeath.Count;

onDeath += handler;      // operator sugar for Add
onDeath -= handler;      // operator sugar for Remove
```

Two properties that matter:

- **Broadcast iterates a snapshot.** A handler that unsubscribes during the
  broadcast still receives the current call, and none after it.
- **An exception in one handler is logged and swallowed**, so the remaining
  handlers still run. Convenient, and a reason to not rely on exceptions
  propagating out of a broadcast.

Always `Remove` or `Clear` in teardown — `SBEvent` holds strong references, and
a handler closing over a destroyed actor keeps it alive.

---

## Putting it together

```csharp
public sealed class ArenaGame : SBEngine
{
    public ArenaGame() : base(new EngineConfig
    {
        WindowTitle         = "Arena",
        GameInstanceFactory = () => new MyGameInstance(),
    }) { }

    protected override void OnEngineReady() => LoadArena();

    private void LoadArena()
    {
        var scene = SceneManager.CreateScene("Arena");

        // Camera
        var cam = new Actor("Main Camera") { Tag = "MainCamera3D" };
        cam.AddComponent<Transform3D>().Position = new Vector3(0, 4, 10);
        cam.AddComponent<Camera3D>();
        scene.AddActor(cam);

        // Light
        var sun = new Actor("Sun");
        sun.AddComponent<Transform3D>().LookAt(new Vector3(0.3f, -1f, 0.2f));
        sun.AddComponent<Light3D>().Type = LightType.Directional;
        scene.AddActor(sun);

        // Spawn points
        foreach (var (pos, team) in new[]
                 { (new Vector3(-8, 0, 0), 0), (new Vector3(8, 0, 0), 1) })
        {
            var s = new Actor("Spawn");
            s.AddComponent<Transform3D>().Position = pos;
            s.AddComponent<PlayerStart>().TeamId = team;
            scene.AddActor(s);
        }

        // Rules — GameMode spawns the local player itself.
        scene.AddActor(new DeathmatchMode());
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);
        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        Renderer3D.Render(scene);
        Renderer2D.RenderScene(SpriteBatch, scene, null);
    }
}
```

---

## Next

- [23. AI](23-ai.md)
- [15. Networking](15-networking.md)
