# Couch Co-op

Several people on one machine. Players join by pressing a button on an unclaimed pad, get their own
devices, and share one camera that keeps everybody in frame.

## Wiring it up

```csharp
using Cookies.CouchCoop;

// On the actor that carries the GameMode:
var players = gameModeActor.AddComponent<LocalPlayers>();
players.MaxPlayers          = 4;
players.JoinAction          = "Jump";      // any action; pressing it on a free pad joins
players.KeyboardIsPlayerOne = true;

// On the camera tagged MainCamera3D:
cameraActor.AddComponent<CoopCameraRig>();
```

Add at least `MaxPlayers` **Player Start** actors to the scene, or everyone spawns in the same
place. The game mode's `AutoStartLocalPlayer` still spawns player one; this joins the rest.

## What it adds

- `LocalPlayers` — the director. Watches free pads for the join action, calls
  `GameMode.SpawnPlayer(n)`, and hands each player their devices. Player one keeps the keyboard
  and mouse; players two and up get one pad each. Handles a pad being unplugged mid-match.
- `CoopCameraRig` — one camera that frames every living player, pulling back to fit and clamping to
  a distance range so nobody is a dot. Drop it on the camera; it needs no wiring.
- `PlayerColours` — a per-index tint so people can tell which pawn is theirs. Add it to the pawn,
  or let `LocalPlayers` add it by setting `TintPawns`.

## Reading input in your controller

Nothing changes. `OnPlayerTick` already receives the manager, and `Player` is this controller's own
view of it:

```csharp
protected override void OnPlayerTick(Pawn pawn, InputManager input, float dt)
{
    var me = Player!;                       // the devices this player owns
    float x = me.GetAxis("MoveX");
    if (me.IsPressed("Jump")) /* ... */;
}
```

Using `input.GetAxis(...)` directly still works, but it always means player one.

## What it does not do

- **No split-screen.** Cameras have no viewport rectangle in this engine yet, so every player
  shares one view. `CoopCameraRig` is the answer for now.
- No drop-in for a player who leaves: an unplugged pad frees its slot, and rejoining spawns a new
  player rather than resuming the old one.
- Nothing networked. Remote players are a different cookie.
