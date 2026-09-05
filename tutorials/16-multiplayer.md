# Tutorial 16 — Multiplayer

**You will build:** a host/join flow with a LAN server browser, replicated
players, server-authoritative shooting, and client interpolation.
**Time:** ~60 minutes.

Builds on [Tutorial 14](14-gameplay-framework.md).

---

## 1. Architecture

Client–server over **LiteNetLib** (UDP, reliable and unreliable channels). One
peer is the server — it may also be a player (a listen server).

```
NetworkManager        transport, connections, packet dispatch
├── ReplicationSystem  [Replicated] members → periodic state packets
└── RpcSystem          [ServerRpc] / [ClientRpc] methods
NetworkObject         per-actor identity and ownership
LanDiscovery          UDP broadcast server browser
```

**The server is authoritative.** Clients send *intent* through a `ServerRpc`;
the server decides and replicates the result. Never let a client write its own
health or score.

## 2. Pump it

`NetworkManager.Tick` is not called by the engine.

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);

    // Networking runs on UNSCALED time — never pause the network.
    NetworkManager.Instance?.Tick(Time.UnscaledDeltaTime);

    // … the rest of Tutorial 1's Update …
}
```

`Tick` polls LiteNetLib, drains the inbound queue, dispatches packets, runs
replication, and (on clients) refreshes `Ping`.

## 3. A session wrapper

`MyGame/Net/NetSession.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Networking;

namespace MyGame.Net;

public sealed class NetSession : IDisposable
{
    public const int GamePort      = 7777;
    public const int DiscoveryPort = 7778;

    public NetworkManager? Manager { get; private set; }
    public bool IsServer => Manager?.IsServer == true;
    public bool IsClient => Manager?.IsClient == true;
    public bool IsRunning => Manager?.IsRunning == true;

    private readonly Game _game;
    private readonly Dictionary<uint, Actor> _netActors = new();

    public NetSession(Game game) => _game = game;

    // -----------------------------------------------------------------------
    public void Host(string serverName, int maxPlayers = 8)
    {
        Manager = new NetworkManager();

        Manager.OnClientConnected    += id => SpawnPlayerFor(id);
        Manager.OnClientDisconnected += id => DespawnPlayerFor(id);

        Manager.StartServer(GamePort, maxPlayers);

        LanDiscovery.StartBroadcast(DiscoveryPort, new LanServerInfo
        {
            ServerName  = serverName,
            Address     = LocalIPv4(),
            Port        = GamePort,
            PlayerCount = 1,
            MaxPlayers  = maxPlayers,
            GameVersion = "1.0.0",
        });

        SpawnPlayerFor(-1);        // the host's own player; -1 = server-owned
    }

    public void Join(string address)
    {
        Manager = new NetworkManager();

        Manager.OnConnectedToServer      += () => Debug.WriteLine("connected");
        Manager.OnDisconnectedFromServer += () => Scenes.MainMenuScene.Load(_game);
        Manager.OnSpawnObject            += SpawnRemote;
        Manager.OnDespawnObject          += DespawnRemote;

        Manager.ConnectToServer(address, GamePort);
    }

    public void Tick(float dt) => Manager?.Tick(dt);

    public void Leave()
    {
        LanDiscovery.StopBroadcast();
        LanDiscovery.StopDiscovery();

        foreach (var actor in _netActors.Values) actor.Destroy();
        _netActors.Clear();

        Manager?.Dispose();
        Manager = null;
    }

    public void Dispose() => Leave();

    // -----------------------------------------------------------------------
    // Server side
    // -----------------------------------------------------------------------
    private void SpawnPlayerFor(int clientId)
    {
        if (Manager is not { IsServer: true }) return;
        var scene = _game.SceneManager.ActiveScene;
        if (scene == null) return;

        var actor = NetFactories.CreatePlayer(_game, clientId);
        var no = actor.GetComponent<NetworkObject>()!;
        no.OwnerClientId = clientId;

        scene.AddActor(actor);
        NetworkObject.Spawn(actor);            // allocates the id and broadcasts

        _netActors[no.NetworkId] = actor;
    }

    private void DespawnPlayerFor(int clientId)
    {
        foreach (var (id, actor) in _netActors.ToArray())
        {
            if (actor.GetComponent<NetworkObject>()?.OwnerClientId != clientId) continue;
            NetworkObject.Despawn(actor);
            actor.Destroy();
            _netActors.Remove(id);
        }
    }

    // -----------------------------------------------------------------------
    // Client side — the spawn factory table
    // -----------------------------------------------------------------------
    private void SpawnRemote(uint netId, string actorName, int ownerId, byte[] state)
    {
        var scene = _game.SceneManager.ActiveScene;
        if (scene == null || Manager == null) return;

        // The engine sends a NAME, not a prefab. You map names to factories.
        Actor actor = actorName switch
        {
            "Player" => NetFactories.CreatePlayer(_game, ownerId),
            "Bullet" => NetFactories.CreateBullet(_game),
            _        => new Actor(actorName),
        };

        var no = actor.GetComponent<NetworkObject>() ?? actor.AddComponent<NetworkObject>();
        no.NetworkId     = netId;
        no.OwnerClientId = ownerId;
        no.IsOwner       = ownerId == Manager.LocalClientId;
        no.IsServer      = false;

        scene.AddActor(actor);
        Manager.Replication.RegisterObject(no);
        Manager.Rpc.Register(actor, no);

        _netActors[netId] = actor;
    }

    private void DespawnRemote(uint netId)
    {
        if (_netActors.Remove(netId, out var actor)) actor.Destroy();
    }

    // -----------------------------------------------------------------------
    public static string LocalIPv4()
    {
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                return ip.ToString();
        return "127.0.0.1";
    }
}
```

**The factory table in `SpawnRemote` is the most important piece of networking
glue you will write.** The engine broadcasts `actor.Name`, not a prefab, so
clients must know how to build each kind of actor. Keep the table next to the
spawn call so the two never drift.

## 4. NetworkObject

```csharp
var no = actor.AddComponent<NetworkObject>();

no.NetworkId;       // uint, assigned by the server on Spawn
no.IsOwner;         // this peer has authority
no.IsServer;
no.OwnerClientId;   // −1 = server-owned
```

```csharp
NetworkObject.Spawn(actor);      // SERVER ONLY — throws otherwise
NetworkObject.Despawn(actor);
```

`Spawn` allocates a network id, registers with replication and RPC, snapshots the
full state, and broadcasts `networkId`, `actor.Name`, `ownerClientId` and the
state blob.

## 5. Replication

Mark fields or properties on any component:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Networking;

namespace MyGame.Net;

public sealed class NetPlayerState : Component
{
    [Replicated] public Vector2 NetPosition;
    [Replicated] public float   NetRotation;
    [Replicated] public int     Health = 100;
    [Replicated] public int     Score;

    [Replicated(ReplicateCondition.OwnerOnly)] public int Ammo = 30;
    [Replicated(ReplicateCondition.InitialOnly)] public string DisplayName = "Player";
}
```

| Condition | Meaning |
|---|---|
| `Always` | to every observer within `InterestRadius`, on each dirty tick |
| `OwnerOnly` | only the owning client — health, inventory, cooldowns |
| `InitialOnly` | once in the spawn snapshot — immutable spawn data |

```csharp
Manager.Replication.SendRate       = 20f;      // updates per second
Manager.Replication.InterestRadius = 2000f;    // world units; beyond this is skipped
```

`InterestRadius` is your bandwidth dial. Most games do not need to replicate the
whole world to every client.

Replication is **server-authoritative and one-directional**: the server owns the
values, clients receive them. To let a client change state, use a `ServerRpc`.

## 6. Client interpolation

Twenty updates per second is 50 ms between snapshots. Without interpolation
remote players teleport; with it, they glide.

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Networking;

namespace MyGame.Net;

/// <summary>
/// On the owner, writes the local transform into replicated fields.
/// On everyone else, smooths toward the replicated values.
/// </summary>
public sealed class NetTransformSync : Component
{
    public float SmoothingHalfLife = 0.05f;
    public float TeleportDistance  = 400f;      // beyond this, snap instead of glide

    private NetworkObject? _net;
    private NetPlayerState? _state;

    public override void Start()
    {
        _net   = GetComponent<NetworkObject>();
        _state = GetComponent<NetPlayerState>();
    }

    public override void Update(float dt)
    {
        if (_net == null || _state == null) return;

        if (_net.IsOwner || _net.IsServer)
        {
            // Authority: publish.
            _state.NetPosition = Transform.Position;
            _state.NetRotation = Transform.Rotation;
        }
        else
        {
            // Remote: interpolate.
            if (Vector2.Distance(Transform.Position, _state.NetPosition) > TeleportDistance)
            {
                Transform.Position = _state.NetPosition;
                Transform.Rotation = _state.NetRotation;
                return;
            }

            Transform.Position = SBMath.Damp(Transform.Position, _state.NetPosition,
                                             SmoothingHalfLife, dt);
            Transform.Rotation = MathHelper.ToRadians(
                SBMath.LerpAngle(MathHelper.ToDegrees(Transform.Rotation),
                                 MathHelper.ToDegrees(_state.NetRotation),
                                 1f - MathF.Pow(0.5f, dt / SmoothingHalfLife)));
        }
    }
}
```

`SBMath.Damp` is framerate-independent, so remote players move identically at
30 and 144 fps. `SBMath.LerpAngle` takes the shortest way round the circle,
which stops a player spinning 350° to turn 10°.

The teleport threshold matters: after a stall or a packet-loss burst, gliding
across half the map looks far worse than a snap.

## 7. RPCs

```csharp
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Networking;

namespace MyGame.Net;

public sealed class NetWeapon : Component
{
    public float Cooldown = 0.25f;
    private float _timer;

    public override void Update(float dt)
    {
        _timer -= dt;

        var no = GetComponent<NetworkObject>();
        if (no is not { IsOwner: true }) return;

        var input = SBEngine.Instance.Input;
        if (!input.IsPressed("Attack") || _timer > 0f) return;

        _timer = Cooldown;

        var dir = new Vector2(MathF.Cos(Transform.Rotation), MathF.Sin(Transform.Rotation));
        RpcSystem.CallServerRpc(no, nameof(FireServerRpc), dir.X, dir.Y);
    }

    [ServerRpc]                                  // requireOwnership: true by default
    public void FireServerRpc(float dirX, float dirY)
    {
        // Runs on the SERVER. Validate here — never trust the caller.
        if (_timer > 0f) return;
        _timer = Cooldown;

        SpawnBullet(new Vector2(dirX, dirY));

        var no = GetComponent<NetworkObject>()!;
        RpcSystem.CallClientRpc(no, nameof(PlayFireFxClientRpc), RpcTarget.All, dirX, dirY);
    }

    [ClientRpc(RpcTarget.All)]
    public void PlayFireFxClientRpc(float dirX, float dirY)
    {
        // Runs on every client, and locally on the server for RpcTarget.All.
        SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/shoot.wav");
        Systems.Effects.Muzzle(Actor.Scene!, Transform.Position, MathF.Atan2(dirY, dirX));
    }
}
```

Semantics from the source:

- **`CallServerRpc` on the server invokes the method directly**, skipping the
  network. Same code path host and client.
- **`CallClientRpc` from a client logs an error and does nothing.**
- `RpcTarget` is `All` (every client plus the server locally), `Owner` (only the
  owning client) or `Others` (everyone except the owner).
- `[ServerRpc(requireOwnership: false)]` allows any client to call it.
- If the manager is not running, both calls log and return — **they do not
  throw**, so RPC-based code paths silently do nothing in single-player.

Suffix your methods `ServerRpc` / `ClientRpc` so the direction is obvious at the
call site, and use `nameof(...)` so renames do not silently break dispatch.

## 8. A LAN server browser

```csharp
using System.Collections.Concurrent;
using SexyBiscuit.Engine.Networking;

// Host — advertise
LanDiscovery.StartBroadcast(NetSession.DiscoveryPort, new LanServerInfo
{
    ServerName  = "Ada's Game",
    Address     = NetSession.LocalIPv4(),
    Port        = NetSession.GamePort,
    PlayerCount = 1,
    MaxPlayers  = 8,
    GameVersion = "1.0.0",
    CustomData  = { ["map"] = "arena" },
});

// Client — browse. onFound fires on the discovery socket's thread.
private readonly ConcurrentQueue<LanServerInfo> _found = new();

LanDiscovery.Discover(NetSession.DiscoveryPort, info => _found.Enqueue(info));

protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    while (_found.TryDequeue(out var info)) AddServerRow(info);   // main thread
}

LanDiscovery.StopBroadcast();
LanDiscovery.StopDiscovery();
```

The callback runs on a background thread — **marshal to the main thread before
touching the scene graph or UI**, or you will get intermittent crashes that are
miserable to reproduce.

```csharp
private void AddServerRow(LanServerInfo info)
{
    string label = $"{info.ServerName}  {info.PlayerCount}/{info.MaxPlayers}  {info.Address}";
    var btn = MakeButton(label, Vector2.Zero, new Vector2(560, 52),
                         () => _session.Join(info.Address));
    _serverList.AddChild(btn);
}
```

## 9. A lobby scene

```csharp
public static class LobbyScene
{
    public static void Load(Game game)
    {
        var scene  = game.SceneManager.CreateScene("Lobby");
        var canvas = game.CreateCanvas(scene, "LobbyUI");

        var title = canvas.AddWidget<Label>();
        title.Text = "MULTIPLAYER";
        title.Position = new Vector2(0, 60);
        title.Size = new Vector2(1280, 48);
        title.Alignment = TextAlignment.Center;

        canvas.AddWidget(game.MakeButton("Host Game", new Vector2(80, 140),
            new Vector2(240, 56), () =>
            {
                game.Session.Host($"{game.PlayerName}'s Game");
                ArenaScene.Load(game);
            }));

        canvas.AddWidget(game.MakeButton("Refresh", new Vector2(80, 210),
            new Vector2(240, 56), () => game.RefreshServerList()));

        var list = canvas.AddWidget<Panel>();
        list.Position          = new Vector2(360, 140);
        list.Size              = new Vector2(840, 480);
        list.BackgroundTexture = game.WhiteTexture;
        list.BackgroundColor   = new Color(12, 14, 24, 220);
        list.LayoutMode        = PanelLayoutMode.Vertical;
        list.Padding           = 8f;
        game.SetServerList(list);

        game.RefreshServerList();
    }
}
```

## 10. Raw messaging

For your own protocol:

```csharp
Manager.SendToServer(bytes, DeliveryMethod.ReliableOrdered);
Manager.SendToClient(clientId, bytes, DeliveryMethod.Unreliable);
Manager.SendToAll(bytes, DeliveryMethod.ReliableUnordered);
```

`Unreliable` for per-frame position streams; `ReliableOrdered` for chat,
inventory, and anything a dropped packet would corrupt.

A chat system:

```csharp
public static class Chat
{
    private const byte MessageType = 200;

    public static void Send(string text)
    {
        var nm = NetworkManager.Instance;
        if (nm is not { IsRunning: true }) return;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(MessageType);
        bw.Write(nm.LocalClientId);
        bw.Write(text);
        bw.Flush();

        if (nm.IsServer) nm.SendToAll(ms.ToArray());
        else             nm.SendToServer(ms.ToArray());
    }
}
```

## 11. Diagnostics

```csharp
using SexyBiscuit.Engine.Debug;

NetworkDiagnostics.Visible = true;
NetworkDiagnostics.RecordBytesIn(packet.Length);     // call these yourself
NetworkDiagnostics.RecordBytesOut(packet.Length);

NetworkDiagnostics.Update(dt);
NetworkDiagnostics.Draw(SpriteBatch, new Vector2(10, 320));
```

Byte counters are **not** recorded automatically — wire them into your own send
and receive paths or the throughput readout stays at zero.

```csharp
Debug.WriteLine($"ping {Manager!.Ping}ms, {Manager.ConnectedClientIds.Count()} clients");
```

## 12. Testing

Run two instances on one machine:

```bash
dotnet run --project MyGame                      # host
dotnet run --project MyGame -- --join 127.0.0.1  # client
```

```csharp
private static void Main(string[] args)
{
    using var game = new Game();
    game.AutoJoinAddress = args.Contains("--join")
        ? args[Array.IndexOf(args, "--join") + 1]
        : null;
    game.Run();
}
```

**Always test with a host plus at least one separate client.** `CallServerRpc`
short-circuits on the server, so host-only bugs — code that works because it
happens to run locally — hide easily.

Simulate poor networks with LiteNetLib's simulation settings on the underlying
`NetManager`, or with `clumsy` (Windows) / `dummynet` (macOS, Linux). Code that
works only on localhost is not networked code.

## 13. Design rules

- **Server authority.** Clients send intent; the server decides. `[Replicated]`
  flows server → client only.
- **Validate every `ServerRpc`.** Cooldowns, ranges, ammo, line-of-sight. A
  client can send anything.
- **Replicate little, predict a lot.** 10–20 Hz plus interpolation beats 60 Hz
  of raw positions.
- **`InterestRadius` before anything else** when bandwidth becomes a problem.
- **Guard single-player.** `NetworkManager.Instance` is null until you construct
  one, and RPCs no-op — prefer code paths that work identically offline.
- **Never pause the network.** Tick it on `Time.UnscaledDeltaTime`.

## 14. Steam multiplayer

For friend invites and matchmaking, [`SteamLobby`](../wiki/19-steam.md) carries
the host's address in lobby metadata and this transport does the rest:

```csharp
SteamLobby.OnLobbyCreated += (ok, id) =>
{
    if (!ok) return;
    _session.Host("Steam Game");
    SteamLobby.SetLobbyData("address", $"{NetSession.LocalIPv4()}:{NetSession.GamePort}");
};

SteamLobby.OnLobbyJoined += ok =>
{
    if (!ok) return;
    var addr = SteamLobby.GetLobbyData(SteamLobby.CurrentLobby, "address");
    _session.Join(addr.Split(':')[0]);
};

SteamLobby.OnLobbyJoinRequested += id => SteamLobby.JoinLobby(id);
```

Remember `SteamManager.Instance?.Update()` in your loop, or no Steam callback
ever fires.

Note this is **direct IP** carried over lobby metadata, which works on a LAN or
with port forwarding. Steam's relay (`SteamNetworkingSockets`) is not wired into
the transport — adding it means writing a LiteNetLib-shaped adapter over the
Steam socket API.

---

## Checkpoint

You have:

- A session wrapper with host, join, and a LAN browser
- The client spawn factory table
- Replicated state with interest management
- Server-authoritative RPCs with client-side effects
- Interpolation that is framerate-independent

## Troubleshooting

| Symptom | Cause |
|---|---|
| Nothing sends or receives | `NetworkManager.Tick(dt)` not called |
| Client sees no other players | `OnSpawnObject` not handled, or a missing factory entry |
| Remote players teleport | no interpolation, or `SendRate` too low |
| `InvalidOperationException` on start | already running — `StopServer()` / `Disconnect()` first |
| RPC does nothing | manager not running, or `CallClientRpc` from a client |
| Works on the host, broken on the client | testing host-only; `CallServerRpc` short-circuits there |
| Crash when a server is found | `LanDiscovery` callback touching the scene off-thread |

## Exercises

1. Add a scoreboard replicated through `PlayerState` with `[Replicated]` fields.
2. Add client-side prediction for the local player: move immediately, reconcile
   against the server position when it disagrees by more than a threshold.
3. Add a kick vote using `ReliableOrdered` raw messages.

---

**Next:** [Tutorial 17 — Editor Workflow](17-editor-workflow.md)
