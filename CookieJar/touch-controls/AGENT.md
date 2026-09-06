# Touch Controls

Thumb sticks and buttons drawn over the game, driving the same actions everything else reads.

## Wiring it up

```csharp
using Cookies.TouchControls;

var touch = actor.AddComponent<TouchOverlay>();
touch.LeftStick        = true;            // MoveX / MoveY
touch.RightStick       = true;            // CameraX / CameraY, for a 3D game
touch.Buttons          = new List<string> { "Jump", "Attack" };
touch.AutoHideOnDesktop = true;           // only appears when the device has a touch screen
```

That is all. A controller reading `input.GetAxis("MoveX")` or `Player!.IsPressed("Jump")` needs no
changes: the overlay pushes through the player's virtual input layer, which the action map reads
before it looks at any device.

## What it adds

- `TouchOverlay` — builds a canvas of sticks and buttons and keeps them fed. Set `PlayerIndex` when
  more than one person is playing.
- `MouseTouchSimulator` — turns mouse drags into touches so the overlay can be tried on a desktop.
  `TouchOverlay.SimulateWithMouse` adds one for you.
- `SafeArea` — insets the overlay by a margin for a notch or a rounded corner. DesktopGL exposes no
  safe-area API, so the numbers are yours to set.

## Tuning

`StickRadius` (80), `StickMargin`, `ButtonRadius` (44), `Opacity` (0.35), and `FloatingSticks` —
when true a stick appears wherever the thumb lands in its half rather than sitting in one place,
which is what most phone games do.

## What it does not do

- **Do not put a `Slider` or a `ScrollView` in this canvas.** Both track a drag across frames with
  no pointer id, so with two fingers down they misbehave. Sticks and buttons here claim a pointer
  by id and are safe.
- It does not lay itself out for you beyond the margins: two sticks and up to four buttons fit
  comfortably, more will overlap.
- Nothing here is drawn in the browser build. The HTML5 runtime has its own touch overlay in the
  page, which is sharper than anything drawn into a canvas.
