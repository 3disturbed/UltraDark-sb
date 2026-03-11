using System.Runtime.CompilerServices;

namespace SexyBiscuit.Engine.Audio;

/// <summary>
/// Lightweight, value-type token that refers to a managed voice inside AudioManager.
///
/// Handles are cheap to copy and safe to hold indefinitely — IsValid returns false once
/// the underlying voice has been stopped, stolen, or released. Never store a raw
/// SoundEffectInstance; always go through a handle.
/// </summary>
public readonly struct AudioHandle : IEquatable<AudioHandle>
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------

    /// <summary>
    /// Monotonically-increasing generation ID assigned at Play time.
    /// 0 is reserved as "invalid / not yet assigned".
    /// </summary>
    public uint Id { get; init; }

    // -------------------------------------------------------------------------
    // Validity
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if this handle refers to a voice that is still alive in
    /// the AudioManager pool. Validity is checked by querying the manager's
    /// live-handle set; O(1) HashSet lookup.
    /// </summary>
    public bool IsValid
    {
        get
        {
            if (Id == 0) return false;
            var mgr = AudioManager.Instance;
            return mgr != null && mgr.IsHandleActive(this);
        }
    }

    // -------------------------------------------------------------------------
    // Sentinel
    // -------------------------------------------------------------------------

    /// <summary>A handle guaranteed never to match any real voice.</summary>
    public static AudioHandle Invalid => default; // Id == 0

    // -------------------------------------------------------------------------
    // Equality
    // -------------------------------------------------------------------------

    public bool Equals(AudioHandle other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is AudioHandle h && Equals(h);
    public override int GetHashCode() => (int)Id;

    public static bool operator ==(AudioHandle a, AudioHandle b) => a.Id == b.Id;
    public static bool operator !=(AudioHandle a, AudioHandle b) => a.Id != b.Id;

    public override string ToString() => Id == 0 ? "AudioHandle(Invalid)" : $"AudioHandle({Id})";
}
