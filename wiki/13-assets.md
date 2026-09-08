# 13. Assets

Namespace: `SexyBiscuit.Engine.Assets`

`AssetManager` is created by `SBEngine` and available as `Assets`. It loads
**raw files** — no MonoGame content pipeline, no `.xnb`.

```csharp
var tex = SBEngine.Instance.Assets.Load<Texture2D>("Assets/Sprites/player.png");
```

---

## Supported types — Verified

`LoadFromStream<T>` handles exactly four types:

| `T` | Loader |
|---|---|
| `Texture2D` | `Texture2D.FromStream` — PNG, JPG, and the formats your MonoGame build supports |
| `SoundEffect` | `SoundEffect.FromStream` — 16-bit PCM WAV |
| `string` | whole file as text |
| `byte[]` | whole file as bytes |

Anything else throws:

```
NotSupportedException: AssetManager: no raw loader registered for type 'X'.
Supported types: Texture2D, SoundEffect, string, byte[].
```

So **`SpriteFont`, `Effect`, `Model`, `TextureCube` and 3D meshes do not go
through `AssetManager`**:

| Asset | Load it with |
|---|---|
| `SpriteFont` | `Content.Load<SpriteFont>("Fonts/ui")` — MGCB pipeline |
| `Effect` (shader) | `Content.Load<Effect>("Shaders/blur")` — MGCB pipeline |
| 3D model | `MeshRenderer.LoadModel(path, GraphicsDevice)` — AssimpNet |
| Cubemap | `Skybox.CubemapPath = "Assets/Sky"`, or `Skybox.LoadCubemap(sixPaths, GraphicsDevice)` |
| Tilemap | `TilemapData.LoadFromJson(path)` |
| Anything else | `Assets.Load<byte[]>(path)` and parse it yourself |

A `TextureCube` is not an `AssetManager` type, but its six faces are: `LoadCubemap`
reads each through `AssetManager.Current` when a host is running, so a cubemap in
a mounted bundle is found like any other texture, and drops to the file system
when there is no host. It used to read the files directly, which is why a sky
that worked from a source tree came out as the gradient in a bundled build.

Adding a type means editing `LoadFromStream<T>` in
`SexyBiscuit.Engine/Assets/AssetManager.cs` — it is a short `if` chain.

---

## Paths

```csharp
private static string NormalisePath(string path)
    => Path.GetFullPath(path).Replace('\\', '/');
```

Paths are made **absolute against the current working directory** and used as
the cache key. Practical consequences:

- `"Assets/Sprites/player.png"` resolves relative to where the process runs,
  which for `dotnet run` is the project directory and for a published build is
  the binary's folder.
- `"Assets/x.png"` and `"./Assets/x.png"` normalise to the same key — one cache
  entry, correctly.
- Comparison is `OrdinalIgnoreCase`, so casing differences share an entry. On
  Linux, a file whose case does not match will still fail to open.

Copy content next to the binary in your `.csproj`:

```xml
<ItemGroup>
  <Content Include="Assets\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

If you need paths relative to the executable rather than the working directory:

```csharp
static string AssetPath(string rel)
    => Path.Combine(AppContext.BaseDirectory, rel);

var tex = Assets.Load<Texture2D>(AssetPath("Assets/Sprites/player.png"));
```

---

## Reference counting

```csharp
var a = Assets.Load<Texture2D>("Assets/x.png");   // RefCount 1, decoded
var b = Assets.Load<Texture2D>("Assets/x.png");   // RefCount 2, same instance
Assets.Unload("Assets/x.png");                    // RefCount 1
Assets.Unload("Assets/x.png");                    // RefCount 0 → disposed, evicted
```

`Unload` on a path that was never loaded is a no-op. `UnloadAll()` disposes
everything regardless of count — call it on a full teardown, not between levels,
unless you reload every asset afterwards.

```csharp
bool loaded = Assets.IsLoaded("Assets/x.png");
foreach (var (path, type, bytes) in Assets.GetLoadedAssets())
    Debug.WriteLine($"{path} — {type.Name} — {bytes / 1024}KB");
```

`GetLoadedAssets` is the basis of a simple memory readout; pair it with
[`MemoryViewer`](17-debugging.md).

### A level-scoped loading pattern

```csharp
public sealed class LevelAssets : IDisposable
{
    private readonly AssetManager _assets;
    private readonly List<string> _paths = new();

    public LevelAssets(AssetManager assets) => _assets = assets;

    public T Load<T>(string path) where T : class
    {
        _paths.Add(path);
        return _assets.Load<T>(path);
    }

    public void Dispose()
    {
        foreach (var p in _paths) _assets.Unload(p);
        _paths.Clear();
    }
}
```

```csharp
using var levelAssets = new LevelAssets(Assets);
var tiles = levelAssets.Load<Texture2D>("Assets/Maps/tileset.png");
// … disposing releases exactly what this level took a reference on …
```

---

## Async loading

```csharp
var tex = await Assets.LoadAsync<Texture2D>("Assets/big.png", p => _progress = p);
```

`LoadAsync` returns immediately from the cache if the asset is already loaded.
Otherwise it wraps the synchronous `Load<T>` in `Task.Run` and reports `0f` then
`1f` — there is **no byte-level progress**, and the decode still happens on a
thread pool thread.

> `Texture2D.FromStream` touches the `GraphicsDevice`. Creating GPU resources
> off the main thread is not guaranteed safe on every MonoGame backend. For a
> real loading screen, read bytes asynchronously and create the texture on the
> main thread:

```csharp
byte[] bytes = await Assets.LoadAsync<byte[]>("Assets/big.png", p => _progress = p);
// back on the main thread, e.g. in the next Update:
using var ms = new MemoryStream(bytes);
var tex = Texture2D.FromStream(GraphicsDevice, ms);
```

---

## Loading screens

`LoadingScreen` (in `SexyBiscuit.Engine.Assets`) loads a batch of assets a few
per frame, so the window stays responsive and a progress bar keeps animating.

```csharp
using SexyBiscuit.Engine.Assets;

var loader = new LoadingScreen(Assets);
loader.Add(AssetRequest.For<Texture2D>("Assets/atlas.png", weight: 4f));
loader.Add(AssetRequest.For<Texture2D>("Assets/Sprites/hero.png"));
loader.Add(AssetRequest.For<SoundEffect>("Assets/Audio/theme.wav", weight: 3f));

loader.ItemsPerFrame = 2;                 // higher finishes sooner, hitches more

loader.ProgressChanged.Add(p => bar.Value = p);
loader.AssetLoaded.Add(path => statusLabel.Text = path);
loader.Completed.Add(() => GameScene.Load(game));
loader.Start();
```

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    _loader?.Tick();                      // required — nothing calls this for you
}
```

```csharp
loader.Progress;      // weighted 0..1
loader.IsLoading;  loader.IsComplete;
loader.Failures;      // IReadOnlyList<(string path, string error)>
```

Two design points worth knowing:

- **`Weight` is relative cost**, not size. Leave it at 1 unless one asset dwarfs
  the others — a large atlas against a dozen icons.
- **Progress and completion are raised on the game thread.** Loading touches the
  GPU (texture upload), so it cannot simply move to a background thread; the
  batch is amortised across frames instead. That means no cross-thread
  marshalling in your handlers.

Check `Failures` when the bar reaches 1 — a missing asset does not stop the
batch.

---

## Hot reload

Compiled only when `DEBUG` or `DEVELOPMENT` is defined.

```csharp
Assets.OnAssetReloaded += path => Debug.WriteLine($"reloaded {path}");
```

`Load<T>` registers a `FileSystemWatcher` on each loaded file's directory. When
the file changes, the asset is re-decoded **in place**, so existing references
see the new content, and `OnAssetReloaded` fires.

The event is raised **on the watcher's background thread**. Do not touch the
scene graph or the graphics device from the handler — set a flag and act on it
in `Update`:

```csharp
private readonly ConcurrentQueue<string> _reloaded = new();

Assets.OnAssetReloaded += p => _reloaded.Enqueue(p);

protected override void Update(GameTime gt)
{
    base.Update(gt);
    while (_reloaded.TryDequeue(out var path))
        Debug.WriteLine($"asset changed: {path}");
}
```

In `Release` builds no watchers are created and the event never fires.

---

## Asset bundles

A bundle is a **ZIP archive** mounted into the asset search path.

```csharp
var bundle = AssetBundle.Mount("Bundles/dlc1.sbb");

// Loads transparently from the bundle if it contains the entry,
// otherwise from the file system.
var tex = Assets.Load<Texture2D>("Assets/DLC1/boss.png");

foreach (var entry in bundle.EnumerateEntries()) Debug.WriteLine(entry);
bool has = bundle.Contains("Assets/DLC1/boss.png");

bundle.Unmount();
bundle.Dispose();
```

`AssetManager.ResolveStream` checks **mounted bundles first**, in mount order,
then the file system. Bundle streams are buffered into a `MemoryStream` so
seek-requiring loaders such as `Texture2D.FromStream` work.

Entry paths are matched against the same normalised (absolute) key the loader
computes — so build bundles with entry names that match the paths your game
requests, and mount them before the first `Load`.

Bundles are the mechanism behind mods and DLC, since mounted bundles are
searched before the disk. Bundles are iterated in **mount order and the first
match wins**, so mount the highest-priority bundle *first*:

```csharp
AssetBundle.Mount("Bundles/mod_texturepack.sbb");   // overrides
AssetBundle.Mount("Bundles/base.sbb");              // fallback
// disk is searched last

foreach (var b in AssetBundle.MountedBundles) Debug.WriteLine(b.ArchivePath);
```

A bundle that shadows an asset already in the `AssetManager` cache has no
effect on the cached instance — mount everything before your first `Load`, or
call `Unload` on the paths you want re-resolved.

---

## Cooking

The [export pipeline](18-build-export.md) runs `AssetCooker` over your `Assets`
folder at build time. It is incremental — only files whose hash changed since the
last cook are reprocessed.

```csharp
var cooker = new AssetCooker { IsIncrementalCook = true };
CookResult result = cooker.Cook("Assets", "dist/Windows_x64/Assets", platformConfig);
foreach (var line in cooker.GetLog()) Console.WriteLine(line);
```

---

## Practical guidance

- **Load once, at scene build time.** `Load<T>` on a cache hit is a dictionary
  lookup plus an increment, but it is still a `Path.GetFullPath` call per
  invocation — do not call it inside `Update`.
- **Cache the `Texture2D` on your component**, not the path.
- **Pair every `Load` with an `Unload`** when the owner dies, or accept that
  everything lives until `UnloadAll`.
- **Prefer 16-bit PCM WAV** for audio. `SoundEffect.FromStream` is the only
  decoder wired up.
- **Keep one texture atlas per layer** where you can; each distinct `Texture2D`
  in a batch forces a draw-call break.

---

## Next

- [14. Save & Preferences](14-save-system.md)
- [18. Build & Export](18-build-export.md)
