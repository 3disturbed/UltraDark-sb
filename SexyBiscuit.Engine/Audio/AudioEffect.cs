namespace SexyBiscuit.Engine.Audio;

// =============================================================================
// AudioEffect — software DSP descriptors and PCM processors
// =============================================================================
//
// LIMITATIONS & FUTURE UPGRADE PATH
// ----------------------------------
// MonoGame 3.8 (DesktopGL) exposes no DSP pipeline.  There is no insert-effect
// slot on SoundEffectInstance, no shared audio graph, and no access to the
// underlying OpenAL source filter chain on all platforms.
//
// What we *can* do is pre-process raw PCM float[] data before handing it to
// SoundEffect.FromStream / DynamicSoundEffectInstance.  This is exactly what the
// processors below do.  They operate offline (on load or once per streaming
// buffer) rather than in real-time.
//
// For a future real-time DSP upgrade the intended path is:
//   1. Switch to NAudio's WaveOut / WASAPI graph (Windows) or OpenAL EFX
//      extensions on Linux/macOS, giving insert-effect slots per voice.
//   2. Port AudioEffectProcessor subclasses to IWaveProvider / ISampleProvider
//      chains so they slot in with zero code changes to callers.
//   3. On consoles, map to the platform audio SDK's equivalent.
//
// Until then callers can bake effects into PCM at load time via
// AudioEffectProcessor.Process(float[], sampleRate, channels, settings).
// =============================================================================

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The DSP algorithm to apply.</summary>
public enum AudioEffectType
{
    /// <summary>
    /// Simulated room reverb via a Schroeder-style parallel comb + series
    /// all-pass filter network baked into the PCM data.
    /// </summary>
    Reverb,

    /// <summary>
    /// Tape-echo / slapback delay line written directly into the PCM buffer.
    /// </summary>
    Echo,

    /// <summary>
    /// First-order IIR low-pass filter — attenuates high frequencies above
    /// FilterCutoff Hz.  Useful for muffled / underwater effects.
    /// </summary>
    LowPass,

    /// <summary>
    /// First-order IIR high-pass filter — attenuates low frequencies below
    /// FilterCutoff Hz.  Useful for telephone / radio effects.
    /// </summary>
    HighPass
}

/// <summary>
/// Data-only descriptor for a single audio DSP effect.  Pass to
/// <see cref="AudioEffectProcessor.Process"/> to bake the effect into PCM, or
/// attach to an <see cref="AudioSource"/> for automatic baking on Play().
/// </summary>
public class AudioEffectSettings
{
    /// <summary>Which algorithm to apply.</summary>
    public AudioEffectType Type { get; set; }

    /// <summary>
    /// Blend ratio between processed (wet) and original (dry) signal.
    /// 0 = fully dry, 1 = fully wet.  Default 0.3.
    /// </summary>
    public float WetDryMix { get; set; } = 0.3f;

    // ── Reverb ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reverb tail decay time in seconds.  Longer = larger perceived space.
    /// Typical values: 0.2 (closet) … 2.0 (cathedral).
    /// </summary>
    public float ReverbDecay { get; set; } = 0.5f;

    /// <summary>
    /// Relative room size, [0, 1].  Controls comb-filter delay lengths.
    /// 0.5 is a medium-sized room.
    /// </summary>
    public float RoomSize { get; set; } = 0.5f;

    // ── Echo ─────────────────────────────────────────────────────────────────

    /// <summary>Echo delay time in seconds.  Default 0.3 s.</summary>
    public float EchoDelay { get; set; } = 0.3f;

    /// <summary>
    /// Echo feedback gain [0, 1].  Controls how many repeats are audible.
    /// Values >= 1 will saturate — clamp to 0.95 internally.
    /// </summary>
    public float EchoDecay { get; set; } = 0.5f;

    // ── Filter ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Filter cutoff frequency in Hz.  For LowPass: frequencies above this
    /// are attenuated.  For HighPass: frequencies below this are attenuated.
    /// Default 1000 Hz.
    /// </summary>
    public float FilterCutoff { get; set; } = 1000f;
}

// ─────────────────────────────────────────────────────────────────────────────
// Processor — stateless, operates on float[] PCM in-place
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless PCM processor.  All methods operate on interleaved float samples
/// normalised to [-1, 1].  Samples are modified in-place and clamped on output.
///
/// Usage example (bake reverb into a loaded clip):
/// <code>
///   float[] pcm = LoadPcmFromOgg("music.ogg", out int sr, out int ch);
///   AudioEffectProcessor.Process(pcm, sr, ch, new AudioEffectSettings
///   {
///       Type       = AudioEffectType.Reverb,
///       RoomSize   = 0.7f,
///       ReverbDecay = 1.2f,
///       WetDryMix  = 0.4f
///   });
///   // pcm is now reverb-baked; hand it to SoundEffect.FromStream / DSFI.
/// </code>
/// </summary>
public static class AudioEffectProcessor
{
    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Apply <paramref name="settings"/> to <paramref name="samples"/> in-place.
    /// </summary>
    /// <param name="samples">Interleaved float PCM, channel-count = <paramref name="channels"/>.</param>
    /// <param name="sampleRate">Sample rate in Hz (e.g. 44100).</param>
    /// <param name="channels">Number of audio channels (1 = mono, 2 = stereo).</param>
    /// <param name="settings">Effect descriptor.</param>
    public static void Process(float[] samples, int sampleRate, int channels, AudioEffectSettings settings)
    {
        switch (settings.Type)
        {
            case AudioEffectType.Reverb:   ApplyReverb(samples, sampleRate, channels, settings);  break;
            case AudioEffectType.Echo:     ApplyEcho(samples, sampleRate, channels, settings);    break;
            case AudioEffectType.LowPass:  ApplyLowPass(samples, sampleRate, channels, settings); break;
            case AudioEffectType.HighPass: ApplyHighPass(samples, sampleRate, channels, settings);break;
        }
    }

    // -------------------------------------------------------------------------
    // Reverb  (Schroeder-Moorer: 4 comb filters + 2 all-pass filters per channel)
    // -------------------------------------------------------------------------
    //
    // Reference: Schroeder, M. R. (1962). "Natural sounding artificial reverberation."
    // J. Audio Eng. Soc. 10(3):219–223.
    //
    // Comb filter delay times are scaled by RoomSize (0-1) and sampleRate.
    // Feedback coefficient g = e^(-3 * delay / T60) where T60 = ReverbDecay.
    //
    // NOTE: This implementation is offline/baked.  Real-time reverb would require
    // per-voice state objects persisted across Update() calls.  See upgrade path at
    // the top of this file.

    private static readonly float[] CombDelayMs  = { 29.7f, 37.1f, 41.1f, 43.7f };
    private static readonly float[] AllPassDelayMs = { 5.0f, 1.7f };
    private const  float AllPassGain = 0.7f;

    private static void ApplyReverb(float[] samples, int sampleRate, int channels, AudioEffectSettings s)
    {
        float wet = Math.Clamp(s.WetDryMix, 0f, 1f);
        float dry = 1f - wet;
        float t60 = Math.Max(0.01f, s.ReverbDecay);
        float room = Math.Clamp(s.RoomSize, 0.1f, 1f);

        int frameCount = samples.Length / channels;

        // Process each channel independently to avoid cross-channel artefacts.
        for (int ch = 0; ch < channels; ch++)
        {
            // Extract mono channel
            float[] mono = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
                mono[i] = samples[i * channels + ch];

            float[] wet_buf = new float[frameCount];

            // ── Parallel comb filters ────────────────────────────────────────
            float[] combOut = new float[frameCount];
            foreach (float delayMs in CombDelayMs)
            {
                int delayFrames = Math.Max(1, (int)(delayMs * 0.001f * room * sampleRate));
                float feedback  = MathF.Exp(-3f * delayFrames / (t60 * sampleRate));

                float[] buf   = new float[delayFrames];
                int     pos   = 0;
                float[] comb  = new float[frameCount];

                for (int i = 0; i < frameCount; i++)
                {
                    float delayed  = buf[pos];
                    float output   = mono[i] + feedback * delayed;
                    buf[pos]       = output;
                    pos            = (pos + 1) % delayFrames;
                    comb[i]        = delayed;
                }

                for (int i = 0; i < frameCount; i++)
                    combOut[i] += comb[i];
            }

            // Average comb outputs
            float combScale = 1f / CombDelayMs.Length;
            for (int i = 0; i < frameCount; i++)
                wet_buf[i] = combOut[i] * combScale;

            // ── Series all-pass filters ──────────────────────────────────────
            foreach (float delayMs in AllPassDelayMs)
            {
                int delayFrames = Math.Max(1, (int)(delayMs * 0.001f * sampleRate));
                float g         = AllPassGain;
                float[] buf     = new float[delayFrames];
                int     pos     = 0;

                for (int i = 0; i < frameCount; i++)
                {
                    float delayed  = buf[pos];
                    float v        = wet_buf[i] + g * delayed;
                    buf[pos]       = v;
                    pos            = (pos + 1) % delayFrames;
                    wet_buf[i]     = delayed - g * v;
                }
            }

            // ── Mix and write back ───────────────────────────────────────────
            for (int i = 0; i < frameCount; i++)
            {
                float mixed = dry * mono[i] + wet * wet_buf[i];
                samples[i * channels + ch] = Math.Clamp(mixed, -1f, 1f);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Echo  (single-tap feedback delay line)
    // -------------------------------------------------------------------------
    //
    // y[n] = x[n] + decay * y[n - D]   where D = EchoDelay * sampleRate frames.
    // WetDryMix blends the delayed signal (wet) against the dry input.

    private static void ApplyEcho(float[] samples, int sampleRate, int channels, AudioEffectSettings s)
    {
        float wet      = Math.Clamp(s.WetDryMix, 0f, 1f);
        float dry      = 1f - wet;
        float feedback = Math.Clamp(s.EchoDecay, 0f, 0.95f);
        int   delayFrames = Math.Max(1, (int)(s.EchoDelay * sampleRate));

        int frameCount = samples.Length / channels;

        for (int ch = 0; ch < channels; ch++)
        {
            float[] delayBuf = new float[delayFrames];
            int     pos      = 0;

            for (int i = 0; i < frameCount; i++)
            {
                float input   = samples[i * channels + ch];
                float delayed = delayBuf[pos];
                float output  = input + feedback * delayed;
                delayBuf[pos] = output;
                pos           = (pos + 1) % delayFrames;

                float mixed = dry * input + wet * delayed;
                samples[i * channels + ch] = Math.Clamp(mixed, -1f, 1f);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Low-pass filter  (first-order IIR)
    // -------------------------------------------------------------------------
    //
    // H(z) = (1-a) / (1 - a*z^-1)   where a = e^(-2π * fc / sr)
    // y[n] = (1-a) * x[n] + a * y[n-1]
    //
    // This is a single-pole RC-equivalent filter.  It rolls off at 6 dB/octave
    // above fc, which is gentle but zero-latency and artifact-free.
    //
    // For steeper slopes, cascade multiple passes (each pass adds another pole).

    private static void ApplyLowPass(float[] samples, int sampleRate, int channels, AudioEffectSettings s)
    {
        float wet = Math.Clamp(s.WetDryMix, 0f, 1f);
        float dry = 1f - wet;
        float fc  = Math.Clamp(s.FilterCutoff, 1f, sampleRate * 0.499f);
        float a   = MathF.Exp(-2f * MathF.PI * fc / sampleRate);
        float b   = 1f - a;

        int frameCount = samples.Length / channels;

        for (int ch = 0; ch < channels; ch++)
        {
            float prev = 0f;
            for (int i = 0; i < frameCount; i++)
            {
                float x = samples[i * channels + ch];
                float y = b * x + a * prev;
                prev    = y;
                samples[i * channels + ch] = Math.Clamp(dry * x + wet * y, -1f, 1f);
            }
        }
    }

    // -------------------------------------------------------------------------
    // High-pass filter  (first-order IIR)
    // -------------------------------------------------------------------------
    //
    // Derived from the low-pass by complementary subtraction:
    //   y_hp[n] = x[n] - y_lp[n]
    //
    // This gives the same -6 dB/octave roll-off below fc with zero latency.

    private static void ApplyHighPass(float[] samples, int sampleRate, int channels, AudioEffectSettings s)
    {
        float wet = Math.Clamp(s.WetDryMix, 0f, 1f);
        float dry = 1f - wet;
        float fc  = Math.Clamp(s.FilterCutoff, 1f, sampleRate * 0.499f);
        float a   = MathF.Exp(-2f * MathF.PI * fc / sampleRate);
        float b   = 1f - a;

        int frameCount = samples.Length / channels;

        for (int ch = 0; ch < channels; ch++)
        {
            float prev = 0f;
            for (int i = 0; i < frameCount; i++)
            {
                float x    = samples[i * channels + ch];
                float ylp  = b * x + a * prev;
                prev       = ylp;
                float yhp  = x - ylp;   // high-pass = input minus low-pass
                samples[i * channels + ch] = Math.Clamp(dry * x + wet * yhp, -1f, 1f);
            }
        }
    }
}
