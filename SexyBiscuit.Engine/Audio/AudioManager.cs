using Microsoft.Xna.Framework.Audio;
#if !ANDROID
using NAudio.Wave;
#endif
using NVorbis;

namespace SexyBiscuit.Engine.Audio;

// =============================================================================
// AudioManager
// =============================================================================
//
// Central audio service.  Owns a fixed pool of up to MAX_VOICES
// SoundEffectInstance objects.  Voices are acquired on Play/PlayOneShot and
// released automatically when the underlying SoundEffectInstance reports
// SoundState.Stopped.
//
// Voice stealing: when the pool is full and a new sound is requested, the
// oldest non-looping voice is stolen.  If all voices are looping, the oldest
// looping voice is stolen (last resort).
//
// Thread safety: AudioManager is single-threaded and must be called from the
// main game thread.  File I/O during Load() is synchronous; for async loading
// use AssetManager's background loading pipeline (not part of this class).
//
// File format support:
//   .wav  — SoundEffect.FromStream (MonoGame built-in)
//   .ogg  — NVorbis VorbisReader → PCM → SoundEffect.FromStream
//   .mp3  — NAudio Mp3FileReader  → PCM → SoundEffect.FromStream
// =============================================================================

public class AudioManager : IDisposable
{
    // ─────────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────────

    private const int MAX_VOICES = 32;

    // ─────────────────────────────────────────────────────────────────────────
    // Singleton access (read by AudioHandle.IsValid)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set by SBEngine.Initialize().  AudioHandle.IsValid queries this.
    /// Not a design singleton — always access via SBEngine.Instance.Audio.
    /// </summary>
    internal static AudioManager? Instance { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Buses
    // ─────────────────────────────────────────────────────────────────────────

    public AudioBus Master { get; }
    public AudioBus Music  { get; }
    public AudioBus SFX    { get; }
    public AudioBus Voice  { get; }

    // ─────────────────────────────────────────────────────────────────────────
    // Voice pool
    // ─────────────────────────────────────────────────────────────────────────

    private readonly VoiceEntry[] _pool = new VoiceEntry[MAX_VOICES];
    private uint _nextHandleId = 1;

    // For O(1) IsHandleActive checks from AudioHandle.IsValid
    private readonly HashSet<uint> _activeIds = new(MAX_VOICES);

    // Pending fade operations, keyed by handle ID
    private readonly Dictionary<uint, FadeState> _fades = new();

    // Cross-fade state (at most one at a time)
    private CrossFadeState? _crossFade;

    // ─────────────────────────────────────────────────────────────────────────
    // SoundEffect asset cache  (path → SoundEffect)
    // Prevents re-decoding the same file on repeated Play() calls.
    // ─────────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, SoundEffect> _cache = new(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Construction / disposal
    // ─────────────────────────────────────────────────────────────────────────

    public AudioManager()
    {
        Instance = this;

        Master = new AudioBus("Master");
        Music  = new AudioBus("Music",  Master);
        SFX    = new AudioBus("SFX",    Master);
        Voice  = new AudioBus("Voice",  Master);
    }

    public void Dispose()
    {
        // Stop and release all voices
        for (int i = 0; i < MAX_VOICES; i++)
        {
            ref var v = ref _pool[i];
            if (v.Instance != null)
            {
                v.Instance.Stop();
                v.Instance.Dispose();
                v.Instance = null;
            }
        }

        // Release cached SoundEffects
        foreach (var sfx in _cache.Values)
            sfx.Dispose();
        _cache.Clear();
        _activeIds.Clear();

        if (Instance == this)
            Instance = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Update  (called every frame by SBEngine)
    // ─────────────────────────────────────────────────────────────────────────

    public void Update(float dt)
    {
        bool anySolo = HasAnySolo();

        // ── 1. Process fades ─────────────────────────────────────────────────
        var toRemove = new List<uint>();
        foreach (var (id, fade) in _fades)
        {
            int slot = FindSlotById(id);
            if (slot < 0) { toRemove.Add(id); continue; }

            ref var v = ref _pool[slot];
            fade.Elapsed += dt;
            float t = Math.Clamp(fade.Elapsed / fade.Duration, 0f, 1f);

            if (fade.IsFadeOut)
            {
                float vol = fade.StartVolume * (1f - t);
                ApplyVolume(ref v, vol, anySolo);
                if (t >= 1f) { StopSlot(slot); toRemove.Add(id); }
            }
            else
            {
                float vol = MathHelper_Lerp(fade.StartVolume, fade.TargetVolume, t);
                ApplyVolume(ref v, vol, anySolo);
                if (t >= 1f) toRemove.Add(id);
            }
        }
        foreach (var id in toRemove) _fades.Remove(id);

        // ── 2. Process cross-fade ────────────────────────────────────────────
        if (_crossFade != null)
        {
            var cf = _crossFade;
            cf.Elapsed += dt;
            float t = Math.Clamp(cf.Elapsed / cf.Duration, 0f, 1f);

            // Fade out the old voice
            int oldSlot = FindSlotById(cf.OldHandleId);
            if (oldSlot >= 0)
            {
                float vol = cf.OldStartVolume * (1f - t);
                ApplyVolume(ref _pool[oldSlot], vol, anySolo);
            }

            // Fade in the new voice
            int newSlot = FindSlotById(cf.NewHandleId);
            if (newSlot >= 0)
            {
                float vol = MathHelper_Lerp(0f, cf.NewTargetVolume, t);
                ApplyVolume(ref _pool[newSlot], vol, anySolo);
            }

            if (t >= 1f)
            {
                if (oldSlot >= 0) StopSlot(oldSlot);
                _crossFade = null;
            }
        }

        // ── 3. Retire finished voices ────────────────────────────────────────
        for (int i = 0; i < MAX_VOICES; i++)
        {
            ref var v = ref _pool[i];
            if (v.Instance == null) continue;
            if (v.Instance.State == SoundState.Stopped)
                RetireSlot(i);
        }

        // ── 4. Re-apply bus volumes to all live voices ───────────────────────
        //     (handles runtime mute / solo / volume slider changes)
        for (int i = 0; i < MAX_VOICES; i++)
        {
            ref var v = ref _pool[i];
            if (v.Instance == null) continue;
            if (_fades.ContainsKey(v.HandleId)) continue; // fade manages its own volume
            if (_crossFade != null &&
                (v.HandleId == _crossFade.OldHandleId || v.HandleId == _crossFade.NewHandleId)) continue;

            // Cache the instance: passing v by ref past this point defeats the
            // compiler's null-flow analysis even though the slot cannot change here.
            var instance = v.Instance;
            ApplyVolume(ref v, v.RequestedVolume, anySolo);
            instance.Pitch = Math.Clamp(
                v.RequestedPitch + (v.Bus?.GetEffectivePitch() ?? 0f), -1f, 1f);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // One-shot helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget playback on the SFX bus.  No handle is returned; the voice
    /// is automatically retired when the clip finishes.
    /// </summary>
    public void PlayOneShot(string path, float volume = 1f, float pitch = 0f)
    {
        var sfx = LoadOrGet(path);
        if (sfx == null) return;
        PlayOneShot(sfx, volume, pitch);
    }

    /// <inheritdoc cref="PlayOneShot(string,float,float)"/>
    public void PlayOneShot(SoundEffect sfx, float volume = 1f, float pitch = 0f)
    {
        // PlayOneShot uses SoundEffect.Play() for zero-allocation fire-and-forget.
        // MonoGame routes this through a shared internal pool — it doesn't consume
        // from our MAX_VOICES pool, so it bypasses bus volume/pitch on the voice.
        // We therefore compute the effective volume now and bake it into the call.
        bool anySolo = HasAnySolo();
        float busVol = SFX.GetEffectiveVolume(anySolo);
        float busPitch = SFX.GetEffectivePitch();

        float finalVol   = Math.Clamp(volume * busVol, 0f, 1f);
        float finalPitch = Math.Clamp(pitch + busPitch, -1f, 1f);

        sfx.Play(finalVol, finalPitch, 0f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Managed playback
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begin playback and return a handle for later control (Stop, Fade, etc.).
    /// </summary>
    /// <param name="path">Path to audio file (.wav / .ogg / .mp3).</param>
    /// <param name="loop">Whether to loop indefinitely.</param>
    /// <param name="bus">Target bus.  Null defaults to the SFX bus.</param>
    public AudioHandle Play(string path, bool loop = false, AudioBus? bus = null)
    {
        var sfx = LoadOrGet(path);
        if (sfx == null) return AudioHandle.Invalid;
        return Play(sfx, loop, bus);
    }

    /// <inheritdoc cref="Play(string,bool,AudioBus?)"/>
    public AudioHandle Play(SoundEffect sfx, bool loop = false, AudioBus? bus = null)
    {
        bus ??= SFX;

        int slot = AcquireSlot(loop);
        if (slot < 0) return AudioHandle.Invalid;

        ref var v = ref _pool[slot];

        // If we stole an occupied slot, clean up first
        if (v.Instance != null)
        {
            v.Instance.Stop();
            v.Instance.Dispose();
            _activeIds.Remove(v.HandleId);
        }

        uint id = _nextHandleId++;
        var inst = sfx.CreateInstance();
        inst.IsLooped = loop;

        v.Instance        = inst;
        v.HandleId        = id;
        v.Bus             = bus;
        v.RequestedVolume = 1f;
        v.RequestedPitch  = 0f;
        v.SpawnOrder      = id; // id is monotonic, so it also orders spawn time
        v.IsLooping       = loop;

        bool anySolo = HasAnySolo();
        ApplyVolume(ref v, 1f, anySolo);
        inst.Pitch = Math.Clamp(bus.GetEffectivePitch(), -1f, 1f);
        inst.Play();

        _activeIds.Add(id);
        return new AudioHandle { Id = id };
    }

    /// <summary>Immediately stop the voice associated with <paramref name="handle"/>.</summary>
    public void Stop(AudioHandle handle)
    {
        if (handle.Id == 0) return;
        int slot = FindSlotById(handle.Id);
        if (slot < 0) return;
        StopSlot(slot);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fades
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Fade the voice in from silence to its current requested volume.</summary>
    public void FadeIn(AudioHandle handle, float duration)
    {
        if (handle.Id == 0) return;
        int slot = FindSlotById(handle.Id);
        if (slot < 0) return;

        ref var v = ref _pool[slot];
        _fades[handle.Id] = new FadeState
        {
            Duration      = Math.Max(0.001f, duration),
            StartVolume   = 0f,
            TargetVolume  = v.RequestedVolume,
            IsFadeOut     = false,
            Elapsed       = 0f
        };
        ApplyVolume(ref v, 0f, HasAnySolo());
    }

    /// <summary>Fade the voice out to silence, then stop it.</summary>
    public void FadeOut(AudioHandle handle, float duration)
    {
        if (handle.Id == 0) return;
        int slot = FindSlotById(handle.Id);
        if (slot < 0) return;

        ref var v = ref _pool[slot];
        float currentVol = v.Instance!.Volume;
        _fades[handle.Id] = new FadeState
        {
            Duration    = Math.Max(0.001f, duration),
            StartVolume = currentVol,
            IsFadeOut   = true,
            Elapsed     = 0f
        };
    }

    /// <summary>
    /// Fade out the currently playing voice on <paramref name="bus"/> while
    /// simultaneously fading in a new clip from <paramref name="newPath"/>.
    /// </summary>
    public void CrossFade(string newPath, float duration, AudioBus? bus = null)
    {
        bus ??= Music;

        // Find the "oldest" active voice on this bus to cross-fade from.
        uint oldId        = 0;
        float oldStartVol = 0f;

        for (int i = 0; i < MAX_VOICES; i++)
        {
            ref var v = ref _pool[i];
            if (v.Instance == null || v.Bus != bus) continue;
            oldId        = v.HandleId;
            oldStartVol  = v.Instance.Volume;
            break;
        }

        // Start the new track at volume 0
        var newHandle = Play(newPath, loop: false, bus: bus);
        if (!newHandle.IsValid) return;

        int newSlot = FindSlotById(newHandle.Id);
        if (newSlot >= 0)
            ApplyVolume(ref _pool[newSlot], 0f, HasAnySolo());

        _crossFade = new CrossFadeState
        {
            OldHandleId   = oldId,
            NewHandleId   = newHandle.Id,
            Duration      = Math.Max(0.001f, duration),
            OldStartVolume = oldStartVol,
            NewTargetVolume = 1f,
            Elapsed        = 0f
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal handle validity query (used by AudioHandle.IsValid)
    // ─────────────────────────────────────────────────────────────────────────

    internal bool IsHandleActive(AudioHandle handle) => _activeIds.Contains(handle.Id);

    // ─────────────────────────────────────────────────────────────────────────
    // Internal: allow AudioSource to set per-voice volume/pitch/pan directly
    // ─────────────────────────────────────────────────────────────────────────

    internal void SetVoiceParameters(AudioHandle handle, float volume, float pitch, float pan)
    {
        int slot = FindSlotById(handle.Id);
        if (slot < 0) return;
        ref var v = ref _pool[slot];
        if (v.Instance == null) return;

        v.RequestedVolume = Math.Clamp(volume, 0f, 1f);
        v.RequestedPitch  = Math.Clamp(pitch,  -1f, 1f);

        bool anySolo = HasAnySolo();
        var instance = v.Instance;
        ApplyVolume(ref v, v.RequestedVolume, anySolo);
        instance.Pitch = Math.Clamp(v.RequestedPitch + (v.Bus?.GetEffectivePitch() ?? 0f), -1f, 1f);
        instance.Pan   = Math.Clamp(pan, -1f, 1f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // File loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a SoundEffect from disk (or return cached).  Supports .wav, .ogg, .mp3.
    /// Returns null and logs a warning if the file cannot be found or decoded.
    /// </summary>
    public SoundEffect? LoadOrGet(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        if (!File.Exists(path))
        {
            System.Diagnostics.Debug.WriteLine($"[AudioManager] File not found: {path}");
            return null;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        SoundEffect? sfx = ext switch
        {
            ".wav" => LoadWav(path),
            ".ogg" => LoadOgg(path),
            ".mp3" => LoadMp3(path),
            _      => null
        };

        if (sfx == null)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioManager] Unsupported format or decode error: {path}");
            return null;
        }

        _cache[path] = sfx;
        return sfx;
    }

    // ─── WAV ─────────────────────────────────────────────────────────────────

    private static SoundEffect? LoadWav(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return SoundEffect.FromStream(fs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioManager] WAV load error ({path}): {ex.Message}");
            return null;
        }
    }

    // ─── OGG (NVorbis) ───────────────────────────────────────────────────────

    private static SoundEffect? LoadOgg(string path)
    {
        try
        {
            using var reader = new NVorbis.VorbisReader(path);
            int sampleRate = reader.SampleRate;
            int channels   = reader.Channels;

            // Read all samples into a float buffer, then convert to 16-bit PCM.
            // VorbisReader.ReadSamples fills interleaved float[-1,1] samples.
            var floatSamples = new List<float>(sampleRate * channels * 10); // pre-alloc ~10s
            var readBuf      = new float[sampleRate * channels];             // 1-second chunks
            int read;
            while ((read = reader.ReadSamples(readBuf, 0, readBuf.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    floatSamples.Add(readBuf[i]);
            }

            return FloatPcmToSoundEffect(floatSamples.ToArray(), sampleRate, channels);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioManager] OGG load error ({path}): {ex.Message}");
            return null;
        }
    }

    // ─── MP3 (NAudio) ────────────────────────────────────────────────────────

    private static SoundEffect? LoadMp3(string path)
    {
#if ANDROID
        // NAudio is a Windows-first audio stack and is not referenced on Android.
        // MP3 is the only format that went through it; Ogg and WAV are decoded above
        // and cover everything the templates ship.
        System.Console.Error.WriteLine($"[AudioManager] MP3 is not supported on Android: '{path}'. Use .ogg or .wav.");
        return null;
#else
        try
        {
            using var reader  = new Mp3FileReader(path);
            // Convert to IEEE float via NAudio's SampleChannel for a clean path to PCM
            var sampleProvider = reader.ToSampleProvider();
            int sampleRate     = sampleProvider.WaveFormat.SampleRate;
            int channels       = sampleProvider.WaveFormat.Channels;

            var floatSamples = new List<float>(sampleRate * channels * 10);
            var buf          = new float[4096];
            int read;
            while ((read = sampleProvider.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    floatSamples.Add(buf[i]);
            }

            return FloatPcmToSoundEffect(floatSamples.ToArray(), sampleRate, channels);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioManager] MP3 load error ({path}): {ex.Message}");
            return null;
        }
#endif
    }

    // ─── PCM conversion helper ───────────────────────────────────────────────

    /// <summary>
    /// Convert an interleaved float[-1,1] buffer to a SoundEffect via a
    /// WAV-header MemoryStream understood by SoundEffect.FromStream.
    /// </summary>
    private static SoundEffect FloatPcmToSoundEffect(float[] samples, int sampleRate, int channels)
    {
        // Convert float → 16-bit signed integer PCM
        short[] pcm16 = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            pcm16[i] = (short)(s * short.MaxValue);
        }

        // Build a minimal WAV stream in memory so SoundEffect.FromStream can parse it.
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int byteRate    = sampleRate * channels * 2;  // 16-bit = 2 bytes per sample
            int blockAlign  = channels * 2;
            int dataSize    = pcm16.Length * 2;
            int chunkSize   = 36 + dataSize;

            // RIFF header
            bw.Write(new char[] { 'R', 'I', 'F', 'F' });
            bw.Write(chunkSize);
            bw.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt  sub-chunk
            bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16);           // sub-chunk size
            bw.Write((short)1);     // PCM format
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)blockAlign);
            bw.Write((short)16);    // bits per sample

            // data sub-chunk
            bw.Write(new char[] { 'd', 'a', 't', 'a' });
            bw.Write(dataSize);
            foreach (short s in pcm16)
                bw.Write(s);
        }

        ms.Position = 0;
        return SoundEffect.FromStream(ms);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pool management helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Find a free slot.  If no free slot exists, steal the oldest non-looping
    /// voice.  If all voices are looping, steal the oldest looping voice.
    /// Returns -1 only if MAX_VOICES == 0 (which is never true in practice).
    /// </summary>
    private int AcquireSlot(bool willBeLooping)
    {
        // First pass: find an empty slot
        for (int i = 0; i < MAX_VOICES; i++)
            if (_pool[i].Instance == null) return i;

        // Second pass: steal oldest non-looping voice
        int stealSlot     = -1;
        uint oldestOrder  = uint.MaxValue;

        for (int i = 0; i < MAX_VOICES; i++)
        {
            ref var v = ref _pool[i];
            if (!v.IsLooping && v.SpawnOrder < oldestOrder)
            {
                oldestOrder = v.SpawnOrder;
                stealSlot   = i;
            }
        }
        if (stealSlot >= 0) return stealSlot;

        // Last resort: steal oldest looping voice
        oldestOrder = uint.MaxValue;
        for (int i = 0; i < MAX_VOICES; i++)
        {
            ref var v = ref _pool[i];
            if (v.SpawnOrder < oldestOrder)
            {
                oldestOrder = v.SpawnOrder;
                stealSlot   = i;
            }
        }
        return stealSlot;
    }

    private int FindSlotById(uint id)
    {
        for (int i = 0; i < MAX_VOICES; i++)
            if (_pool[i].HandleId == id) return i;
        return -1;
    }

    private void StopSlot(int slot)
    {
        ref var v = ref _pool[slot];
        if (v.Instance == null) return;
        v.Instance.Stop();
        RetireSlot(slot);
    }

    private void RetireSlot(int slot)
    {
        ref var v = ref _pool[slot];
        if (v.Instance == null) return;

        _activeIds.Remove(v.HandleId);
        _fades.Remove(v.HandleId);

        v.Instance.Dispose();
        v.Instance  = null;
        v.HandleId  = 0;
        v.Bus       = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Volume application
    // ─────────────────────────────────────────────────────────────────────────

    private static void ApplyVolume(ref VoiceEntry v, float requestedVol, bool anySolo)
    {
        if (v.Instance == null) return;
        float busVol  = v.Bus?.GetEffectiveVolume(anySolo) ?? 1f;
        float final   = Math.Clamp(requestedVol * busVol, 0f, 1f);
        v.Instance.Volume = final;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Solo query
    // ─────────────────────────────────────────────────────────────────────────

    private bool HasAnySolo()
        => Master.Solo || Music.Solo || SFX.Solo || Voice.Solo;

    // ─────────────────────────────────────────────────────────────────────────
    // MathHelper compatibility shim
    // ─────────────────────────────────────────────────────────────────────────

    private static float MathHelper_Lerp(float a, float b, float t)
        => a + (b - a) * t;

    // ─────────────────────────────────────────────────────────────────────────
    // Inner types
    // ─────────────────────────────────────────────────────────────────────────

    private struct VoiceEntry
    {
        public SoundEffectInstance? Instance;
        public uint                 HandleId;
        public AudioBus?            Bus;
        public float                RequestedVolume;
        public float                RequestedPitch;
        public uint                 SpawnOrder;   // lower = spawned earlier
        public bool                 IsLooping;
    }

    private class FadeState
    {
        public float Duration;
        public float StartVolume;
        public float TargetVolume;
        public bool  IsFadeOut;
        public float Elapsed;
    }

    private class CrossFadeState
    {
        public uint  OldHandleId;
        public uint  NewHandleId;
        public float Duration;
        public float OldStartVolume;
        public float NewTargetVolume;
        public float Elapsed;
    }
}
