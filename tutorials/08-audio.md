# Tutorial 8 — Audio

**You will build:** music with crossfades, positional sound effects, a bus-driven
mixer, and the small touches that keep repeated sounds from grating.
**Time:** ~25 minutes.

Builds on [Tutorial 7](07-ui-and-menus.md).

---

## 1. Format

`AudioManager` decodes through MonoGame's `SoundEffect.FromStream`, which reads
**16-bit PCM WAV**. NAudio is referenced by the engine project but not used by
the audio path, and OGG/MP3 support in the design document is not wired up.

**Ship WAV.** Convert with ffmpeg:

```bash
ffmpeg -i music.mp3 -acodec pcm_s16le -ar 44100 -ac 2 Assets/Audio/music.wav
ffmpeg -i hit.ogg   -acodec pcm_s16le -ar 44100 -ac 1 Assets/Audio/hit.wav
```

Mono for sound effects (it pans correctly in 3D), stereo for music.

## 2. Buses

Four buses exist from construction, in a parent chain:

```
Master
├── Music
├── SFX
└── Voice
```

```csharp
Audio.Master.Volume = 1f;
Audio.Music.Volume  = 0.7f;
Audio.SFX.Volume    = 1f;
Audio.Voice.Volume  = 0.9f;
```

`EffectiveVolume` multiplies up the chain, so `Master.Volume = 0` silences
everything. `Muted` zeroes a bus; `Solo` on any bus silences the others — useful
while mixing.

Custom buses parent onto any existing one:

```csharp
using SexyBiscuit.Engine.Audio;

var ambience = new AudioBus("Ambience", Audio.Master);
var handle = Audio.Play("Assets/Audio/wind.wav", loop: true, bus: ambience);
```

Buses are what make the options menu from Tutorial 7 a three-line affair rather
than a per-source bookkeeping problem.

## 3. Playing sound

### Fire and forget

```csharp
Audio.PlayOneShot("Assets/Audio/hit.wav");
Audio.PlayOneShot("Assets/Audio/hit.wav", volume: 0.7f, pitch: 0.2f);
```

`pitch` follows MonoGame's convention: `−1` is an octave down, `0` unchanged,
`+1` an octave up.

### Managed playback

```csharp
using SexyBiscuit.Engine.Audio;

AudioHandle music = Audio.Play("Assets/Audio/theme.wav", loop: true, bus: Audio.Music);
Audio.Stop(music);

bool alive = music.IsValid;
```

`AudioHandle` is a readonly struct wrapping a `uint`. Safe to store; stopping an
already-finished handle is a no-op.

### Fades

```csharp
Audio.FadeIn(handle, duration: 1.5f);
Audio.FadeOut(handle, duration: 2f);                       // stops at zero
Audio.CrossFade("Assets/Audio/battle.wav", 2f, Audio.Music);
```

`CrossFade` starts the new track and ramps the current one on that bus down,
releasing the old voice at the end. Fades advance in `AudioManager.Update`,
which the engine pumps.

## 4. A music director

Music that survives scene changes, with per-track crossfades:

`MyGame/Systems/MusicDirector.cs`:

```csharp
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Audio;

namespace MyGame.Systems;

/// <summary>
/// Owns the single music voice. Crossfades between tracks and refuses to
/// restart a track that is already playing.
/// </summary>
public sealed class MusicDirector
{
    private readonly AudioManager _audio;
    private string?      _currentPath;
    private AudioHandle  _handle;

    public MusicDirector(AudioManager audio) => _audio = audio;

    public void Play(string path, float crossfade = 1.5f)
    {
        if (_currentPath == path && _handle.IsValid) return;

        if (_handle.IsValid && crossfade > 0f)
        {
            _audio.CrossFade(path, crossfade, _audio.Music);
        }
        else
        {
            if (_handle.IsValid) _audio.Stop(_handle);
            _handle = _audio.Play(path, loop: true, bus: _audio.Music);
            if (crossfade > 0f) _audio.FadeIn(_handle, crossfade);
        }

        _currentPath = path;
    }

    public void Stop(float fadeOut = 1f)
    {
        if (!_handle.IsValid) return;
        if (fadeOut > 0f) _audio.FadeOut(_handle, fadeOut);
        else              _audio.Stop(_handle);

        _handle      = AudioHandle.Invalid;
        _currentPath = null;
    }

    /// <summary>Temporarily lowers music, e.g. under dialogue.</summary>
    public void Duck(float toVolume, float seconds)
    {
        float restore = _audio.Music.Volume;
        _audio.Music.Volume = toVolume;

        SBEngine.Instance.Timers.SetTimer(seconds, () => _audio.Music.Volume = restore);
    }
}
```

```csharp
// in Game
public MusicDirector Music { get; private set; } = null!;

protected override void OnEngineReady()
{
    Music = new MusicDirector(Audio);
    // …
}

// in a scene loader
game.Music.Play("Assets/Audio/menu_theme.wav");
game.Music.Play("Assets/Audio/level1.wav", crossfade: 2f);
```

`Timers.SetTimer` is the engine's own scheduler and is pumped between `Update`
and `LateUpdate` — no bookkeeping component required.

## 5. AudioSource

Ties a clip to an actor, with optional 3D positioning.

```csharp
using SexyBiscuit.Engine.Audio;

var src = actor.AddComponent<AudioSource>();
src.Clip   = Audio.LoadOrGet("Assets/Audio/engine.wav");
src.Volume = 0.8f;
src.Pitch  = 0f;
src.Loop   = true;
src.Bus    = Audio.SFX;
src.Play();                      // do NOT rely on PlayOnAwake — see below

src.Stop();  src.Pause();
src.FadeIn(1f);  src.FadeOut(1f);
bool playing = src.IsPlaying;
```

**`PlayOnAwake` never fires for a component you add in code.** `Awake` runs
inside `AddComponent`, before you can assign `Clip`. Call `Play()` explicitly.

### 3D sound

```csharp
src.Is3D            = true;
src.RolloffDistance = 500f;      // volume reaches zero at this distance, in world units
src.DopplerScale    = 1f;
```

Volume falls off linearly to zero at `RolloffDistance`, panning follows the
horizontal offset, and Doppler shifts pitch by relative velocity.

**The listener is the first actor tagged `"Camera"`:**

```csharp
// AudioSource.GetListenerPosition(), simplified:
foreach (var actor in scene.FindByTag("Camera")) return actor.Transform.Position;
return Vector2.Zero;
```

With no such actor the listener sits at the origin and every 3D source is
attenuated by its distance from `(0,0)` — which sounds like "3D audio is
broken". Tutorial 1 already tags the camera `"Camera"`; keep doing that.

Note this differs from the tags other systems want: `Camera3D.Main` looks for
`"MainCamera3D"`. If you need both on one object, put the audio listener tag on
a child actor parented to the camera.

`RolloffDistance` is in world units, so match your
[physics units convention](06-physics-platformer.md#1-units--decide-this-first).

## 6. Making repetition bearable

The same WAV played twenty times a minute becomes noise. Two cheap fixes.

`MyGame/Systems/Sfx.cs`:

```csharp
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;

namespace MyGame.Systems;

public static class Sfx
{
    /// <summary>Plays with a random pitch offset so repeats do not sound identical.</summary>
    public static void Play(string path, float volume = 1f, float pitchVariance = 0.12f)
    {
        float pitch = SBMath.RandomRange(-pitchVariance, pitchVariance);
        SBEngine.Instance.Audio.PlayOneShot(path, volume, pitch);
    }

    /// <summary>Picks one of several takes at random, then varies its pitch.</summary>
    public static void PlayAny(string[] paths, float volume = 1f, float pitchVariance = 0.12f)
    {
        if (paths.Length == 0) return;
        Play(paths[SBMath.RandomRange(0, paths.Length)], volume, pitchVariance);
    }
}
```

```csharp
static readonly string[] Footsteps =
{
    "Assets/Audio/step1.wav",
    "Assets/Audio/step2.wav",
    "Assets/Audio/step3.wav",
};

Sfx.PlayAny(Footsteps, 0.6f);
```

Three takes plus ±12 % pitch is enough that the ear stops noticing repetition.

### Rate limiting

A collision sound fired on every contact frame becomes a buzz. Gate it:

```csharp
public sealed class ImpactSound : Component
{
    public string Path        = "Assets/Audio/impact.wav";
    public float  MinInterval = 0.08f;
    public float  MinVelocity = 200f;

    private float _cooldown;

    public override void Update(float dt) => _cooldown -= dt;

    public override void OnCollisionEnter(CollisionData data)
    {
        if (_cooldown > 0f || data.RelativeVelocity < MinVelocity) return;
        _cooldown = MinInterval;

        float volume = Math.Clamp(data.RelativeVelocity / 800f, 0.2f, 1f);
        Sfx.Play(Path, volume);
    }
}
```

Scaling volume by impact speed costs one line and makes collisions read as
physical rather than binary.

## 7. Loading

```csharp
SoundEffect? clip = Audio.LoadOrGet("Assets/Audio/hit.wav");   // cached by path
```

Or through `AssetManager`, which reference-counts:

```csharp
var clip = Assets.Load<SoundEffect>("Assets/Audio/hit.wav");
Assets.Unload("Assets/Audio/hit.wav");
```

Prefer `AssetManager` for clips that belong to a level you will unload;
`LoadOrGet` for a handful of global sounds.

Warm the cache during a loading screen rather than on first play — decoding a
WAV mid-combat costs a frame:

```csharp
static readonly string[] Preload =
{
    "Assets/Audio/jump.wav",
    "Assets/Audio/hit.wav",
    "Assets/Audio/pickup.wav",
};

foreach (var path in Preload) Audio.LoadOrGet(path);
```

## 8. Ducking under dialogue

```csharp
public void PlayLine(string voPath, float lineSeconds)
{
    float restore = Audio.Music.Volume;
    Audio.Music.Volume = restore * 0.25f;

    Audio.Play(voPath, loop: false, bus: Audio.Voice);

    Tween.Create()
         .Delay(lineSeconds)
         .TweenFloat(Audio.Music, nameof(AudioBus.Volume), restore, 0.5f, EaseType.OutQuad)
         .Play();
}
```

`TweenFloat` sets a float property by name via reflection, which is exactly what
`AudioBus.Volume` needs. The engine pumps `Tween.UpdateAll` itself.

## 9. Effects

```csharp
using SexyBiscuit.Engine.Audio;

var settings = new AudioEffectSettings
{
    Type        = AudioEffectType.Reverb,
    WetDryMix   = 0.3f,
    ReverbDecay = 0.6f,
    RoomSize    = 0.7f,
};

AudioEffectProcessor.Process(samples, sampleRate, channels, settings);
```

`Process` transforms a raw `float[]` sample buffer **in place**. It is not
connected to `AudioManager` — there is no live effect bus. Use it offline:
decode a clip to samples, process, and rebuild a `SoundEffect`. For a "cave"
version of a sound, bake it at build time rather than at runtime.

## 10. In JavaScript

```js
var handle = Audio.play("Assets/Audio/theme.wav");   // → { id: number }
Audio.playOneShot("Assets/Audio/hit.wav");
Audio.stop(handle);
```

`Audio.play` from script always uses the default bus and never loops — the
bridge calls `AudioManager.Play(path)` with its defaults. Drive music and bus
routing from C#.

---

## Checkpoint

You have:

- A music director with crossfades and ducking
- Positional audio, with the listener tagged correctly
- Pitch variation and rate limiting for repeated sounds
- Volume settings routed through buses and persisted

## Troubleshooting

| Symptom | Cause |
|---|---|
| Silence | not 16-bit PCM WAV, or the path is wrong (check the working directory) |
| `AudioSource` never plays | relying on `PlayOnAwake`; call `Play()` |
| 3D audio always quiet | no actor tagged `"Camera"` — the listener is at the origin |
| Everything is quiet | a bus has `Solo` set, or `Master.Volume` is low |
| Sound clips off | too many concurrent voices; the manager steals the oldest |
| Frame hitch on first play | decode the clip during loading with `LoadOrGet` |

## Exercises

1. Add a low-health heartbeat that fades in as health drops below 25 %.
2. Give `MusicDirector` a stinger method: duck music, play a one-shot, restore.
3. Add an `Ambience` bus with a looping wind track whose volume follows the
   player's height.

---

**Next:** [Tutorial 9 — Animation & Tweens](09-animation-and-tweens.md)
