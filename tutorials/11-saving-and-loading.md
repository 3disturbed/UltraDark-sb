# Tutorial 11 — Saving & Loading

**You will build:** save slots with metadata, a settings layer, a per-user data
directory, and autosave. **Time:** ~30 minutes.

Builds on [Tutorial 10](10-scenes-and-prefabs.md).

---

## 1. Two services

| | `SaveManager` | `PlayerPrefs` |
|---|---|---|
| For | game state, slots | settings, small key/values |
| Format | JSON (optionally AES-256) or binary | one JSON dictionary |
| Default path | `Saves/save_{slot}.json` | `Saves/prefs.json` |

Both default to paths **relative to the working directory**. That fails on a
read-only install location — Program Files, a locked Steam library, a macOS app
bundle — so fix it before anything else.

## 2. A per-user data directory

`MyGame/UserData.cs`:

```csharp
using SexyBiscuit.Engine.Save;

namespace MyGame;

public static class UserData
{
    public const string Company = "MyStudio";
    public const string Product = "MyGame";

    /// <summary>%APPDATA% on Windows, ~/.config on Linux, ~/Library/Application Support on macOS.</summary>
    public static string Root
    {
        get
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create);

            string dir = Path.Combine(appData, Company, Product);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Saves    => Path.Combine(Root, "Saves");
    public static string Prefs    => Path.Combine(Root, "prefs.json");
    public static string Bindings => Path.Combine(Root, "bindings.json");
    public static string Logs     => Path.Combine(Root, "Logs");

    /// <summary>Point every persistence service at the user directory. Call once at boot.</summary>
    public static void Install()
    {
        Directory.CreateDirectory(Saves);
        SaveManager.SaveDirectory = Saves + Path.DirectorySeparatorChar;
        PlayerPrefs.PrefsPath     = Prefs;
    }
}
```

```csharp
protected override void OnEngineReady()
{
    UserData.Install();
    ApplySettings();
    // …
}
```

Setting `PlayerPrefs.PrefsPath` clears its loaded flag, so the next access reads
from the new location.

## 3. Settings with PlayerPrefs

```csharp
using SexyBiscuit.Engine.Save;

PlayerPrefs.SetFloat("vol.master", 0.8f);
PlayerPrefs.SetInt("difficulty", 2);
PlayerPrefs.SetString("lang", "en");
PlayerPrefs.SetBool("fullscreen", true);

float v  = PlayerPrefs.GetFloat("vol.master", 1f);
int   d  = PlayerPrefs.GetInt("difficulty", 1);
string l = PlayerPrefs.GetString("lang", "en");
bool  fs = PlayerPrefs.GetBool("fullscreen", false);

PlayerPrefs.HasKey("difficulty");
PlayerPrefs.DeleteKey("difficulty");
PlayerPrefs.DeleteAll();

PlayerPrefs.Save();      // ← nothing is written until you call this
```

**`Save()` is not automatic.** Call it when the player leaves a settings screen,
and again on shutdown.

Apply settings at boot, before the first scene exists:

```csharp
private void ApplySettings()
{
    Audio.Master.Volume = PlayerPrefs.GetFloat("vol.master", 1f);
    Audio.Music.Volume  = PlayerPrefs.GetFloat("vol.music",  0.7f);
    Audio.SFX.Volume    = PlayerPrefs.GetFloat("vol.sfx",    1f);

    if (PlayerPrefs.GetBool("fullscreen", false))
    {
        Graphics.IsFullScreen = true;
        Graphics.ApplyChanges();
    }

    // LoadBindings throws when the file is missing.
    if (File.Exists(UserData.Bindings))
        Input.LoadBindings(UserData.Bindings);
}

protected override void UnloadContent()
{
    PlayerPrefs.Save();
    base.UnloadContent();
}
```

Two behaviours worth knowing about bindings:

- `LoadBindings` **merges** — actions in the file replace the current ones,
  actions absent from it keep their defaults. A file containing only what the
  player remapped is valid and preferable.
- `RebindAction` **replaces** an action's whole binding list with the single
  binding you pass.

## 4. A save model

Design the save around **plain data**, not live objects. `SceneSerializer`
cannot capture a live scene usefully — it drops textures and every runtime
reference.

`MyGame/Saves/GameSave.cs`:

```csharp
namespace MyGame.Saves;

public sealed class GameSave
{
    public int      Version    { get; set; } = 1;
    public string   PlayerName { get; set; } = "Player";
    public DateTime SavedAt    { get; set; } = DateTime.UtcNow;
    public float    PlayTime   { get; set; }

    public int     Level     { get; set; } = 1;
    public float[] Position  { get; set; } = new float[2];
    public int     Health    { get; set; } = 100;
    public int     MaxHealth { get; set; } = 100;
    public int     Score     { get; set; }

    public List<string>            Inventory = new();
    public Dictionary<string, bool> Flags    = new();
    public HashSet<string>          LevelsCompleted = new();
}
```

`System.Text.Json` rules apply: public get/set properties (or public fields), a
parameterless constructor, and nothing cyclic.

The `Version` field earns its place the first time you change the model.

## 5. Capture and restore

`MyGame/Saves/SaveSystem.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Save;
using MyGame.Components;
using MyGame.Scenes;

namespace MyGame.Saves;

public static class SaveSystem
{
    public static float PlayTime;
    public static int   Score;
    public static int   CurrentLevel = 1;
    public static readonly List<string> Inventory = new();
    public static readonly Dictionary<string, bool> Flags = new();

    // ---- capture ---------------------------------------------------------
    public static GameSave Capture(string playerName = "Player")
    {
        var scene  = SBEngine.Instance.SceneManager.ActiveScene;
        var player = scene?.FindByTag("Player").FirstOrDefault();
        var health = player?.GetComponent<Health>();

        return new GameSave
        {
            PlayerName = playerName,
            SavedAt    = DateTime.UtcNow,
            PlayTime   = PlayTime,
            Level      = CurrentLevel,
            Position   = player != null
                ? new[] { player.Transform.Position.X, player.Transform.Position.Y }
                : new float[2],
            Health     = health?.Current ?? 100,
            MaxHealth  = health?.Max     ?? 100,
            Score      = Score,
            Inventory  = new List<string>(Inventory),
            Flags      = new Dictionary<string, bool>(Flags),
        };
    }

    // ---- restore ---------------------------------------------------------
    public static void Apply(Game game, GameSave save)
    {
        save = Migrate(save);

        PlayTime     = save.PlayTime;
        Score        = save.Score;
        CurrentLevel = save.Level;

        Inventory.Clear(); Inventory.AddRange(save.Inventory);
        Flags.Clear();     foreach (var (k, v) in save.Flags) Flags[k] = v;

        // Build the level the same way a new game does, then overlay the save.
        GameScene.Load(game, save.Level);

        var scene  = game.SceneManager.ActiveScene!;
        var player = scene.FindByTag("Player").First();
        player.Transform.Position = new Vector2(save.Position[0], save.Position[1]);

        var health = player.GetComponent<Health>();
        if (health != null)
        {
            health.Max = save.MaxHealth;
            health.Reset();
            health.Damage(save.MaxHealth - save.Health);
        }
    }

    // ---- versioning ------------------------------------------------------
    private static GameSave Migrate(GameSave save)
    {
        if (save.Version < 1)
        {
            save.MaxHealth = 100;
            save.Version   = 1;
        }
        return save;
    }

    // ---- slots -----------------------------------------------------------
    public static void SaveToSlot(int slot, string playerName = "Player")
        => SaveManager.Save(slot, Capture(playerName));

    public static bool LoadSlot(Game game, int slot)
    {
        var save = SaveManager.Load<GameSave>(slot);
        if (save == null) return false;
        Apply(game, save);
        return true;
    }

    public static void Tick(float dt) => PlayTime += dt;
}
```

Restoring by **rebuilding the level and overlaying the save** keeps one code
path warm instead of two. A separate "load a saved level" path is where
divergence bugs breed.

## 6. Slot management

```csharp
SaveManager.SaveDirectory = UserData.Saves + Path.DirectorySeparatorChar;

SaveManager.Save(0, save);
GameSave? loaded = SaveManager.Load<GameSave>(0);   // null when the slot is empty
bool exists      = SaveManager.SlotExists(0);
SaveManager.Delete(0);

foreach (SaveSlotInfo info in SaveManager.GetSaveSlots())
    Debug.WriteLine($"slot {info.Slot} — {info.SavedAt:g} — {info.ByteSize} bytes");
```

`GetSaveSlots` enumerates `save_*.*` and reports `.json`, `.enc` and `.bin`
files alike. Read each slot for richer metadata:

```csharp
public sealed record SlotSummary(int Slot, bool Exists, string PlayerName,
                                 int Level, TimeSpan PlayTime, DateTime SavedAt);

public static SlotSummary Summarise(int slot)
{
    if (!SaveManager.SlotExists(slot))
        return new SlotSummary(slot, false, "", 0, TimeSpan.Zero, default);

    var save = SaveManager.Load<GameSave>(slot);
    return save == null
        ? new SlotSummary(slot, false, "", 0, TimeSpan.Zero, default)
        : new SlotSummary(slot, true, save.PlayerName, save.Level,
                          TimeSpan.FromSeconds(save.PlayTime), save.SavedAt);
}
```

```csharp
for (int slot = 0; slot < 3; slot++)
{
    var s = Summarise(slot);
    string text = s.Exists
        ? $"{s.PlayerName} — Level {s.Level} — {s.PlayTime:hh\\:mm}"
        : "— Empty —";

    var btn = game.MakeButton(text, Vector2.Zero, new Vector2(420, 64),
                              () => { if (s.Exists) SaveSystem.LoadSlot(game, s.Slot); });
    btn.Interactable = s.Exists || saveMode;
    panel.AddChild(btn);
}
```

## 7. Encryption

```csharp
SaveManager.SetEncryptionKey("some-passphrase");
SaveManager.Save(0, data);                    // writes Saves/save_0.enc
var back = SaveManager.Load<GameSave>(0);
SaveManager.SetEncryptionKey(null);           // back to plaintext .json
```

The scheme, from the source:

| Element | Value |
|---|---|
| Key derivation | PBKDF2 / SHA-256 / 10 000 iterations / 32 bytes |
| Salt | 16 random bytes at file offset 0 |
| IV | 16 bytes at offset 16 |
| Cipher | AES-256-CBC from offset 32 |

This stops casual save editing. It is **not** a security boundary — the
passphrase ships inside your binary. Never put anything genuinely sensitive in a
save file.

## 8. Binary saves

For large or performance-sensitive data — a voxel world, a million-tile map:

```csharp
using SexyBiscuit.Engine.Save;

public sealed class WorldSave : IBinarySerializable
{
    public int Seed;
    public List<(int x, int y, byte tile)> Edits = new();

    public void Write(BinaryWriter w)
    {
        w.Write((byte)1);                                 // version, always first
        w.Write(Seed);
        w.Write(Edits.Count);
        foreach (var (x, y, t) in Edits) { w.Write(x); w.Write(y); w.Write(t); }
    }

    public void Read(BinaryReader r)
    {
        byte version = r.ReadByte();
        Seed = r.ReadInt32();
        int n = r.ReadInt32();
        Edits.Clear();
        for (int i = 0; i < n; i++)
            Edits.Add((r.ReadInt32(), r.ReadInt32(), r.ReadByte()));

        if (version >= 2) { /* fields added in v2 */ }
    }
}
```

```csharp
SaveManager.SaveBinary(1, world);                    // Saves/save_1.bin
WorldSave? w = SaveManager.LoadBinary<WorldSave>(1);
```

Binary saves are **not encrypted** — the key applies only to the JSON path.
Write the version byte first, always; retrofitting it is painful.

## 9. Autosave

```csharp
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using MyGame.Saves;

namespace MyGame.Components;

public sealed class Autosave : Component
{
    public float Interval = 120f;
    public int   Slot     = 9;          // a dedicated autosave slot

    private TimerHandle _timer;

    public override void Start()
        => _timer = SBEngine.Instance.Timers.SetTimer(Interval, Save, looping: true);

    public override void OnDestroy()
        => SBEngine.Instance.Timers.Clear(_timer);

    private void Save()
    {
        SaveSystem.SaveToSlot(Slot);
        Debug.WriteLine($"autosaved to slot {Slot}");
    }
}
```

`Timers.Clear` in `OnDestroy` is not optional: a looping timer holds a delegate
closing over `this`, so an uncleared timer keeps the component alive and keeps
saving into a scene that no longer exists.

Save at checkpoints too — a timer alone can save at an unhelpful moment:

```csharp
public sealed class Checkpoint : Component
{
    public override void OnTriggerEnter(Actor other)
    {
        if (other.Tag != "Player") return;
        SaveSystem.SaveToSlot(9);
        Juice.Pop(Actor);
    }
}
```

## 10. Safe writes

`SaveManager.Save` writes directly to the destination. A crash or a power cut
mid-write leaves a truncated file and no previous save. Write-then-rename, and
keep a backup:

```csharp
public static void SafeSave(int slot, GameSave data)
{
    string dir   = SaveManager.SaveDirectory;
    string final = Path.Combine(dir, $"save_{slot}.json");
    string temp  = final + ".tmp";
    string bak   = final + ".bak";

    Directory.CreateDirectory(dir);
    File.WriteAllText(temp, JsonSerializer.Serialize(data,
        new JsonSerializerOptions { WriteIndented = true }));

    if (File.Exists(final)) File.Copy(final, bak, overwrite: true);
    File.Move(temp, final, overwrite: true);       // atomic on the same volume
}

public static GameSave? SafeLoad(int slot)
{
    string final = Path.Combine(SaveManager.SaveDirectory, $"save_{slot}.json");
    string bak   = final + ".bak";

    foreach (var path in new[] { final, bak })
    {
        if (!File.Exists(path)) continue;
        try { return JsonSerializer.Deserialize<GameSave>(File.ReadAllText(path)); }
        catch (Exception ex) { Debug.WriteLine($"corrupt save {path}: {ex.Message}"); }
    }
    return null;
}
```

Fifteen lines that turn "my save is gone" into "we lost the last two minutes".

## 11. Steam Cloud

If you ship on Steam, prefer the cloud — it syncs across machines.

```csharp
using SexyBiscuit.Engine.Steam;

public static void Write(int slot, GameSave data)
{
    string json = JsonSerializer.Serialize(data);
    if (!SteamCloud.Write($"save_{slot}.json", Encoding.UTF8.GetBytes(json)))
        SaveManager.Save(slot, data);              // fall back to disk
}

public static GameSave? Read(int slot)
{
    var bytes = SteamCloud.Read($"save_{slot}.json");
    if (bytes is { Length: > 0 })
        return JsonSerializer.Deserialize<GameSave>(Encoding.UTF8.GetString(bytes));
    return SaveManager.Load<GameSave>(slot);
}
```

Enable Cloud in Steam App Admin and declare the file paths there, or writes
succeed locally and never sync.

Without the `STEAMWORKS` symbol these calls compile to stubs returning `false`
and `null`, so the fallback path runs — the code is safe either way.

---

## Checkpoint

You have:

- A per-user data directory for saves, prefs, bindings and logs
- Settings applied at boot and persisted on exit
- A versioned save model, captured as plain data
- Slot listing with metadata, autosave, and crash-safe writes

## Troubleshooting

| Symptom | Cause |
|---|---|
| Nothing persists | `PlayerPrefs.Save()` never called |
| `UnauthorizedAccessException` | writing to the install directory — use `UserData.Install()` |
| Save loads but the world is wrong | restoring without rebuilding the level first |
| `FileNotFoundException` from `LoadBindings` | missing file — guard with `File.Exists` |
| Old saves crash after an update | no `Version` field or no `Migrate` |

## Exercises

1. Add a save/load slot UI to the pause menu using `Summarise`.
2. Add `SaveSystem.Flags` checks so collected pickups stay collected.
3. Move autosave from a timer to checkpoint triggers plus level transitions.

---

**Next:** [Tutorial 12 — Particles & Post-FX](12-particles-and-postfx.md)
