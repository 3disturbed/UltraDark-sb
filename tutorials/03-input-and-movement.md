# Tutorial 3 — Input & Movement

**You will build:** a character controlled on keyboard, mouse and gamepad, with
remappable actions and a small amount of game feel. **Time:** ~25 minutes.

Builds on [Tutorial 2](02-sprites-and-cameras.md).

---

## 1. Actions, not keys

`InputManager` is created and pumped by the engine — one of the few systems you
do not wire up. Reach it as `SBEngine.Instance.Input`, or just `Input` inside
your `Game` subclass.

You *can* read raw keys:

```csharp
if (Input.IsKeyDown(Keys.W)) { }
```

Don't, for gameplay. Use **actions**: named, device-agnostic, remappable.

```csharp
float x = Input.GetAxis("MoveX");     // −1..1
if (Input.IsPressed("Jump")) { }      // this frame
if (Input.IsHeld("Attack")) { }       // continuously
if (Input.IsReleased("Attack")) { }   // this frame
```

The default map is installed at startup:

| Action | Keyboard | Gamepad / Mouse |
|---|---|---|
| `MoveX` | `A`/`D`, `←`/`→` | left stick X |
| `MoveY` | `S`/`W`, `↓`/`↑` (inverted) | left stick Y (inverted) |
| `Jump` | `Space` | `A` |
| `Attack` | `Z` | mouse left, `X` |
| `Dodge` | `LeftShift` | `B` |
| `Interact` | `E` | `X` |
| `Pause` | `Escape` | `Start` |
| `CameraX` / `CameraY` | — | mouse delta, right stick |

Names match case-insensitively, and an unknown name returns `false` / `0f`
silently — a typo shows up as an unresponsive control, not a crash.

### The MoveY sign

`MoveY` is inverted so `W` gives `+1`. Screen Y increases *downward*, so
applying it to a position needs a minus:

```csharp
float mx = Input.GetAxis("MoveX");
float my = Input.GetAxis("MoveY");
Transform.Position += new Vector2(mx, -my) * speed * dt;
```

Get this backwards once and you will remember it forever.

## 2. A movement component

Create `MyGame/Components/TopDownMovement.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Components;

/// <summary>
/// Eight-directional movement with acceleration and friction, driven by the
/// MoveX / MoveY actions.
/// </summary>
public sealed class TopDownMovement : Component
{
    public float MaxSpeed     = 260f;
    public float Acceleration = 1800f;
    public float Friction     = 1400f;

    public Vector2 Velocity { get; private set; }

    private SpriteRenderer? _sprite;

    public override void Start() => _sprite = GetComponent<SpriteRenderer>();

    public override void Update(float dt)
    {
        var input = SBEngine.Instance.Input;

        // Screen-space direction: +Y from the action map means "up", i.e. −Y on screen.
        var wish = new Vector2(input.GetAxis("MoveX"), -input.GetAxis("MoveY"));
        if (wish.LengthSquared() > 1f) wish.Normalize();     // no diagonal speed boost

        if (wish.LengthSquared() > 0.0001f)
        {
            Velocity += wish * Acceleration * dt;
            if (Velocity.Length() > MaxSpeed)
                Velocity = Vector2.Normalize(Velocity) * MaxSpeed;
        }
        else
        {
            // Decelerate toward zero without overshooting into a jitter.
            float speed = Velocity.Length();
            float drop  = Friction * dt;
            Velocity = speed <= drop ? Vector2.Zero : Velocity * ((speed - drop) / speed);
        }

        Transform.Position += Velocity * dt;

        if (_sprite != null && MathF.Abs(Velocity.X) > 1f)
            _sprite.Effects = Velocity.X < 0
                ? Microsoft.Xna.Framework.Graphics.SpriteEffects.FlipHorizontally
                : Microsoft.Xna.Framework.Graphics.SpriteEffects.None;
    }
}
```

Swap it in, replacing `AutoMove`:

```csharp
var player = CreateBox("Player", new Vector2(300, 540), new Vector2(40, 56),
                       new Color(90, 200, 255));
player.Tag = "Player";
player.AddComponent<TopDownMovement>();
scene.AddActor(player, "default");
```

```bash
dotnet run --project MyGame
```

WASD or the arrows move the square; a gamepad stick works with no extra code.

**Why acceleration rather than setting the position directly?** Because
instantaneous velocity changes read as robotic. Acceleration and friction give a
weight the player feels without any physics engine involved.

## 3. Aiming with the mouse

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace MyGame.Components;

/// <summary>Rotates the actor toward the mouse, or the right stick when one is in use.</summary>
public sealed class MouseAim : Component
{
    private Camera2D? _camera;

    public override void Start()
        => _camera = Actor.Scene?.FindByName("Main Camera")?.GetComponent<Camera2D>();

    public override void Update(float dt)
    {
        var engine = SBEngine.Instance;
        var pad    = engine.Input.GetGamepad(0);

        Vector2 aim;
        if (pad.IsConnected && pad.RightStick.LengthSquared() > 0.04f)
        {
            aim = new Vector2(pad.RightStick.X, -pad.RightStick.Y);   // stick Y is up-positive
        }
        else
        {
            if (_camera == null) return;
            var mouseWorld = _camera.ScreenToWorld(engine.Input.MousePosition, engine.GraphicsDevice);
            aim = mouseWorld - Transform.Position;
        }

        if (aim.LengthSquared() > 0.0001f)
            Transform.Rotation = MathF.Atan2(aim.Y, aim.X);
    }
}
```

`ScreenToWorld` is the piece that matters: `MousePosition` is in screen pixels
and the world is camera-transformed, so a raw comparison would be wrong the
moment the camera moves.

Rotation is **radians** in 2D. `MathHelper.ToDegrees` / `ToRadians` when you need
the other.

## 4. Game feel: a dash

Two ideas here that generalise: **buffered input** (register intent slightly
before it is valid) and **coyote time** (accept intent slightly after it stops
being valid).

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;

namespace MyGame.Components;

public sealed class Dash : Component
{
    public float Speed        = 900f;
    public float Duration     = 0.14f;
    public float Cooldown     = 0.55f;
    public float BufferWindow = 0.15f;

    public bool IsDashing => _remaining > 0f;

    private TopDownMovement? _movement;
    private Vector2 _direction;
    private float   _remaining;
    private float   _cooldown;
    private float   _buffer;

    public override void Start() => _movement = GetComponent<TopDownMovement>();

    public override void Update(float dt)
    {
        var input = SBEngine.Instance.Input;

        // Remember the press for a moment so a slightly early input still counts.
        if (input.IsPressed("Dodge")) _buffer = BufferWindow;
        else _buffer -= dt;

        if (_cooldown > 0f) _cooldown -= dt;

        if (_buffer > 0f && _cooldown <= 0f && _remaining <= 0f)
        {
            var dir = new Vector2(input.GetAxis("MoveX"), -input.GetAxis("MoveY"));
            if (dir.LengthSquared() < 0.01f)
                dir = new Vector2(MathF.Cos(Transform.Rotation), MathF.Sin(Transform.Rotation));

            _direction = Vector2.Normalize(dir);
            _remaining = Duration;
            _cooldown  = Cooldown;
            _buffer    = 0f;

            SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/dash.wav");
        }

        if (_remaining > 0f)
        {
            _remaining -= dt;
            Transform.Position += _direction * Speed * dt;
        }
    }
}
```

Suppress normal movement while dashing, in `TopDownMovement.Update`:

```csharp
public override void Update(float dt)
{
    if (GetComponent<Dash>()?.IsDashing == true) return;
    // … existing body …
}
```

```csharp
player.AddComponent<TopDownMovement>();
player.AddComponent<Dash>();
```

A 0.15 s buffer is imperceptible to the player and turns "the game dropped my
input" into "the game feels responsive". Use it for jumps, attacks and dashes.

## 5. Custom action maps

Create `MyGame/Assets/Input/actions.json`:

```json
{
  "MoveX": [
    { "device": "keyboard", "negKey": "A",    "posKey": "D" },
    { "device": "keyboard", "negKey": "Left", "posKey": "Right" },
    { "device": "gamepad",  "axis": "LeftX" }
  ],
  "MoveY": [
    { "device": "keyboard", "negKey": "S",    "posKey": "W",  "invert": true },
    { "device": "keyboard", "negKey": "Down", "posKey": "Up", "invert": true },
    { "device": "gamepad",  "axis": "LeftY", "invert": true }
  ],
  "Fire": [
    { "device": "mouse",   "button": "Left" },
    { "device": "gamepad", "axis": "RightTrigger" }
  ],
  "Dodge": [
    { "device": "keyboard", "key": "LeftShift" },
    { "device": "gamepad",  "button": "B" }
  ],
  "Interact": [
    { "device": "keyboard", "key": "E" },
    { "device": "gamepad",  "button": "X" }
  ],
  "Pause": [
    { "device": "keyboard", "key": "Escape" },
    { "device": "gamepad",  "button": "Start" }
  ]
}
```

```csharp
protected override void OnEngineReady()
{
    Input.LoadActionMap("Assets/Input/actions.json");
    BuildScene();
}
```

Binding fields:

| Field | Meaning |
|---|---|
| `device` | `keyboard`, `mouse`, `gamepad`, `touch` |
| `key` | one key, matching the `Keys` enum name |
| `negKey` / `posKey` | the two halves of a keyboard axis |
| `button` | mouse `Left`/`Right`/`Middle`, or a `Buttons` enum name |
| `axis` | gamepad `LeftX`/`LeftY`/`RightX`/`RightY`/`LeftTrigger`/`RightTrigger`, or mouse `MouseX`/`MouseY`/`Scroll` |
| `invert` | negate the axis |
| `scale` | multiply the axis |

`GetAxis` returns the **first non-zero** binding rather than a sum, so keyboard
and stick never fight. `invert` and `scale` apply to `GetAxis` only, not to the
boolean queries.

## 6. Rebinding

```csharp
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

namespace MyGame.Components;

/// <summary>Listens for the next key press and rebinds an action to it.</summary>
public sealed class RebindListener : Component
{
    public string Action    = "Jump";
    public bool   Listening;
    public Action<string>? OnRebound;

    public override void Update(float dt)
    {
        if (!Listening) return;
        var input = SBEngine.Instance.Input;

        foreach (Keys key in Enum.GetValues<Keys>())
        {
            if (!input.IsKeyPressed(key)) continue;

            Listening = false;
            if (key == Keys.Escape) return;             // cancel

            input.RebindAction(Action, new InputBinding
            {
                Device = "keyboard",
                Key    = key.ToString(),
            });
            OnRebound?.Invoke(key.ToString());
            return;
        }
    }
}
```

```csharp
input.SaveBindings("Saves/bindings.json");

if (File.Exists("Saves/bindings.json"))      // LoadBindings throws when missing
    input.LoadBindings("Saves/bindings.json");

input.ResetBindings();                       // back to the map captured at construction
```

Two behaviours to know:

- **`RebindAction` replaces** the action's whole binding list with the one
  binding you pass. Call it once per binding you want to keep.
- **`LoadBindings` merges** — actions in the file replace the current ones,
  actions absent from it are untouched. So a file containing only the actions
  the player remapped is valid and preferable.

Key names round-trip through `Enum.TryParse<Keys>`, so `key.ToString()` always
produces a name the loader accepts.

## 7. Gamepad and cursor

```csharp
var pad = Input.GetGamepad(0);           // indices 0–3
if (pad.IsConnected)
{
    Vector2 move = pad.LeftStick;        // dead-zoned
    float   fire = pad.RightTrigger;     // 0..1
    if (pad.IsButtonPressed(Buttons.A)) { }
    pad.DeadZone = 0.2f;                 // default 0.15, radial
    pad.SetRumble(0.7f, 0.3f, duration: 0.2f);
}
```

Rumble duration counts down inside `GamepadState.Update`, which the engine
drives — a timed rumble stops on its own.

```csharp
Input.HideCursor();
Input.LockCursor();      // warps back each frame; MouseDelta still reports movement
Input.UnlockCursor();
Input.ShowCursor();
```

### Which device is the player using?

```csharp
public sealed class ActiveDevice : Component
{
    public bool UsingGamepad { get; private set; }

    public override void Update(float dt)
    {
        var input = SBEngine.Instance.Input;
        var pad   = input.GetGamepad(0);

        if (pad.IsConnected &&
            (pad.LeftStick.LengthSquared() > 0.04f || pad.IsButtonPressed(Buttons.A)))
            UsingGamepad = true;
        else if (input.MouseDelta.LengthSquared() > 1f)
            UsingGamepad = false;
    }
}
```

Use it to swap button-prompt art. Players notice.

---

## Checkpoint

You have:

- Action-based input that works on keyboard, mouse and gamepad
- Acceleration/friction movement and mouse aiming
- A dash with input buffering and a cooldown
- A custom action map, plus rebinding and persistence

## Exercises

1. Add a `Sprint` action that raises `TopDownMovement.MaxSpeed` while held.
2. Rumble on dash: `Input.SetRumble(0, 0.6f, 0.2f, 0.12f);`
3. Make `Dash` fire a `Camera2D.Shake(0.3f, 0.15f)` on activation.

---

**Next:** [Tutorial 4 — Writing Components](04-components.md)
