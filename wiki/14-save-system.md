# 14. Save & Preferences

Namespace: `SexyBiscuit.Engine.Save`

Two static services:

| | `SaveManager` | `PlayerPrefs` |
|---|---|---|
| For | game state, slots | settings, small key/values |
| Format | JSON (optionally AES-256) or binary | JSON dictionary |
| Default location | `Saves/save_{slot}.json` | `Saves/prefs.json` |

Both write **relative to the working directory** by default. For a shipped game
you almost certainly want a per-user directory — see
[Where saves should live](#where-saves-should-live).

---

## SaveManager

### JSON slots

```csharp
public sealed class GameSave
{
    public string PlayerName { get; set; } = "";
    public int    Level      { get; set; }
    public float  PlayTime   { get; set; }
    public float[] Position  { get; set; } = new float[2];
    public List<string> Inventory { get; set; } = new();
    public Dictionary<string, bool> Flags { get; set; } = new();
}
```

```csharp
SaveManager.SaveDirectory = "Saves/";

SaveManager.Save(0, new GameSave { PlayerName = "Ada", Level = 7 });

GameSave? loaded = SaveManager.Load<GameSave>(0);    // null if the slot is missing
bool exists      = SaveManager.SlotExists(0);
SaveManager.Delete(0);

foreach (SaveSlotInfo info in SaveManager.GetSaveSlots())
    Debug.WriteLine($"slot {info.Slot} — {info.SavedAt:g} — {info.ByteSize} bytes — {info.FilePath}");
```

Serialisation uses `System.Text.Json` with `WriteIndented = true` and
case-insensitive property matching. Your save type needs public
get/set properties and a parameterless constructor — the usual STJ rules.

`GetSaveSlots` enumerates `save_*.*` in `SaveDirectory` and reports `.json`,
`.enc` and `.bin` files alike.

### Encryption

```csharp
SaveManager.SetEncryptionKey("a-passphrase-from-somewhere");
SaveManager.Save(0, data);           // writes Saves/save_0.enc
var back = SaveManager.Load<GameSave>(0);

SaveManager.SetEncryptionKey(null);  // back to plaintext .json
```

Scheme, from the source:

| Element | Value |
|---|---|
| Key derivation | PBKDF2 / SHA-256 / 10 000 iterations / 32 bytes |
| Salt | 16 random bytes, stored at file offset 0 |
| IV | 16 bytes at offset 16 |
| Cipher | AES-256-CBC from offset 32 |

This stops casual save editing. It is **not** a security boundary: the
passphrase ships inside your binary, so anyone determined can extract it. Do
not store anything genuinely sensitive in a save file.

### Binary saves

For large or performance-sensitive saves, implement `IBinarySerializable`:

```csharp
public sealed class WorldSave : IBinarySerializable
{
    public int Seed;
    public List<(int x, int y, byte tile)> Edits = new();

    public void Write(BinaryWriter w)
    {
        w.Write(Seed);
        w.Write(Edits.Count);
        foreach (var (x, y, t) in Edits) { w.Write(x); w.Write(y); w.Write(t); }
    }

    public void Read(BinaryReader r)
    {
        Seed = r.ReadInt32();
        int n = r.ReadInt32();
        Edits.Clear();
        for (int i = 0; i < n; i++)
            Edits.Add((r.ReadInt32(), r.ReadInt32(), r.ReadByte()));
    }
}
```

```csharp
SaveManager.SaveBinary(1, worldSave);                 // Saves/save_1.bin
WorldSave? w = SaveManager.LoadBinary<WorldSave>(1);  // T : IBinarySerializable, new()
```

Binary saves are **not encrypted** — the encryption key applies only to the JSON
path.

Version your binary format from day one:

```csharp
public void Write(BinaryWriter w) { w.Write((byte)2); /* … */ }

public void Read(BinaryReader r)
{
    byte version = r.ReadByte();
    if (version >= 1) { /* … */ }
    if (version >= 2) { /* … */ }
}
```

---

## PlayerPrefs

A single JSON dictionary, loaded lazily and written on `Save()`.

```csharp
PlayerPrefs.SetFloat("volume.master", 0.8f);
PlayerPrefs.SetInt("difficulty", 2);
PlayerPrefs.SetString("lastProfile", "Ada");
PlayerPrefs.SetBool("fullscreen", true);

float v  = PlayerPrefs.GetFloat("volume.master", 1f);
int   d  = PlayerPrefs.GetInt("difficulty", 1);
string p = PlayerPrefs.GetString("lastProfile", "");
bool  fs = PlayerPrefs.GetBool("fullscreen", false);

bool has = PlayerPrefs.HasKey("difficulty");
PlayerPrefs.DeleteKey("difficulty");
PlayerPrefs.DeleteAll();

PlayerPrefs.Save();     // ← writes to disk; nothing is persisted until you call it
PlayerPrefs.Load();     // explicit reload
```

**`Save()` is not automatic.** Call it when the player leaves a settings screen,
and again on shutdown:

```csharp
protected override void UnloadContent()
{
    PlayerPrefs.Save();
    base.UnloadContent();
}
```

The file location defaults to `Saves/prefs.json`:

```csharp
PlayerPrefs.PrefsPath = Path.Combine(UserDataDir(), "prefs.json");
```

Setting `PrefsPath` clears the loaded flag, so the next access reads from the
new location.

---

## Where saves should live

Writing next to the executable fails on a read-only install directory (Program
Files, a Steam library on a locked volume, a macOS app bundle). Point both
services at a per-user directory during startup:

```csharp
static string UserDataDir()
{
    string root = Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData,        // %APPDATA% / ~/.config / ~/Library/Application Support
        Environment.SpecialFolderOption.Create);
    string dir = Path.Combine(root, "MyStudio", "MyGame");
    Directory.CreateDirectory(dir);
    return dir;
}

protected override void OnEngineReady()
{
    string data = UserDataDir();
    SaveManager.SaveDirectory = Path.Combine(data, "Saves") + Path.DirectorySeparatorChar;
    PlayerPrefs.PrefsPath     = Path.Combine(data, "prefs.json");
}
```

On Steam, [`SteamCloud`](19-steam.md) is the better home for saves — it
synchronises across machines.

---

## Recipes

### Autosave with a debounce

```csharp
public sealed class Autosave : Component
{
    public float Interval = 60f;
    private float _timer;

    public override void Update(float dt)
    {
        _timer += dt;
        if (_timer < Interval) return;
        _timer = 0f;
        SaveManager.Save(0, GameState.Capture());
    }
}
```

### Settings screen backed by prefs

```csharp
masterSlider.Value = PlayerPrefs.GetFloat("volume.master", 1f);
masterSlider.OnValueChanged += v =>
{
    SBEngine.Instance.Audio.Master.Volume = v;
    PlayerPrefs.SetFloat("volume.master", v);
};

applyButton.OnClick += () => PlayerPrefs.Save();
```

Apply them at boot, before the first scene:

```csharp
protected override void OnEngineReady()
{
    Audio.Master.Volume = PlayerPrefs.GetFloat("volume.master", 1f);
    Audio.Music.Volume  = PlayerPrefs.GetFloat("volume.music",  0.7f);
    Audio.SFX.Volume    = PlayerPrefs.GetFloat("volume.sfx",    1f);
    string bindings = Path.Combine(UserDataDir(), "bindings.json");
    if (File.Exists(bindings)) Input.LoadBindings(bindings);
}
```

`InputManager.LoadBindings` throws `FileNotFoundException` when the file does
not exist, so guard the first run:

```csharp
string bindings = Path.Combine(UserDataDir(), "bindings.json");
if (File.Exists(bindings)) Input.LoadBindings(bindings);
```

### Capturing scene state

Do **not** try to serialise the live scene as a save. `SceneSerializer` drops
textures and runtime references. Capture a plain data snapshot instead:

```csharp
public static GameSave Capture()
{
    var scene = SBEngine.Instance.SceneManager.ActiveScene!;
    var player = scene.FindByName("Player")!;

    return new GameSave
    {
        Level     = CurrentLevelIndex,
        Position  = new[] { player.Transform.Position.X, player.Transform.Position.Y },
        Inventory = Inventory.Items.Select(i => i.Id).ToList(),
        Flags     = QuestFlags.ToDictionary(),
    };
}
```

Restore by rebuilding the scene and applying the snapshot — the same code path
as starting a new game, plus an "apply save" step. That keeps one code path
warm instead of two.

---

## Next

- [15. Networking](15-networking.md)
- [Tutorial 11: Saving & Loading](../tutorials/11-saving-and-loading.md)
