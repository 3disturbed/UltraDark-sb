# 17. Debugging & Profiling

Namespace: `SexyBiscuit.Engine.Debug`

Five static tools. **None of them are pumped by the engine** — each needs an
`Update` call, a `Draw` call, or both. See
[3. The Game Loop](03-game-loop.md).

| Tool | Update | Draw |
|---|---|---|
| `DebugOverlay` | `Update(dt)` | `Draw(sb)` |
| `Gizmos` | `Update(dt)` | `Flush(sb, gd)` |
| `Profiler` | `BeginFrame()` / `EndFrame()` | `Draw(sb, pos)` |
| `MemoryViewer` | `Update()` | `Draw(sb, pos)` |
| `NetworkDiagnostics` | `Update(dt)` | `Draw(sb, pos)` |

A single block covers all five:

```csharp
protected override void Update(GameTime gameTime)
{
    Profiler.BeginFrame();
    float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

    base.Update(gameTime);

    Gizmos.Update(dt);
    DebugOverlay.Update(dt);
    NetworkDiagnostics.Update(dt);
    MemoryViewer.Update();

    Profiler.EndFrame();
}

protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);

    var scene = SceneManager.ActiveScene;
    if (scene != null) Renderer2D.RenderScene(SpriteBatch, scene, _camera);

    SpriteBatch.Begin();
    Gizmos.Flush(SpriteBatch, GraphicsDevice);
    DebugOverlay.Draw(SpriteBatch);
    Profiler.Draw(SpriteBatch, new Vector2(300, 8));
    MemoryViewer.Draw(SpriteBatch, new Vector2(600, 8));
    NetworkDiagnostics.Draw(SpriteBatch, new Vector2(900, 8));
    SpriteBatch.End();
}
```

Guard the whole block with `#if DEBUG || DEVELOPMENT` if you would rather not
ship it. Note that
[`PlatformConfig.IncludeDebugOverlay`](18-build-export.md) defaults to
`!isRelease`, so the export pipeline already expects this to be conditional.

---

## DebugOverlay

An FPS/frame HUD.

```csharp
DebugOverlay.Visible = true;
DebugOverlay.BeginFrame();          // resets the draw-call counter
DebugOverlay.IncrementDrawCall();   // call from your own draw code
```

Shows rolling-average FPS over 60 samples, last frame's delta in ms, draw calls,
actor count, GC collection counts, and managed heap size.

**It toggles itself with <kbd>F1</kbd>**, reading `Keyboard.GetState()` directly
so it works with no `InputManager` dependency.

Draw calls are not counted automatically. If you want the number to mean
anything, call `IncrementDrawCall()` where you issue batches.

The overlay renders with a lazily-created 1×1 pixel texture and a blocky
built-in glyph routine, so it needs no font asset.

---

## Gizmos

Immediate-mode debug shapes with an optional lifetime.

```csharp
Gizmos.Enabled = true;

Gizmos.DrawLine  (start3, end3,        Color.Yellow, duration: 0f);
Gizmos.DrawBox   (center3, size3,      Color.Lime);
Gizmos.DrawCircle(center3, radius, segments: 32, Color.Cyan);
Gizmos.DrawSphere(center3, radius,     Color.Magenta);
Gizmos.DrawText  (worldPos3, "spawn",  Color.White);

Gizmos.DrawLine2D(a2, b2, Color.Red);
Gizmos.DrawBox2D (center2, size2, Color.Lime);
```

`duration: 0` (the default) draws for one frame — call it every frame from
`Update`. A positive duration keeps the shape alive that many seconds, which is
what you want for one-shot events:

```csharp
public override void OnCollisionEnter(CollisionData data)
    => Gizmos.DrawSphere(new Vector3(data.ContactPoint, 0f), 0.15f, Color.Red, duration: 1.5f);
```

`Gizmos.Update(dt)` expires timed commands; `Flush(sb, gd)` draws and clears the
queue. Flush inside a `SpriteBatch` that is **not** camera-transformed if you
pass screen coordinates, or inside the camera batch if you pass world
coordinates — the calls are drawn as-is.

### Visualising colliders

```csharp
public sealed class ColliderGizmo : Component
{
    public Color Colour = Color.Lime;

    public override void Update(float dt)
    {
        if (!Gizmos.Enabled) return;
        var p = Transform.Position;

        if (GetComponent<BoxCollider2D>() is { } box)
            Gizmos.DrawBox2D(p + box.Offset, box.Size, Colour);

        if (GetComponent<CircleCollider2D>() is { } c)
            Gizmos.DrawCircle(new Vector3(p + c.Offset, 0f), c.Radius, 24, Colour);
    }
}
```

Attach it to every physics actor while you are bringing up a level, then delete
the `AddComponent` calls.

---

## Profiler

Named, nestable timing sections with a rolling average.

```csharp
Profiler.BeginFrame();

Profiler.Begin("AI");
UpdateAI(dt);
Profiler.End("AI");

Profiler.Begin("Waves");
_waveDirector.Update(dt);
Profiler.End("Waves");

Profiler.EndFrame();
```

```csharp
double frameMs = Profiler.TotalFrameMs;
foreach (var s in Profiler.Samples)
    Debug.WriteLine($"{s.Name}: {s.LastMs:F2}ms (avg {s.AvgMs:F2}ms)");

Profiler.Draw(SpriteBatch, new Vector2(300, 8));
```

Every `Begin` needs a matching `End` with the same name. A `try`/`finally`
wrapper avoids losing a section on an early return:

```csharp
public readonly struct ProfileScope : IDisposable
{
    private readonly string _name;
    public ProfileScope(string name) { _name = name; Profiler.Begin(name); }
    public void Dispose() => Profiler.End(_name);
}
```

```csharp
using (new ProfileScope("AI")) UpdateAI(dt);
```

Averages are computed over a rolling window, so a single spike will not dominate
the display — watch `LastMs` for spikes and `AvgMs` for trends.

---

## MemoryViewer

```csharp
MemoryViewer.Visible = true;
MemoryViewer.Update();
MemorySnapshot snap = MemoryViewer.GetSnapshot();
MemoryViewer.Draw(SpriteBatch, new Vector2(600, 8));
```

```csharp
public record MemorySnapshot(
    long TotalManagedBytes,
    int  Gen0, int Gen1, int Gen2,
    int  AssetCount,
    long AssetEstimatedBytes);
```

Asset figures come from
[`AssetManager.GetLoadedAssets`](13-assets.md#reference-counting). A steadily
climbing `AssetCount` across level transitions means you are missing `Unload`
calls; a climbing `Gen0` count per frame means per-frame allocation in your
update path.

---

## NetworkDiagnostics

```csharp
NetworkDiagnostics.Visible = true;
NetworkDiagnostics.RecordBytesIn(packet.Length);
NetworkDiagnostics.RecordBytesOut(packet.Length);
NetworkDiagnostics.Update(dt);
NetworkDiagnostics.Draw(SpriteBatch, new Vector2(900, 8));
```

Byte counters are **not recorded automatically** — call them from your own send
and receive paths if you want the throughput readout to be meaningful.

---

## Logging

The engine writes with `System.Diagnostics.Debug.WriteLine` and, in a few
places, `Console.WriteLine` / `Console.Error.WriteLine`. Script output goes
through `Debug.log` / `warn` / `error` in the bridge, which writes to both.

Route it wherever you like with a trace listener — this is exactly what the
editor's console panel does:

```csharp
public sealed class FileTraceListener : TraceListener
{
    private readonly StreamWriter _w;
    public FileTraceListener(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _w = new StreamWriter(path, append: true) { AutoFlush = true };
    }
    public override void Write(string? message)     => _w.Write(message);
    public override void WriteLine(string? message) => _w.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}
```

```csharp
Trace.Listeners.Add(new FileTraceListener("Logs/game.log"));
```

`Debug.WriteLine` is compiled out in `Release` (no `DEBUG` symbol), so a
shipping build logs nothing through that path. Use `Trace.WriteLine` for
messages you want in release builds.

---

## A debug hotkey block

```csharp
private KeyboardState _prevKeys;

private void HandleDebugKeys()
{
    var keys = Keyboard.GetState();
    bool Pressed(Keys k) => keys.IsKeyDown(k) && !_prevKeys.IsKeyDown(k);

    if (Pressed(Keys.F1)) { /* DebugOverlay toggles itself */ }
    if (Pressed(Keys.F2)) Gizmos.Enabled            = !Gizmos.Enabled;
    if (Pressed(Keys.F3)) MemoryViewer.Visible      = !MemoryViewer.Visible;
    if (Pressed(Keys.F4)) NetworkDiagnostics.Visible = !NetworkDiagnostics.Visible;

    if (Pressed(Keys.F9))  Time.TimeScale = Time.TimeScale > 0f ? 0f : 1f;
    if (Pressed(Keys.F10)) Time.TimeScale = 0.25f;

    _prevKeys = keys;
}
```

---

## A short diagnosis table

| Symptom | First thing to check |
|---|---|
| Black window | `SpriteBatch.Begin` never called — use `RenderSystem2D`. See [page 3](03-game-loop.md). |
| Sprites invisible | `SpriteRenderer.Texture` is null, or the actor is off-camera, or its layer is `Visible = false`. |
| Nothing moves | `Update` not reached: actor not added to a scene, `IsActive`/`Enabled` false, or layer `Active = false`. |
| Physics inert | `Config.EnablePhysics2D` is false, or no `Rigidbody2D`. |
| Tweens frozen | `Time.TimeScale` is 0 — tweens run on scaled time. |
| Script does nothing | `ScriptPath` set after `AddComponent` — call `script.Awake()`. Check the log for `[Script Error]`. |
| Text invisible | `Canvas.Font` is null. |
| Panel/Button invisible | No `BackgroundTexture` / `NormalTexture`. |
| Collider wrong size | Shape set after `AddComponent` — call `Rebuild()`. |
| Network silent | `NetworkManager.Tick(dt)` not called. |
| Frame time spikes | `Profiler` a suspect section; check `Gen0` in `MemoryViewer` for per-frame allocation. |

---

## Next

- [18. Build & Export](18-build-export.md)
- [21. Gotchas](21-gotchas.md)
