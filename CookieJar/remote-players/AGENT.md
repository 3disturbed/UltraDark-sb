# Remote Players

Host a game or join one. A client that connects becomes a spawned, possessed player on every
machine.

## Wiring it up

Two steps, not one, because the game mode decides how players are made:

```csharp
using Cookies.RemotePlayers;

// 1. The session, on any actor in the start scene.
var session = actor.AddComponent<NetSession>();
session.Mode    = SessionMode.ListenServer;   // or Client, DedicatedServer, Offline
session.Port    = 9050;
session.Address = "127.0.0.1";                // for Client

// 2. The game mode. Use this one, or subclass it.
scene.AddActor(new NetworkGameMode());
```

`NetSession` logs a warning if the scene's game mode is not a `NetworkGameMode`, because nothing
would spawn and the reason would not be obvious.

## What it adds

- `NetSession` — the turn-on. Starts a server or connects, and owns the session for as long as the
  component lives. It also pumps the transport, so nothing in the engine had to change.
- `NetworkGameMode` — spawns a player per connected client, attaches a `NetworkObject`, sets its
  owner, and removes the player when the client goes.
- `NetPlayerController` — sends its owner's actions to the server every tick and applies them there
  through the virtual input layer, so **the same `OnPlayerTick` runs for a local and a remote
  player**.
- `NetTransform` — replicates position and rotation, and interpolates them on the machines that do
  not own the actor, so a pawn does not step at the send rate.
- `LanBrowser` — finds servers broadcasting on the local network.

## What it does not do, and this matters

**There is no client-side prediction.** With `Authority = ServerAuthoritative` your own character
waits a round trip before it moves, which is fine on a LAN and not fine at 100 ms. The default is
therefore `ClientAuthoritative`: your own pawn moves at once and the server accepts it. That feels
right and is trivially cheatable. This is co-op-with-friends netcode, not competitive netcode, and
building the latter means making movement deterministic and re-simulable, which is a much larger
job.

Also missing: no reconnect (a client that drops rejoins as a new player), no lag compensation, no
host migration, and nothing at all in the browser — the runtime cannot open a UDP socket, so a web
build should stay `Offline`.
