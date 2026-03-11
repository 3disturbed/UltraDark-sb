namespace SexyBiscuit.Engine.Audio;

/// <summary>
/// An AudioBus is a named routing group that applies volume, pitch, mute, and solo
/// modifiers to every voice assigned to it.  Buses form a tree: Music, SFX, and
/// Voice are children of Master.  EffectiveVolume walks the parent chain so that
/// lowering Master silences everything at once.
///
/// Solo semantics: when any bus is soloed, only soloed buses (and their parents up
/// to Master) produce sound.  The AudioManager polls EffectiveVolume each frame
/// before setting SoundEffectInstance.Volume, so solo / mute changes are instant.
/// </summary>
public class AudioBus
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------

    /// <summary>Human-readable name (e.g. "Master", "Music", "SFX", "Voice").</summary>
    public string Name { get; }

    // -------------------------------------------------------------------------
    // Routing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Optional parent bus.  Master has no parent; Music / SFX / Voice point to Master.
    /// EffectiveVolume multiplies up the chain.
    /// </summary>
    public AudioBus? Parent { get; set; }

    // -------------------------------------------------------------------------
    // Modifiers
    // -------------------------------------------------------------------------

    private float _volume = 1f;

    /// <summary>
    /// Linear volume scalar for this bus.  Clamped to [0, 1].
    /// Changes take effect on the next AudioManager.Update() tick.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    private float _pitch = 0f;

    /// <summary>
    /// Pitch offset in semitone-equivalent units as expected by
    /// SoundEffectInstance.Pitch: [-1, +1] where ±1 is an octave.
    /// Applied additively on top of per-voice pitch.
    /// </summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -1f, 1f);
    }

    /// <summary>
    /// When true all voices on this bus (and child buses) produce silence.
    /// Takes precedence over Solo.
    /// </summary>
    public bool Muted { get; set; } = false;

    /// <summary>
    /// When true this bus is part of the "solo group".  Only buses with Solo == true
    /// (and their ancestors) will be heard.  Managed by AudioManager so that setting
    /// Solo on one bus does not automatically clear others — callers clear Solo manually.
    /// </summary>
    public bool Solo { get; set; } = false;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public AudioBus(string name, AudioBus? parent = null)
    {
        Name   = name;
        Parent = parent;
    }

    // -------------------------------------------------------------------------
    // Effective values
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computed volume after walking the parent chain and applying mute/solo.
    /// This is what AudioManager reads each frame before pushing volume to instances.
    ///
    /// Solo check: if ANY bus anywhere in the manager has Solo == true and this bus
    /// does not (nor any ancestor), EffectiveVolume returns 0.  The manager passes
    /// the global "anySolo" flag via <see cref="GetEffectiveVolume(bool)"/>.
    /// </summary>
    public float EffectiveVolume => GetEffectiveVolume(false);

    /// <summary>
    /// Internal variant called by AudioManager which already knows whether any bus
    /// in the graph has Solo set.
    /// </summary>
    internal float GetEffectiveVolume(bool anySoloActive)
    {
        // Mute is absolute.
        if (Muted) return 0f;

        // Solo: if someone in the graph is soloed, only soloed buses pass through.
        if (anySoloActive && !IsSoloed()) return 0f;

        float vol = _volume;
        if (Parent != null)
            vol *= Parent.GetEffectiveVolume(anySoloActive);

        return vol;
    }

    /// <summary>
    /// Effective pitch contribution from this bus and its parent chain (additive, clamped).
    /// </summary>
    internal float GetEffectivePitch()
    {
        float p = _pitch;
        if (Parent != null)
            p += Parent.GetEffectivePitch();
        return Math.Clamp(p, -1f, 1f);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if this bus or any ancestor has Solo set, meaning audio should
    /// pass through (in a solo context).
    /// </summary>
    private bool IsSoloed()
    {
        if (Solo) return true;
        return Parent?.IsSoloed() ?? false;
    }

    public override string ToString() => $"AudioBus({Name}, vol={_volume:F2}, muted={Muted}, solo={Solo})";
}
