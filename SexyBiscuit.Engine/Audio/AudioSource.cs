using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Audio;

// =============================================================================
// AudioSource — positional audio component
// =============================================================================
//
// Attach to any Actor to give it a sound emitter.  Supports:
//   • Full 2D / 3D spatial attenuation and left-right panning
//   • Loop / one-shot playback
//   • Per-source fade in / out
//   • PlayOnAwake
//   • Bus routing (defaults to SFX bus)
//
// 3D AUDIO MODEL
// ──────────────
// MonoGame SoundEffectInstance exposes Volume and Pan in [-1, 1].  We map the
// world-space geometry onto these two scalars:
//
//   Distance attenuation (linear):
//       vol_3d = clamp(1 - distance / RolloffDistance, 0, 1)
//
//   Horizontal pan (linear):
//       dx      = actor.x - camera.x          (signed world units)
//       pan     = clamp(dx / RolloffDistance, -1, 1)
//
// Doppler is approximated by tracking the listener's velocity relative to the
// source across frames and converting to a SoundEffectInstance.Pitch offset.
// SoundEffectInstance.Pitch is in the range [-1, +1] where ±1 = ±1 octave.
// We convert the velocity ratio via:
//       pitch_offset = clamp((v_rel / SPEED_OF_SOUND) * DopplerScale, -1, 1)
//
// For true HRTF / distance-model audio, replace this component with a backend
// that drives OpenAL-Soft's AL_EXT_distance_model or XAudio2 X3D audio; see
// AudioEffect.cs for the intended upgrade path.
// =============================================================================

public class AudioSource : Component
{
    // ─────────────────────────────────────────────────────────────────────────
    // Public configuration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The audio clip to play.  Can be swapped at any time.</summary>
    public SoundEffect? Clip { get; set; }

    private float _volume = 1f;
    /// <summary>Base volume [0, 1] before bus and spatial attenuation.</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    private float _pitch = 0f;
    /// <summary>Pitch offset in SoundEffectInstance units: [-1, 1], ±1 = octave.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -1f, 1f);
    }

    /// <summary>Whether the clip loops indefinitely.</summary>
    public bool Loop { get; set; } = false;

    /// <summary>
    /// When true, spatial attenuation and panning are applied each frame
    /// based on the distance from the active camera to this actor.
    /// When false, the clip plays at full volume regardless of position.
    /// </summary>
    public bool Is3D { get; set; } = false;

    /// <summary>
    /// World-space distance (in pixels / units matching your coordinate system)
    /// beyond which the sound is completely silent.  Volume attenuates linearly
    /// from 1 at distance 0 to 0 at this distance.
    /// </summary>
    public float RolloffDistance { get; set; } = 500f;

    /// <summary>
    /// Multiplier applied to the computed Doppler pitch shift.
    /// 0 = no Doppler.  1 = physically realistic (can be jarring for large
    /// velocity changes — 0.3–0.5 is usually more pleasant in a game).
    /// Only active when Is3D == true.
    /// </summary>
    public float DopplerScale { get; set; } = 1f;

    /// <summary>
    /// Target bus.  Null = SFX bus from AudioManager.
    /// </summary>
    public AudioBus? Bus { get; set; }

    /// <summary>
    /// When true the source calls Play() automatically during Awake().
    /// </summary>
    public bool PlayOnAwake { get; set; } = false;

    // ─────────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────────

    private AudioHandle _handle = AudioHandle.Invalid;

    /// <summary>Returns true if a voice is currently active (playing or paused).</summary>
    public bool IsPlaying => _handle.IsValid;

    // Doppler tracking
    private Vector2 _lastSourcePos;
    private Vector2 _lastListenerPos;
    private bool    _posInitialised;

    // Fade state (for FadeIn / FadeOut without going through the manager — we
    // drive volume ourselves to account for spatial attenuation simultaneously)
    private float _fadeTarget   = 1f;
    private float _fadeCurrent  = 1f;
    private float _fadeSpeed    = 0f;   // units per second; 0 = not fading
    private bool  _stopAfterFade = false;

    // Approximate speed of sound in world units per second.  Calibrate this to
    // match your coordinate scale (pixels/unit).  Default assumes ~100 units ≈ 1 m,
    // giving ≈ 34 300 "units/s" — feel free to adjust per project.
    private const float SPEED_OF_SOUND = 34_300f;

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    public override void Awake()
    {
        if (PlayOnAwake)
            Play();
    }

    public override void Update(float dt)
    {
        if (!_handle.IsValid)
        {
            // Voice was stolen or finished naturally — clear our state
            _handle       = AudioHandle.Invalid;
            _posInitialised = false;
            return;
        }

        // ── Advance fade ─────────────────────────────────────────────────────
        if (_fadeSpeed > 0f)
        {
            if (_fadeCurrent < _fadeTarget)
                _fadeCurrent = Math.Min(_fadeCurrent + _fadeSpeed * dt, _fadeTarget);
            else if (_fadeCurrent > _fadeTarget)
                _fadeCurrent = Math.Max(_fadeCurrent - _fadeSpeed * dt, _fadeTarget);

            if (Math.Abs(_fadeCurrent - _fadeTarget) < 0.001f)
            {
                _fadeCurrent = _fadeTarget;
                _fadeSpeed   = 0f;
                if (_stopAfterFade) { Stop(); return; }
            }
        }

        // ── Spatial update ───────────────────────────────────────────────────
        float spatialVol = 1f;
        float pan        = 0f;
        float dopplerPitch = 0f;

        if (Is3D)
        {
            Vector2 sourcePos   = Actor.Transform.Position;
            Vector2 listenerPos = GetListenerPosition();

            if (_posInitialised && dt > 0f)
            {
                // Estimate relative velocity (listener - source) in world units/s
                Vector2 sourceVel   = (sourcePos   - _lastSourcePos)   / dt;
                Vector2 listenerVel = (listenerPos - _lastListenerPos)  / dt;
                Vector2 relVel      = listenerVel - sourceVel;

                // Project onto source-to-listener direction
                Vector2 dir = listenerPos - sourcePos;
                float dist  = dir.Length();
                if (dist > 0.0001f)
                {
                    Vector2 dirNorm = dir / dist;
                    float vRel      = Vector2.Dot(relVel, dirNorm); // positive = approaching
                    dopplerPitch    = Math.Clamp((vRel / SPEED_OF_SOUND) * DopplerScale, -1f, 1f);
                }
            }

            _lastSourcePos   = sourcePos;
            _lastListenerPos = listenerPos;
            _posInitialised  = true;

            // Attenuation
            float distance = Vector2.Distance(sourcePos, listenerPos);
            spatialVol = Math.Clamp(1f - distance / Math.Max(0.001f, RolloffDistance), 0f, 1f);

            // Panning: horizontal offset normalised by rolloff distance
            float dx = sourcePos.X - listenerPos.X;
            pan = Math.Clamp(dx / Math.Max(0.001f, RolloffDistance), -1f, 1f);
        }

        // ── Push to manager ──────────────────────────────────────────────────
        float finalVol   = _volume * _fadeCurrent * spatialVol;
        float finalPitch = _pitch + dopplerPitch;

        SBEngine.Instance.Audio.SetVoiceParameters(_handle, finalVol, finalPitch, pan);
    }

    public override void OnDestroy()
    {
        Stop();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start playing <see cref="Clip"/> (or restart if already playing).
    /// </summary>
    public void Play()
    {
        if (Clip == null) return;

        // Stop any existing voice first
        if (_handle.IsValid)
            SBEngine.Instance.Audio.Stop(_handle);

        var mgr = SBEngine.Instance.Audio;
        var bus  = Bus ?? mgr.SFX;
        _handle  = mgr.Play(Clip, Loop, bus);

        // Reset fade state
        _fadeCurrent   = 1f;
        _fadeTarget    = 1f;
        _fadeSpeed     = 0f;
        _stopAfterFade = false;
        _posInitialised = false;
    }

    /// <summary>Immediately stop playback.</summary>
    public void Stop()
    {
        if (!_handle.IsValid) return;
        SBEngine.Instance.Audio.Stop(_handle);
        _handle         = AudioHandle.Invalid;
        _posInitialised = false;
        _fadeSpeed      = 0f;
        _stopAfterFade  = false;
    }

    /// <summary>
    /// Pause the underlying SoundEffectInstance.
    /// MonoGame SoundEffectInstance supports Pause/Resume natively.
    /// </summary>
    public void Pause()
    {
        if (!_handle.IsValid) return;

        // We don't have direct access to the SoundEffectInstance from a handle,
        // but AudioManager.SetVoiceParameters can drive volume to 0 as a soft
        // pause.  For a true pause (no CPU decode) we need the instance reference.
        // Workaround: set volume to 0 so the voice is silent but still alive.
        // TODO: expose a Pause(AudioHandle) API on AudioManager once it stores
        //       SoundEffectInstance accessors in the public surface.
        SBEngine.Instance.Audio.SetVoiceParameters(_handle, 0f, _pitch, 0f);
    }

    /// <summary>
    /// Fade from silence to full volume over <paramref name="duration"/> seconds.
    /// Can be called before or after Play().
    /// </summary>
    public void FadeIn(float duration)
    {
        if (!_handle.IsValid) Play();
        _fadeCurrent   = 0f;
        _fadeTarget    = 1f;
        _fadeSpeed     = duration > 0f ? 1f / duration : float.MaxValue;
        _stopAfterFade = false;
    }

    /// <summary>
    /// Fade from current volume to silence over <paramref name="duration"/> seconds,
    /// then stop the voice.
    /// </summary>
    public void FadeOut(float duration)
    {
        if (!_handle.IsValid) return;
        _fadeTarget    = 0f;
        _fadeSpeed     = duration > 0f ? 1f / duration : float.MaxValue;
        _stopAfterFade = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the listener position — the world-space position of the active
    /// Camera2D actor in the current scene, or Vector2.Zero if none is found.
    ///
    /// We look for an actor tagged "Camera" in any layer of the active scene.
    /// If your project uses a different tag or a dedicated CameraManager, replace
    /// this lookup with a direct reference or a service-locator call.
    /// </summary>
    private static Vector2 GetListenerPosition()
    {
        var scene = SBEngine.Instance.SceneManager.ActiveScene;
        if (scene == null) return Vector2.Zero;

        // Find the first actor tagged "Camera"
        foreach (var actor in scene.FindByTag("Camera"))
            return actor.Transform.Position;

        return Vector2.Zero;
    }
}
