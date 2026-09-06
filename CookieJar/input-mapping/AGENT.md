# Input Mapping

Actions the player can rebind, on keyboard, mouse or gamepad, remembered between runs.

## Wiring it up

Add an `InputSettings` component to any actor in the start scene. One is enough: it configures the
whole input manager on `Awake`. Everything else in the game keeps calling `input.GetAxis("MoveX")`
and `input.IsPressed("Jump")` exactly as before.

```csharp
using Cookies.InputMapping;

var settings = actor.AddComponent<InputSettings>();
settings.ActionMapAsset  = "Config/Cookies/input-mapping/Actions.json";
settings.LookSensitivity = 0.15f;
```

## What it adds

- `InputSettings` — the turn-on. Loads the action map and any saved rebinds, applies look and
  dead-zone defaults.
- `BindingStore` — `Load`, `Save` and `Reset`, plus `SetBinding`, which replaces **one** slot of an
  action rather than all of them. The engine's own `RebindAction` clears every alternate, so
  rebinding Jump to K would otherwise silently drop its gamepad and touch bindings.
- `RebindCapture` — "press anything". `Begin(action)`, then call `Update()` each frame until it
  returns a binding.
- `InputGlyphs` — `Describe(binding)` gives "Space", "LMB" or "A button" for a prompt.

## Actions it ships

`MoveX`, `MoveY`, `Jump`, `Attack`, `Dodge`, `Interact`, `Pause`, `CameraX`, `CameraY` (the engine's
own), plus `Sprint`, `Crouch`, `Reload`, `Aim`, `Zoom`, and `UISubmit`, `UICancel`, `UINavX`,
`UINavY` for menus.

## What it does not do

- No rebinding screen. Widgets are drawn in code and every game wants its own; use
  `RebindCapture` and `InputGlyphs` to build one.
- Nothing per-player is configured here. Ask the engine for `input.GetPlayer(n)` and set its
  `Devices` — the couch co-op cookie does exactly that.
- Bindings are saved per user, not per project, so two games on one machine do not share them.
