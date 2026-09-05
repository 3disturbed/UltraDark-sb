# 8. Audio

Namespace: `SexyBiscuit.Engine.Audio`

`AudioManager` is created by `SBEngine` and pumped for you in `Update` — no
wiring needed.

```csharp
var audio = SBEngine.Instance.Audio;
```

Built on MonoGame's `SoundEffect` / `SoundEffectInstance`. Every playing voice
is tracked by the manager, routed through a **bus**, and addressed by an opaque
**handle**.

---

## Buses

Four buses exist from construction, in a parent chain:

```
Master
├── Music
├── SFX
└── Voice
```

```csharp
audio.Master.Volume = 0.8f;
audio.Music.Volume  = 0.5f;
audio.SFX.Volume    = 1.0f;
audio.Voice.Volume  = 0.9f;
```

```csharp
public class AudioBus
{
    public string    Name    { get; }
    public AudioBus? Parent  { get; set; }
    public float     Volume  { get; set; }   // clamped 0..1
    public float     Pitch   { get; set; }
    public bool      Muted   { get; set; }
    public bool      Solo    { get; set; }
    public float     EffectiveVolume { get; }
}
```

`EffectiveVolume` multiplies up the parent chain, so `Master.Volume = 0` silences
everything. `Muted` zeroes the bus; `Solo` on any bus silences the others.

Custom buses:

```csharp
var ambience = new AudioBus("Ambience", audio.Master);
audio.Play("Assets/Audio/wind.ogg", loop: true, bus: ambience);
```

An options menu maps naturally onto buses:

```csharp
masterSlider.OnValueChanged += v => { audio.Master.Volume = v; PlayerPrefs.SetFloat("vol.master", v); };
musicSlider .OnValueChanged += v => { audio.Music.Volume  = v; PlayerPrefs.SetFloat("vol.music",  v); };
sfxSlider   .OnValueChanged += v => { audio.SFX.Volume    = v; PlayerPrefs.SetFloat("vol.sfx",    v); };
```

---

## Playing sound

### Fire and forget

```csharp
audio.PlayOneShot("Assets/Audio/hit.wav");
audio.PlayOneShot("Assets/Audio/hit.wav", volume: 0.7f, pitch: 0.2f);
audio.PlayOneShot(loadedSoundEffect, volume: 1f, pitch: 0f);
```

`pitch` follows MonoGame's convention: `−1` is an octave down, `0` unchanged,
`+1` an octave up.

### Managed playback

```csharp
AudioHandle music = audio.Play("Assets/Audio/theme.ogg", loop: true, bus: audio.Music);
audio.Stop(music);
```

`AudioHandle` is a readonly struct wrapping a `uint`. `AudioHandle.Invalid` has
`Id == 0`; `handle.IsValid` tells you whether the voice is still alive. Handles
are safe to store — stopping an already-finished handle is a no-op.

### Fades and crossfades

```csharp
audio.FadeIn(handle, duration: 1.5f);
audio.FadeOut(handle, duration: 2f);          // stops the voice when it reaches zero
audio.CrossFade("Assets/Audio/battle.ogg", duration: 2f, bus: audio.Music);
```

`CrossFade` starts the new track, ramps it up while ramping the current track on
that bus down, and releases the old voice at the end. Fades are advanced in
`AudioManager.Update`.

### Loading

```csharp
SoundEffect? sfx = audio.LoadOrGet("Assets/Audio/hit.wav");
```

`LoadOrGet` caches by path. `AssetManager.Load<SoundEffect>` also works and
participates in reference counting — prefer it when the clip belongs to a level
you will unload.

Supported by MonoGame's `SoundEffect.FromStream`: **16-bit PCM WAV**. OGG and
MP3 are listed in the design document and NAudio is referenced by the project,
but `AudioManager` decodes through `SoundEffect.FromStream`, so **ship WAV**
unless you add a decode step yourself.

---

## AudioSource

A component that ties a clip to an actor, with optional 3D positioning.

```csharp
var src = actor.AddComponent<AudioSource>();
src.Clip            = audio.LoadOrGet("Assets/Audio/engine.wav");
src.Volume          = 0.8f;
src.Pitch           = 0f;
src.Loop            = true;
src.Bus             = audio.SFX;
src.PlayOnAwake     = false;

src.Is3D            = true;
src.RolloffDistance = 500f;    // volume reaches zero at this distance
src.DopplerScale    = 1f;
```

```csharp
src.Play();
src.Stop();
src.Pause();
src.FadeIn(1f);
src.FadeOut(1f);
bool playing = src.IsPlaying;
```

`PlayOnAwake` is checked in `Awake`, which runs the instant you call
`AddComponent` — before you have had a chance to assign `Clip`. It therefore
never fires for a component you add in code. Call `Play()` explicitly:

```csharp
var src = actor.AddComponent<AudioSource>();
src.Clip = clip;
src.Loop = true;
src.Play();          // rather than relying on PlayOnAwake
```

When `Is3D` is set, the component attenuates, pans, and Doppler-shifts by the
distance between its actor and the **listener** each `Update`. Volume falls off
linearly to zero at `RolloffDistance`, in world units — match your
[physics units convention](06-physics.md#units--read-this-first).

### The listener is an actor tagged `"Camera"` — Verified

```csharp
// AudioSource.GetListenerPosition(), simplified:
foreach (var actor in scene.FindByTag("Camera"))
    return actor.Transform.Position;
return Vector2.Zero;
```

The first actor in the active scene whose `Tag` is exactly `"Camera"` is the
listener. If no actor carries that tag, the listener sits at the world origin
and every 3D source is attenuated by its distance from `(0,0)`. Tag your camera:

```csharp
var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
```

Note this differs from the tags other systems look for — `Camera3D.Main` wants
`"MainCamera3D"`, and the bundled scene templates use `"MainCamera"`. If you use
3D audio and a 3D camera on the same actor, you can only satisfy one of them;
put the `AudioSource` listener tag on a separate child actor parented to the
camera.

---

## Audio effects

```csharp
var settings = new AudioEffectSettings
{
    Type         = AudioEffectType.Reverb,
    WetDryMix    = 0.3f,
    ReverbDecay  = 0.6f,
    RoomSize     = 0.7f,
    EchoDelay    = 0.3f,
    EchoDecay    = 0.5f,
    FilterCutoff = 1200f,
};

AudioEffectProcessor.Process(samples, sampleRate, channels, settings);
```

`AudioEffectProcessor.Process` transforms a raw `float[]` sample buffer in
place. It is **not** connected to `AudioManager` — there is no live effect bus.
Use it offline: decode a clip to samples, process, and rebuild a `SoundEffect`
from the result.

---

## Recipes

### Music that survives scene changes

```csharp
var musicActor = new Actor("Music");
var src = musicActor.AddComponent<AudioSource>();
src.Clip = audio.LoadOrGet("Assets/Audio/theme.ogg");
src.Loop = true;
src.Bus  = audio.Music;
src.Play();

scene.AddActor(musicActor, "background");
SceneManager.DontDestroyOnLoad(musicActor);
```

### Pitch variation so repeats do not fatigue

```csharp
static readonly Random Rng = new();

void Footstep()
{
    float pitch = (float)(Rng.NextDouble() * 0.3 - 0.15);   // ±0.15
    SBEngine.Instance.Audio.PlayOneShot("Assets/Audio/step.wav", 0.6f, pitch);
}
```

### Ducking music under dialogue

```csharp
float restore = audio.Music.Volume;
audio.Music.Volume = restore * 0.25f;
var line = audio.Play("Assets/Audio/vo_line01.wav", bus: audio.Voice);

Tween.Create()
     .Delay(lineLengthSeconds)
     .TweenFloat(audio.Music, nameof(AudioBus.Volume), restore, 0.5f, EaseType.OutQuad)
     .Play();
```

`TweenFloat` sets a float property by name via reflection, which is exactly what
you need for `AudioBus.Volume`. The engine pumps `Tween.UpdateAll` itself.

---

## In JavaScript

```js
var handle = Audio.play("Assets/Audio/theme.ogg");   // { id: number }
Audio.playOneShot("Assets/Audio/hit.wav");
Audio.stop(handle);
```

`Audio.play` from script always uses the default bus and does not loop —
`AudioManager.Play(path)` is called with its defaults. For looping music or bus
routing, drive it from C#.

---

## Next

- [9. UI](09-ui.md)
- [Tutorial 8: Audio](../tutorials/08-audio.md)
