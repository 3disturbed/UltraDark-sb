# 7. Input

Namespace: `SexyBiscuit.Engine.Input`

`InputManager` is created by `SBEngine` and pumped for you in `Update` — this is
one of the few systems that needs no wiring.

```csharp
var input = SBEngine.Instance.Input;
```

It offers two layers:

- **Raw device queries** — keyboard, mouse, gamepad, touch. Direct and immediate.
- **Action maps** — named, rebindable, device-agnostic actions. Use these for
  anything a player might want to remap.

---

## Action maps

An **action** is a name with one or more **bindings**. The first binding that
reports activity wins.

```csharp
if (input.IsPressed("Jump"))   { }    // true on the frame it goes down
if (input.IsHeld("Attack"))    { }    // true while active
if (input.IsReleased("Attack")){ }    // true on the frame it goes up
float x = input.GetAxis("MoveX");     // −1..1
```

### The default action map — Verified

`ActionMap.Default()` is installed at construction. These are the exact action
names and bindings:

| Action | Keyboard | Gamepad / Mouse |
|---|---|---|
| `MoveX` | `A` / `D`, `Left` / `Right` | left stick X |
| `MoveY` | `S` / `W`, `Down` / `Up` (inverted) | left stick Y (inverted) |
| `Jump` | `Space` | `A` |
| `Attack` | `Z` | mouse left, gamepad `X` |
| `Dodge` | `LeftShift` | `B` |
| `Interact` | `E` | `X` |
| `Pause` | `Escape` | `Start` |
| `CameraX` | — | mouse X delta, right stick X |
| `CameraY` | — | mouse Y delta, right stick Y |

`MoveY` is **inverted** so that `W`/`Up` yields `+1`. In a 2D game where screen
Y increases downward, that means "up on the stick" is positive Y in action
space, and you negate it when applying to a position:

```csharp
float mx = input.GetAxis("MoveX");
float my = input.GetAxis("MoveY");
transform.Position += new Vector2(mx, -my) * speed * dt;   // note the minus
```

Action names are matched **case-insensitively** (`StringComparer.OrdinalIgnoreCase`).
An unknown action name returns `false` / `0f` silently — it never throws, so a
typo shows up as an unresponsive control, not an exception.

### Defining your own map

```csharp
input.LoadActionMap("Assets/Input/actions.json");
```

The file is `Dictionary<string, List<InputBinding>>`:

```json
{
  "MoveX": [
    { "device": "keyboard", "negKey": "A",    "posKey": "D" },
    { "device": "keyboard", "negKey": "Left", "posKey": "Right" },
    { "device": "gamepad",  "axis": "LeftX" }
  ],
  "MoveY": [
    { "device": "keyboard", "negKey": "S", "posKey": "W", "invert": true },
    { "device": "gamepad",  "axis": "LeftY", "invert": true }
  ],
  "Fire": [
    { "device": "mouse",    "button": "Left" },
    { "device": "gamepad",  "axis": "RightTrigger", "scale": 1.0 }
  ],
  "Sprint": [
    { "device": "keyboard", "key": "LeftShift" },
    { "device": "gamepad",  "button": "LeftStick" }
  ]
}
```

Trailing commas and `//` comments are allowed — the loader sets
`AllowTrailingCommas` and `ReadCommentHandling = Skip`.

### InputBinding fields

| Field | Type | Used for |
|---|---|---|
| `device` | `"keyboard"` \| `"mouse"` \| `"gamepad"` \| `"touch"` | dispatch |
| `key` | string | a single key, name matching the `Keys` enum |
| `negKey` / `posKey` | string | the two halves of a keyboard axis |
| `button` | string | mouse (`Left`, `Right`, `Middle`, `XButton1`, `XButton2`) or gamepad button (`Buttons` enum name) |
| `axis` | string | gamepad `LeftX`/`LeftY`/`RightX`/`RightY`/`LeftTrigger`/`RightTrigger`, mouse `MouseX` / `MouseY` / `Scroll`, or touch `LeftJoystickX`/`LeftJoystickY`/`RightJoystickX`/`RightJoystickY`/`PinchDelta` |
| `invert` | bool | negates the axis result |
| `scale` | float | multiplies the axis result, default `1` |

Evaluation notes worth knowing:

- **Digital axes** produce exactly `−1`, `0`, `+1`. `posKey` adds `+1`,
  `negKey` subtracts `1`, so holding both gives `0`.
- **`GetAxis` returns the first non-zero binding**, not a sum. Keyboard and stick
  never fight each other.
- **`IsPressed`/`IsHeld`/`IsReleased` on an axis binding** fall back to `key`,
  then `posKey`, then `negKey` — so `IsHeld("MoveX")` is true if either
  direction is held.
- `scale` and `invert` apply **only to `GetAxis`**, not to the boolean queries.

### Rebinding

```csharp
input.RebindAction("Jump", new InputBinding { Device = "keyboard", Key = "W" });
input.SaveBindings("Saves/bindings.json");

if (File.Exists("Saves/bindings.json"))      // LoadBindings throws if it is missing
    input.LoadBindings("Saves/bindings.json");

input.ResetBindings();                       // back to the map captured at construction
```

`LoadBindings` **merges** — actions present in the file replace the current
ones, actions absent from it keep their existing bindings. So a partial file
containing only the actions the player remapped is valid and preferable.

`RebindAction` **replaces** the action's binding list with the single new
binding. To keep alternatives, rebuild the list yourself:

```csharp
var jump = new InputAction("Jump",
    new InputBinding { Device = "keyboard", Key = newKeyName },
    new InputBinding { Device = "gamepad",  Button = "A" });
// then reload the whole map, or call RebindAction once per binding you want kept
```

A "press a key to rebind" flow:

```csharp
public sealed class RebindPrompt : Component
{
    public string Action = "Jump";
    public bool   Listening;

    public override void Update(float dt)
    {
        if (!Listening) return;
        var input = SBEngine.Instance.Input;

        foreach (Keys key in Enum.GetValues<Keys>())
        {
            if (!input.IsKeyPressed(key)) continue;
            if (key == Keys.Escape) { Listening = false; return; }

            input.RebindAction(Action, new InputBinding
            {
                Device = "keyboard",
                Key    = key.ToString(),
            });
            input.SaveBindings("Saves/bindings.json");
            Listening = false;
            return;
        }
    }
}
```

Key names round-trip through `Enum.TryParse<Keys>`, so `key.ToString()` always
produces a name the loader accepts.

---

## Keyboard

```csharp
input.IsKeyDown(Keys.W);        // held this frame
input.IsKeyPressed(Keys.Space); // went down this frame
input.IsKeyReleased(Keys.Space);// came up this frame
```

`Keys` is `Microsoft.Xna.Framework.Input.Keys`.

---

## Mouse

```csharp
Vector2 pos    = input.MousePosition;   // screen pixels
Vector2 delta  = input.MouseDelta;      // movement since last frame
float   scroll = input.ScrollDelta;     // wheel change since last frame

input.IsMouseButtonDown(MouseButton.Left);
input.IsMouseButtonPressed(MouseButton.Right);
input.IsMouseButtonReleased(MouseButton.Middle);
```

`MouseButton` is the engine's own enum (`SexyBiscuit.Engine.Input.MouseButton`).

### Cursor

```csharp
input.ShowCursor();
input.HideCursor();
input.LockCursor();      // warps the cursor back each frame — FPS-style mouselook
input.UnlockCursor();
input.SetCursorTexture(myTexture);
```

While locked, the cursor is warped back to its lock position every `Update`, and
`MouseDelta` still reports the movement that happened before the warp — so
mouselook works.

### Screen to world

`MousePosition` is in screen pixels. Convert through the camera:

```csharp
Vector2 world = camera.ScreenToWorld(input.MousePosition, GraphicsDevice);
```

---

## Gamepads

Four pads, indices 0–3.

```csharp
var pad = input.GetGamepad(0);
if (!pad.IsConnected) return;

Vector2 move = pad.LeftStick;      // dead-zoned
Vector2 look = pad.RightStick;
float   lt   = pad.LeftTrigger;    // 0..1
float   rt   = pad.RightTrigger;

pad.IsButtonDown(Buttons.A);
pad.IsButtonPressed(Buttons.B);
pad.IsButtonReleased(Buttons.X);

pad.DeadZone = 0.2f;               // default 0.15, radial
float axis = pad.GetAxis("LeftX"); // "LeftX" | "LeftY" | "RightX" | "RightY" | "LeftTrigger" | "RightTrigger"
```

Rumble:

```csharp
pad.SetRumble(lowFreq: 0.8f, highFreq: 0.4f, duration: 0.25f);
pad.StopRumble();
input.SetRumble(playerIndex: 0, 0.8f, 0.4f, 0.25f);   // same thing via the manager
input.IsGamepadConnected(0);
```

Rumble duration is counted down in `GamepadState.Update`, which `InputManager`
drives — so a timed rumble stops on its own.

---

## Touch and virtual controls

```csharp
var touch = input.Touch;
touch.SetScreenSize(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

foreach (TouchPoint t in touch.Touches)
{
    // t.Id, t.Position, t.Delta, t.Phase (Began / Moved / Stationary / Ended / Cancelled)
}

float pinch = touch.PinchDelta;        // >0 spreading, <0 pinching
```

Two built-in virtual sticks, one per half of the screen:

```csharp
touch.LeftJoystick.Radius = 100f;    // Center is anchored where the finger lands
Vector2 move = touch.LeftJoystick.Value;     // −1..1, zero when not touched
Vector2 look = touch.RightJoystick.Value;
bool active  = touch.LeftJoystick.IsActive;
```

`VirtualJoystick.Update` takes the touch list and the screen width and claims
touches in its own half — `Side` is `Left`, `Right` or `Any`. Opposite halves can
never fight over the same finger, which is what lets a player move and look at
once. `TouchManager.Update` drives both for you. Call `SetScreenSize` once at
startup and again on resize.

### Touch through the action map

Prefer binding an action to a stick over reading `Input.Touch` directly: a game
that goes through the map keeps rebinding, gamepad support and everything else
the map provides.

```json
{
  "MoveX": [
    { "device": "keyboard", "negKey": "A", "posKey": "D" },
    { "device": "gamepad",  "axis": "LeftX" },
    { "device": "touch",    "axis": "LeftJoystickX" }
  ],
  "Zoom": [ { "device": "touch", "axis": "PinchDelta" } ]
}
```

`ActionMap.Default()` already binds `MoveX`, `MoveY`, `CameraX` and `CameraY` to
the sticks, so a game using nothing but the defaults is playable on a phone.

A touch binding with **no** axis reads as a tap anywhere on the screen, so
`IsPressed`, `IsHeld` and `IsReleased` work on it like a button.

The stick's Y is screen-space — pushing up reads negative — which is the same
sign the inverted keyboard and gamepad bindings produce for forward. A touch
binding on `MoveY` therefore needs no `invert`.

---

## Recipes

### Twin-stick aiming

```csharp
public override void Update(float dt)
{
    var input = SBEngine.Instance.Input;
    var pad   = input.GetGamepad(0);

    Vector2 aim = pad.IsConnected && pad.RightStick.LengthSquared() > 0.04f
        ? pad.RightStick
        : Vector2.Normalize(_camera.ScreenToWorld(input.MousePosition, _gd) - Transform.Position);

    if (aim.LengthSquared() > 0f)
        Transform.Rotation = MathF.Atan2(aim.Y, aim.X);
}
```

### Buffered jump (forgiving input)

```csharp
private float _jumpBuffer;

public override void Update(float dt)
{
    var input = SBEngine.Instance.Input;
    if (input.IsPressed("Jump")) _jumpBuffer = 0.12f;
    else _jumpBuffer -= dt;

    if (_jumpBuffer > 0f && _controller.IsGrounded)
    {
        _controller.Jump();
        _jumpBuffer = 0f;
    }
}
```

### Detecting the active device, to swap prompt art

```csharp
private bool _usingGamepad;

public override void Update(float dt)
{
    var input = SBEngine.Instance.Input;
    var pad   = input.GetGamepad(0);

    if (pad.IsConnected && (pad.LeftStick.LengthSquared() > 0.04f || pad.IsButtonPressed(Buttons.A)))
        _usingGamepad = true;
    else if (input.MouseDelta.LengthSquared() > 1f)
        _usingGamepad = false;
}
```

---

## In JavaScript

The script bridge exposes a subset:

```js
Input.isPressed("Jump");
Input.isHeld("MoveX");
Input.isReleased("Attack");
Input.getAxis("MoveX");
Input.mouseX;   // read-only
Input.mouseY;   // read-only
```

There is **no** `Input.isKeyPressed` / `isKeyHeld` / `isMouseHeld` in the bridge,
despite what several project templates assume. Define an action and use
`Input.isPressed`, or extend the bridge — see
[11. Scripting](11-scripting.md#extending-the-bridge).

---

## Next

- [8. Audio](08-audio.md)
- [Tutorial 3: Input & Movement](../tutorials/03-input-and-movement.md)
