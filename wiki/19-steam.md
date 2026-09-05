# 19. Steam

Namespace: `SexyBiscuit.Engine.Steam` · via **Steamworks.NET 20.1.0**

Five static/singleton services:

| Type | Covers |
|---|---|
| `SteamManager` | init, shutdown, callback pump, overlay state |
| `SteamAchievements` | achievements and stats |
| `SteamCloud` | remote file storage |
| `SteamLobby` | lobbies, matchmaking, invites, rich presence |
| `SteamWorkshop` | UGC create, update, subscribe |

Everything is wrapped in `#if STEAMWORKS`. Without that symbol the classes
compile to **no-op stubs** returning empty values — so Steam calls are safe to
leave in a non-Steam build.

> `STEAMWORKS` is currently defined in **all three** configurations of
> `SexyBiscuit.Engine.csproj` (`Debug`, `Development`, `Release`). If you are not
> shipping on Steam, remove it from `DefineConstants` so the stubs are used.

---

## Lifecycle

```csharp
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        SteamManager.Init(480);          // 480 = Spacewar, Valve's test app
        SBEngine.Run(config);
        SteamManager.Shutdown();
    }
}
```

`Init` needs **Steam running** and either a `steam_appid.txt` in the working
directory containing your app id, or the `SteamAppId` environment variable set
before the call. On failure it logs to stderr, leaves `IsInitialised == false`,
and returns — the game keeps running.

```csharp
bool ok         = SteamManager.Instance?.IsInitialised == true;
uint appId      = SteamManager.AppId;
string persona  = SteamManager.PersonaName;
bool overlayUp  = SteamManager.IsOverlayActive;
SteamManager.TriggerScreenshot();
```

### Pump the callbacks

`SteamAPI.RunCallbacks()` is wrapped by `SteamManager.Update()`, and **nothing
calls it for you**:

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    SteamManager.Instance?.Update();
}
```

Without this, no Steam callback ever fires — achievements never confirm, lobby
events never arrive, and `SteamLobby`'s events stay silent.

### Pause on overlay

```csharp
if (SteamManager.IsOverlayActive && !_paused) Pause();
```

Required for Steam Deck verification, and simply good manners.

---

## Achievements and stats

```csharp
SteamAchievements.OnStatsReceived += () => RefreshAchievementUi();
SteamAchievements.RequestStats();          // call once after Init

SteamAchievements.Unlock("ACH_FIRST_BISCUIT");
SteamAchievements.SetProgress("ACH_100_KILLS", current: 42, max: 100);

SteamAchievements.SetStat("total_kills", 42);
SteamAchievements.SetStat("play_time", 3600f);
int kills   = SteamAchievements.GetStatInt("total_kills");
float hours = SteamAchievements.GetStatFloat("play_time");
```

Ids must match the App Admin configuration exactly. `RequestStats` is
asynchronous — wait for `OnStatsReceived` before reading stats or showing
progress, and remember the callback only fires if you are pumping
`SteamManager.Update()`.

`SetProgress` drives Steam's progress-notification toast; it does **not**
unlock. Call `Unlock` when the goal is reached.

Mirror achievements locally so the game behaves the same without Steam:

```csharp
void Award(string id)
{
    if (PlayerPrefs.GetBool($"ach.{id}")) return;
    PlayerPrefs.SetBool($"ach.{id}", true);
    PlayerPrefs.Save();
    SteamAchievements.Unlock(id);          // no-op without Steam
}
```

---

## Cloud saves

```csharp
byte[] data = Encoding.UTF8.GetBytes(json);
bool wrote  = SteamCloud.Write("save_0.json", data);

byte[]? back = SteamCloud.Read("save_0.json");
bool exists  = SteamCloud.Exists("save_0.json");
SteamCloud.Delete("save_0.json");

var (used, total) = SteamCloud.GetQuota();
string[] files    = SteamCloud.ListFiles();
```

A save layer that prefers the cloud and falls back to disk:

```csharp
public static class Saves
{
    public static void Write<T>(int slot, T data)
    {
        string json = JsonSerializer.Serialize(data);
        if (!SteamCloud.Write($"save_{slot}.json", Encoding.UTF8.GetBytes(json)))
            SaveManager.Save(slot, data);
    }

    public static T? Read<T>(int slot)
    {
        var bytes = SteamCloud.Read($"save_{slot}.json");
        if (bytes is { Length: > 0 })
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes));
        return SaveManager.Load<T>(slot);
    }
}
```

Enable Steam Cloud in App Admin and declare the file paths there, or writes
succeed locally but never sync.

---

## Lobbies

The API is **event-driven**, not `async`. Subscribe first, then call.

```csharp
SteamLobby.OnLobbyCreated += (success, lobbyId) =>
{
    if (!success) { ShowError("Could not create lobby"); return; }

    SteamLobby.SetLobbyData("map",  "forest");
    SteamLobby.SetLobbyData("mode", "coop");

    // Start the game server and publish its address for joiners.
    _net = new NetworkManager();
    _net.StartServer(7777);
    SteamLobby.SetLobbyData("address", $"{LocalIPv4()}:7777");

    SteamLobby.SetRichPresence("status", "Hosting on Forest");
};

SteamLobby.OnLobbyJoined += success =>
{
    if (!success) { ShowError("Could not join"); return; }
    string address = SteamLobby.GetLobbyData(SteamLobby.CurrentLobby, "address");
    var parts = address.Split(':');
    _net = new NetworkManager();
    _net.ConnectToServer(parts[0], int.Parse(parts[1]));
};

SteamLobby.OnLobbiesFound += ids =>
{
    foreach (ulong id in ids) AddLobbyRow(id);
};

SteamLobby.OnLobbyJoinRequested += lobbyId => SteamLobby.JoinLobby(lobbyId);
```

```csharp
SteamLobby.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxPlayers: 4);
SteamLobby.FindLobbies();
SteamLobby.JoinLobby(lobbyId);          // CSteamID or ulong
SteamLobby.LeaveLobby();
SteamLobby.InviteFriend(friendSteamId);
SteamLobby.SetRichPresence("connect", $"+connect {address}");
```

`OnLobbyJoinRequested` is the "friend clicked Join in the overlay" path —
handling it is what makes invites work.

> Every method has both a `CSteamID`/`ELobbyType` overload (real build) and a
> `ulong`/int overload (stub build). Prefer the `ulong` overloads in your game
> code so it compiles with and without `STEAMWORKS`.

The design document shows `await SteamLobby.CreateLobby(...)` and a
`FindLobbies(filter => …)` builder. Neither exists — the implemented API is the
event-based one above.

---

## Workshop

```csharp
SteamWorkshop.OnItemCreated += (success, publishedFileId) =>
{
    if (!success) return;
    SteamWorkshop.SubmitUpdate(
        publishedFileId,
        title:            "My Level Pack",
        description:      "Five new levels.",
        contentPath:      "Mods/MyPack",
        previewImagePath: "Mods/MyPack/preview.png",
        tags:             new[] { "Levels" });
};

SteamWorkshop.OnItemSubmitted += success => ShowToast(success ? "Uploaded" : "Failed");

SteamWorkshop.OnSubscribedItemsLoaded += paths =>
{
    foreach (var path in paths)
        AssetBundle.Mount(Path.Combine(path, "content.sbb"));
};

SteamWorkshop.CreateItem(appId);
SteamWorkshop.GetSubscribedItems();
string install = SteamWorkshop.GetItemInstallPath(publishedFileId);
```

Workshop content pairs naturally with
[asset bundles](13-assets.md#asset-bundles): mount each subscribed item's
archive before the base bundle so mod assets override shipped ones.

---

## Steam multiplayer, end to end

```csharp
public sealed class SteamSession
{
    private NetworkManager? _net;

    public void Init()
    {
        SteamManager.Init(YourAppId);
        SteamAchievements.RequestStats();

        SteamLobby.OnLobbyCreated       += (ok, id) => { if (ok) HostGame(); };
        SteamLobby.OnLobbyJoined        += ok       => { if (ok) JoinGame(); };
        SteamLobby.OnLobbyJoinRequested += id       => SteamLobby.JoinLobby(id);
    }

    public void Tick(float dt)
    {
        SteamManager.Instance?.Update();     // Steam callbacks
        _net?.Tick(dt);                      // game networking
    }

    public void Host()   => SteamLobby.CreateLobby(4);
    public void Browse() => SteamLobby.FindLobbies();
    public void Leave()  { SteamLobby.LeaveLobby(); _net?.Dispose(); _net = null; }

    private void HostGame()
    {
        _net = new NetworkManager();
        _net.StartServer(7777);
        SteamLobby.SetLobbyData("address", $"{LocalIPv4()}:7777");
        SteamLobby.SetRichPresence("status", "In a match");
    }

    private void JoinGame()
    {
        var parts = SteamLobby.GetLobbyData(SteamLobby.CurrentLobby, "address").Split(':');
        _net = new NetworkManager();
        _net.ConnectToServer(parts[0], int.Parse(parts[1]));
    }

    public void Shutdown() { Leave(); SteamManager.Shutdown(); }
}
```

Note that this uses **direct IP** connection carried over lobby metadata, which
only works on a LAN or with port forwarding. Steam's relay (`SteamNetworkingSockets`)
is not wired into the transport — adding it means writing a LiteNetLib-shaped
adapter over the Steam socket API.

---

## Shipping notes

- `steam_appid.txt` beside the executable during development. **Do not ship it**
  — a shipping build gets its app id from the Steam client.
- Test with Steam running and your account owning the app.
- Achievement and stat ids come from App Admin; typos fail silently.
- Enable Cloud in App Admin and declare the file paths before relying on it.
- Handle `IsOverlayActive` by pausing.
- Build with `--platform steam-windows|steam-linux|steam-macos` so the pipeline
  writes `app_build.vdf`; upload with `steamcmd` yourself. See
  [18. Build & Export](18-build-export.md#steam).

---

## Next

- [20. API Index](20-api-index.md)
- [21. Gotchas](21-gotchas.md)
