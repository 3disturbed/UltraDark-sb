# 15. Networking

Namespaces: `SexyBiscuit.Engine.Networking` · `html5/src/net/`

Both engines speak **one wire**. A message id table, a set of encodings and a
protocol version live in `NetProtocol.cs` and `html5/src/net/protocol.js`, and a
test on each side reads the other's source — so a browser client and a native
server can sit in the same session, and a script written against `Network.*`
behaves the same in a tab and in a desktop build.

```
NetworkManager           sessions, client ids, frame dispatch
├── INetworkTransport     the seam; no transport type gets past it
│   ├── WebSocket          the shared wire — the only one a browser has
│   ├── LiteNetLib (UDP)   desktop ↔ desktop, with a real unreliable channel
│   └── Loopback           a process wired to itself: solo play, and every test
├── ReplicationSystem      [Replicated] members → periodic state frames
└── RpcSystem              [ServerRpc] / [ClientRpc]
NetworkObject            per-actor identity, ownership, state collection
```

> **`Tick(dt)` is not called by the engine.** Nothing is sent or received until
> you pump it — a game that is not networked should pay nothing for the fact
> that it could be.

---

## Starting a session

```csharp
var nm = new NetworkManager { PlayerName = "Darko" };

nm.StartSolo();                                  // both ends, no socket
nm.StartServer(port: 7777);                      // WebSocket: a browser can join
nm.StartServer(7777, transport: NetTransportKind.Udp);   // desktop ↔ desktop
nm.ConnectToServer("192.168.1.42", 7777);        // UDP
nm.ConnectToUrl("wss://my-game.darksgames.app/ws?room=ABC234", room: "ABC234");
```

```js
const nm = new NetworkManager();
nm.playerName = 'Darko';

nm.startSolo();                                  // and `startServer(port)`, which is this
nm.connectToUrl('wss://my-game.darksgames.app/ws?room=ABC234', { room: 'ABC234' });
nm.connect('192.168.1.42', 7777);                // composes a ws:// URL: a tab has no UDP
```

**Solo is a real session.** It goes through the same handshake, the same encode
and decode, and the same replication path a lobby does. That is the point: it is
what stops "works alone, breaks with two players".

**A browser cannot listen.** `startServer` in a tab hosts over loopback; other
players join through a room server (below), not through that tab.

### State

```csharp
nm.IsServer;            // this peer runs the server end of the transport
nm.IsHost;              // this peer is the session's AUTHORITY (see below)
nm.IsClient;
nm.IsRunning;
nm.IsConnected;         // the server has accepted us, or we are it
nm.LocalClientId;       // 0 on the server, −1 until Welcome arrives
nm.Ping;                // ms, clients
nm.Room;                // the room code, when there is one
nm.Players;             // id → name, everybody in the session
nm.ConnectedClientIds;  // server only
```

`IsHost` means **"should I run the simulation?"**. It is the server when there
is one; in a relayed room nobody is the server, so it is the lowest client id
present — a rule every peer evaluates alone, with no extra message, and which
hands the role on the moment the current host leaves.

### Events

```csharp
nm.OnClientConnected    += id => …;      // server
nm.OnClientDisconnected += id => …;      // server
nm.OnConnectedToServer      += () => LoadGameScene();
nm.OnDisconnectedFromServer += reason => ReturnToMenu(reason);
nm.OnPlayerJoined += (id, name) => …;
nm.OnPlayerLeft   += id => …;
nm.OnSpawnObject   += (netId, actorName, ownerId, state) => SpawnRemote(…);
nm.OnDespawnObject += netId => DespawnRemote(netId);
nm.OnMessage += (sender, type, payload) => …;
```

```js
const off = nm.on('message', (sender, type, payload) => …);   // returns an unsubscribe
```

### Pump it

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    NetworkManager.Instance?.Tick((float)gameTime.ElapsedGameTime.TotalSeconds);
}
```

---

## The wire

Every frame is `[id u8][payload]`, little-endian, strings as
`[u16 byte length][UTF-8]`.

| id | Name | Direction | Payload |
|---|---|---|---|
| `0x01` | Hello | C→S | JSON `{proto, name, room, token}` |
| `0x02` | Welcome | S→C | JSON `{proto, clientId, room, players}` |
| `0x03` | Spawn | S→C | `[netId u32][name str][owner i32][state bytes]` |
| `0x04` | Despawn | S→C | `[netId u32]` |
| `0x05` | State | both | `[netId u32][state bytes]` |
| `0x06` | Rpc | both | `[netId u32][toServer u8][target i32][method str][args bytes]` |
| `0x07` | Message | both | `[sender i32][type str][json str]` |
| `0x08` / `0x09` | Ping / Pong | | `[clientTime f64]` / `+ [serverTime f64]` |
| `0x0A` / `0x0B` | PeerJoined / PeerLeft | S→C | `[clientId i32][name str]` / `[clientId i32]` |
| `0x0C` | Kick | S→C | `[reason str]` |

Two things about this that are load-bearing:

- **Not `BinaryWriter`.** Its `Write(string)` prefixes a 7-bit-encoded length —
  a .NET detail with no JavaScript counterpart — and its endianness is whatever
  the machine is. Either one makes a browser client that agrees with a native
  server impossible to write correctly.
- **A mismatched `proto` is refused at Hello.** A client one version behind
  would otherwise read every later frame at the wrong offsets and fail somewhere
  unrelated, which is the failure that cannot be diagnosed from a bug report.

### Delivery

`NetDelivery.Unreliable` for state, `ReliableOrdered` for everything that
mutates agreed state. A transport that cannot honour a mode satisfies it with a
stronger one: **WebSocket delivers everything reliably ordered**, because TCP has
no other setting. A state update therefore cannot be dropped in favour of a
fresher one the way it can over UDP. That is a real difference, and it is why
both transports exist rather than one.

---

## The script channel

What `Network.sendToAll(type, data)` reaches. The engine never looks inside the
payload; it guarantees only that the sender the receiver sees is the id the
**server** assigned, not one the sender chose — trusting the number in the frame
lets any peer post as any other, which is the cheapest possible way to cheat.

```js
Network.sendToAll('playerMove', { x: 10, y: 20 });
Network.sendTo(3, 'privateOffer', { card: 'ace' });

function onNetworkMessage(type, data, sender) {   // a lifecycle hook, like onUpdate
    if (type === 'playerMove') movePeer(sender, data);
}
```

The hook is subscribed only for a script that declares it, and only once a
session exists — a lobby that calls `Network.startServer` from `onUpdate` is
still connected in time.

```
Network.localId  .isServer  .isHost  .isConnected  .ping  .room  .players
        .playerName  .isLocalPlayer(id)
        .startServer(port)  .startSolo()  .connect(address, port)  .disconnect()
        .sendToAll(type, data)  .broadcast(…)  .sendTo(id, type, data)
        .on(event, fn)   // message, playerJoined, playerLeft, connected, disconnected
```

---

## Rooms and link-to-join

A browser cannot listen, so "hosting" on the web is asking a room server for a
code. The shape is the one every Darks Games title uses, so the social overlay's
**Join** button works with no translation — see
[29. Darks Games](29-darksgames.md).

```
POST /api/rooms        -> { code, mode, joinUrl }
GET  /api/rooms/:code  -> { code, players, max, phase, joinable } | 404
WS   /ws?room=CODE
```

```bash
node html5/tools/roomserver.js --port 8081 --max 8
```

```js
const room = await createRoom({ mode: 'coop' });      // { code, joinUrl }
nm.connectToUrl(roomSocketUrl(room.code), { room: room.code });

const code = roomCodeFromUrl();       // /j/CODE, ?room=, #CODE — all read
```

Codes are six characters from an alphabet with no `0`/`O` and no `1`/`I`/`L`:
they get read aloud, typed from memory and pasted into chat.

**The relay owns identity, not just bytes.** Through a relay every peer reaches
the host down one socket, so a host cannot tell two players apart by connection.
The room server therefore answers the handshake, assigns the client ids everyone
sees, and rewrites the sender on a relayed message; spawn, state and RPC go out
untouched, which is what keeps the server the same for every game.

---

## NetworkObject

Every replicated actor needs one.

```csharp
NetworkObject.Spawn(actor);      // server only; allocates an id and broadcasts
NetworkObject.Despawn(actor);

netObj.NetworkId;      // uint, assigned by the server
netObj.IsOwner;        // this peer has authority over the object
netObj.OwnerClientId;  // −1 for server-owned
```

**Clients construct the actor themselves** in response to `OnSpawnObject` — the
engine sends a name, not a prefab:

```csharp
nm.OnSpawnObject += (netId, actorName, ownerId, state) =>
{
    Actor actor = actorName switch
    {
        "Player" => BuildPlayer(),
        "Bullet" => BuildBullet(),
        _        => new Actor(actorName),
    };

    var no = actor.GetComponent<NetworkObject>() ?? actor.AddComponent<NetworkObject>();
    no.NetworkId     = netId;
    no.OwnerClientId = ownerId;
    no.IsOwner       = ownerId == nm.LocalClientId;

    SceneManager.ActiveScene!.AddActor(actor);
    nm.Replication.RegisterObject(no);
    nm.Rpc.Register(actor, no);
    _netActors[netId] = actor;
};
```

That factory table is the single most important piece of networking glue you
will write. Keep it beside your spawn call so the two never drift.

---

## Replication

C# marks a member; JavaScript says so in the component's schema. Both produce
the same blob, so the two engines replicate to each other with no translation.

```csharp
public sealed class PlayerState : Component
{
    [Replicated] public int Health = 100;
    [Replicated(Condition = ReplicateCondition.OwnerOnly)] public Vector2 Aim;
    [Replicated(Condition = ReplicateCondition.InitialOnly)] public string Skin = "";
}
```

```js
class PlayerState extends Component {
    static schema = {
        ...Component.schema,
        health: { type: 'int', default: 100, replicated: true },
        aim: { type: 'vector2', default: [0, 0], replicated: true, condition: 'ownerOnly' },
    };
}
```

The blob is `[count u16]` then `[name str][tag u8][value]` per member. Only
changed members are sent after the first snapshot, and **a member the receiving
build has never heard of is stepped over rather than breaking the blob** — which
is what lets an older client stay in a session with a newer server instead of
dropping every update.

`Int64` rides as a double, in a replicated member and in an RPC argument alike:
JavaScript has no 64-bit integer in a `Number`, and a value that does not survive
the round trip is worse than one documented to carry 53 bits.

---

## RPC

```csharp
[ServerRpc] public void RequestFire(Vector2 at) { … }    // client → server
[ClientRpc] public void PlayHit(int amount) { … }        // server → clients
```

Arguments may be `bool`, `byte`, `int`, `uint`, `long`, `float`, `double`,
`string`, `Vector2` and `Vector3`. Anything else is sent as null, with a warning.

---

## LAN discovery

`LanDiscovery` broadcasts and listens on UDP so a game can offer a server browser
without anyone typing an address. Desktop only — a browser cannot broadcast.

---

## Next

- [29. Darks Games](29-darksgames.md) — identity, friends, presence and Join
- [11. Scripting](11-scripting.md) — the `Network` and `DG` globals
- [3. The Game Loop](03-game-loop.md) — where `Tick` goes
