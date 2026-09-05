# 15. Networking

Namespace: `SexyBiscuit.Engine.Networking`

Client–server over **LiteNetLib** (UDP, reliable and unreliable channels), with
attribute-driven state replication and RPCs.

```
NetworkManager        transport, connections, packet dispatch
├── ReplicationSystem  [Replicated] fields → periodic state packets
└── RpcSystem          [ServerRpc] / [ClientRpc] methods
NetworkObject         per-actor identity, ownership, state collection
LanDiscovery          UDP broadcast server browser
```

> **`NetworkManager.Tick(dt)` is not called by the engine.** Nothing is sent or
> received until you pump it. See [3. The Game Loop](03-game-loop.md).

---

## Starting a session

```csharp
var nm = new NetworkManager();          // sets NetworkManager.Instance

// Host
nm.StartServer(port: 7777, maxClients: 8);

// Client
nm.ConnectToServer("192.168.1.42", 7777);
```

```csharp
nm.IsServer;        // bool
nm.IsClient;        // bool
nm.IsRunning;       // bool
nm.LocalClientId;   // int, −1 until the server assigns one
nm.Ping;            // int ms, client only
nm.ConnectedClientIds;   // IEnumerable<int>, server only

nm.StopServer();
nm.Disconnect();
nm.KickClient(clientId, "cheating");
nm.Dispose();
```

Events:

```csharp
nm.OnClientConnected    += id => Debug.WriteLine($"client {id} joined");
nm.OnClientDisconnected += id => Debug.WriteLine($"client {id} left");
nm.OnConnectedToServer      += () => LoadGameScene();
nm.OnDisconnectedFromServer += () => ReturnToMenu();
nm.OnSpawnObject   += (netId, actorName, ownerId, state) => SpawnRemote(netId, actorName, ownerId, state);
nm.OnDespawnObject += netId => DespawnRemote(netId);
```

Both `StartServer` and `ConnectToServer` throw `InvalidOperationException` if
the manager is already running — call `StopServer()` / `Disconnect()` first.
Connections use the connection key `"SexyBiscuit"`.

### Pump it

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    NetworkManager.Instance?.Tick((float)gameTime.ElapsedGameTime.TotalSeconds);
}
```

`Tick` polls LiteNetLib events, drains the inbound queue, dispatches packets,
runs `ReplicationSystem.Tick`, and (on clients) sends a periodic ping probe and
refreshes `Ping`.

---

## NetworkObject

Every replicated actor needs one.

```csharp
var netObj = actor.AddComponent<NetworkObject>();
```

```csharp
netObj.NetworkId;      // uint, assigned by the server on Spawn
netObj.IsOwner;        // this peer has authority over the object
netObj.IsServer;       // this peer is the server
netObj.OwnerClientId;  // int, −1 for server-owned
```

### Spawning — server only

```csharp
NetworkObject.Spawn(actor);      // throws unless nm.IsServer
NetworkObject.Despawn(actor);
```

`Spawn` allocates a network id, registers the object with the replication and
RPC systems, snapshots its full state, and broadcasts a spawn packet carrying
`networkId`, `actor.Name`, `ownerClientId` and the state blob.

**Clients must construct the actor themselves** in response to
`OnSpawnObject` — the engine sends a name, not a prefab. Wire it to a factory:

```csharp
private readonly Dictionary<uint, Actor> _netActors = new();

nm.OnSpawnObject += (netId, actorName, ownerId, state) =>
{
    Actor actor = actorName switch
    {
        "Player"  => BuildPlayer(),
        "Bullet"  => BuildBullet(),
        _         => new Actor(actorName),
    };

    var no = actor.GetComponent<NetworkObject>() ?? actor.AddComponent<NetworkObject>();
    no.NetworkId     = netId;
    no.OwnerClientId = ownerId;
    no.IsOwner       = ownerId == nm.LocalClientId;
    no.IsServer      = false;

    SceneManager.ActiveScene!.AddActor(actor);
    nm.Replication.RegisterObject(no);
    nm.Rpc.Register(actor, no);
    _netActors[netId] = actor;
};

nm.OnDespawnObject += netId =>
{
    if (_netActors.Remove(netId, out var a)) a.Destroy();
};
```

That factory table is the single most important piece of networking glue you
will write. Keep it beside your spawn call so the two never drift.

---

## Replication

Mark fields or properties on any `Component` with `[Replicated]`.

```csharp
public sealed class PlayerState : Component
{
    [Replicated] public int   Health = 100;
    [Replicated] public int   Score;
    [Replicated(ReplicateCondition.OwnerOnly)] public float Stamina;
    [Replicated(ReplicateCondition.Always, priority: 10)] public Vector2 Position;
}
```

| `ReplicateCondition` | Meaning |
|---|---|
| `Always` | sent to every observer within `InterestRadius` on each dirty tick |
| `OwnerOnly` | only the owning client receives it — use for health, inventory, cooldowns |
| `InitialOnly` | sent once in the spawn snapshot, never again — use for immutable spawn data |

`priority` orders members within a packet: higher values are written first, so
they survive when bandwidth forces the system to shed data.

```csharp
nm.Replication.SendRate      = 20f;     // state updates per second, default 20
nm.Replication.InterestRadius = 2000f;  // world units; objects beyond are skipped
```

`ReplicationSystem.Tick` runs inside `NetworkManager.Tick`. The server collects
changed replicated members per object at `SendRate` and sends deltas; clients
apply them through `HandleIncomingStateUpdate`.

Replication is **server-authoritative and one-directional**: the server owns the
values, clients receive them. To let a client change state, use a `ServerRpc`.

Register and unregister objects explicitly when you are not going through
`NetworkObject.Spawn`:

```csharp
nm.Replication.RegisterObject(netObj);
nm.Replication.UnregisterObject(netObj);
```

---

## Remote procedure calls

Attribute methods on components attached to a networked actor.

```csharp
public sealed class Weapon : Component
{
    [ServerRpc]                       // requireOwnership: true by default
    public void FireServerRpc(float dirX, float dirY)
    {
        // Runs on the server. Validate here — never trust the caller.
        SpawnProjectile(new Vector2(dirX, dirY));
        RpcSystem.CallClientRpc(GetComponent<NetworkObject>()!, nameof(PlayFireFxClientRpc),
                                RpcTarget.All, dirX, dirY);
    }

    [ClientRpc(RpcTarget.All)]
    public void PlayFireFxClientRpc(float dirX, float dirY)
    {
        // Runs on every client (and on the server locally, for RpcTarget.All).
        SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/shoot.wav");
    }
}
```

Invoke them:

```csharp
var no = actor.GetComponent<NetworkObject>()!;

// From a client (or the server) → runs on the server
RpcSystem.CallServerRpc(no, nameof(Weapon.FireServerRpc), dir.X, dir.Y);

// From the server only → runs on clients
RpcSystem.CallClientRpc(no, nameof(Weapon.PlayFireFxClientRpc), RpcTarget.All, dir.X, dir.Y);
```

Semantics from the source:

- `CallServerRpc` **on the server invokes the method directly**, skipping the
  network. Same code path host and client.
- `CallClientRpc` **from a client logs an error and does nothing**.
- `RpcTarget` is `All` (every client, plus the server locally), `Owner` (only
  the owning client) or `Others` (everyone except the owner).
- `[ServerRpc(requireOwnership: false)]` allows any client to call it. Default
  is ownership-checked.
- If the manager is not running, both calls log to stderr and return — they do
  not throw. In single-player, RPC-based code paths silently do nothing.

Registration happens in `NetworkObject.Spawn`; do it manually otherwise:

```csharp
nm.Rpc.Register(actor, netObj);
nm.Rpc.Unregister(actor);
```

Naming convention: suffix `ServerRpc` / `ClientRpc` so the direction is obvious
at the call site, and use `nameof(...)` rather than string literals so renames
do not silently break dispatch.

---

## Raw messaging

When you want your own protocol:

```csharp
nm.SendToServer(bytes, DeliveryMethod.ReliableOrdered);
nm.SendToClient(clientId, bytes, DeliveryMethod.Unreliable);
nm.SendToAll(bytes, DeliveryMethod.ReliableUnordered);
```

`DeliveryMethod` is LiteNetLib's enum: `Unreliable`, `ReliableUnordered`,
`Sequenced`, `ReliableOrdered`, `ReliableSequenced`.

Rules of thumb: `Unreliable` for per-frame position streams, `ReliableOrdered`
for chat, inventory and anything a dropped packet would corrupt.

---

## LAN discovery

```csharp
// Host — advertise
LanDiscovery.StartBroadcast(port: 7778, new LanServerInfo
{
    ServerName  = "Ada's Game",
    Address     = LocalIPv4(),
    Port        = 7777,
    PlayerCount = 1,
    MaxPlayers  = 8,
    GameVersion = "1.0.0",
    CustomData  = { ["map"] = "forest" },
});

// Client — browse
LanDiscovery.Discover(port: 7778, onFound: info =>
{
    Debug.WriteLine($"{info.ServerName} {info.PlayerCount}/{info.MaxPlayers} @ {info.Address}:{info.Port}");
});

LanDiscovery.StopBroadcast();
LanDiscovery.StopDiscovery();
```

`onFound` is invoked from the discovery socket's thread. Marshal to the main
thread before touching the scene graph or UI:

```csharp
private readonly ConcurrentQueue<LanServerInfo> _found = new();
LanDiscovery.Discover(7778, info => _found.Enqueue(info));

protected override void Update(GameTime gt)
{
    base.Update(gt);
    while (_found.TryDequeue(out var info)) AddServerRow(info);
}
```

`LanServerInfo` is serialised with a source-generated JSON context, so it stays
allocation-light and trimming-safe.

---

## Diagnostics

```csharp
NetworkDiagnostics.Visible = true;
NetworkDiagnostics.RecordBytesIn(n);
NetworkDiagnostics.RecordBytesOut(n);

// in your loop
NetworkDiagnostics.Update(dt);
NetworkDiagnostics.Draw(SpriteBatch, new Vector2(10, 320));
```

Like all the debug tooling, it is not pumped for you.

---

## A minimal host/join flow

```csharp
public sealed class NetSession
{
    private NetworkManager? _nm;
    public NetworkManager? Manager => _nm;

    public void Host(int port = 7777)
    {
        _nm = new NetworkManager();
        _nm.OnClientConnected += id => SpawnPlayerFor(id);
        _nm.StartServer(port, maxClients: 8);

        LanDiscovery.StartBroadcast(7778, new LanServerInfo
        {
            ServerName = "Ada's Game", Address = LocalIPv4(), Port = port,
            MaxPlayers = 8, GameVersion = "1.0.0",
        });

        SpawnPlayerFor(-1);          // the host's own player
    }

    public void Join(string address, int port = 7777)
    {
        _nm = new NetworkManager();
        _nm.OnSpawnObject   += SpawnRemote;
        _nm.OnDespawnObject += DespawnRemote;
        _nm.ConnectToServer(address, port);
    }

    public void Tick(float dt) => _nm?.Tick(dt);

    public void Leave()
    {
        LanDiscovery.StopBroadcast();
        LanDiscovery.StopDiscovery();
        _nm?.Dispose();
        _nm = null;
    }

    private void SpawnPlayerFor(int clientId)
    {
        var scene = SBEngine.Instance.SceneManager.ActiveScene!;
        var actor = BuildPlayer();
        var no = actor.AddComponent<NetworkObject>();
        no.OwnerClientId = clientId;
        scene.AddActor(actor);
        NetworkObject.Spawn(actor);
    }

    // SpawnRemote / DespawnRemote: the factory table shown earlier.
}
```

---

## Design guidance

- **Server authority.** Clients send intent (`ServerRpc`); the server decides and
  replicates the result. Never let a client write its own health or score.
- **Replicate a little, predict a lot.** Send positions at 10–20 Hz and
  interpolate on the client rather than sending every frame.
- **`InterestRadius` is your bandwidth dial.** Most games do not need to
  replicate the whole world to every client.
- **Test with the host as a client.** `CallServerRpc` short-circuits on the
  server, so host-only bugs hide easily; always run one extra client.
- **Guard single-player.** `NetworkManager.Instance` is null until you construct
  one; RPCs no-op. Prefer code paths that work identically offline.

---

## Steam multiplayer

For Steam lobbies and matchmaking on top of this transport, see
[19. Steam](19-steam.md).

## Next

- [16. The Editor](16-editor.md)
- [Tutorial 16: Multiplayer](../tutorials/16-multiplayer.md)
