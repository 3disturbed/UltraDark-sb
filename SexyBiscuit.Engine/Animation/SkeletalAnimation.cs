using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Animation;

// ---------------------------------------------------------------------------
// Bone definition
// ---------------------------------------------------------------------------

/// <summary>
/// A single bone in the skeletal hierarchy.  Mirrors what Assimp.NET exposes after
/// import: a name, a hierarchy index, the bind-pose transform, and its inverse.
/// </summary>
public class Bone
{
    /// <summary>Bone name (matches channel names in <see cref="SkeletalClip"/>).</summary>
    public string Name        { get; set; } = "";

    /// <summary>Index into the flat <see cref="SkeletalAnimator.Skeleton"/> list.</summary>
    public int    Index       { get; set; }

    /// <summary>Index of the parent bone, or -1 for root bones.</summary>
    public int    ParentIndex { get; set; } = -1;

    /// <summary>Local-space transform of the bone in the rest pose.</summary>
    public Matrix BindPose    { get; set; } = Matrix.Identity;

    /// <summary>
    /// Inverse of the world-space bind pose.  Transforms a vertex from model space
    /// into bone space so the bone delta can be applied correctly.
    /// </summary>
    public Matrix InvBindPose { get; set; } = Matrix.Identity;
}

// ---------------------------------------------------------------------------
// Keyframe data
// ---------------------------------------------------------------------------

/// <summary>
/// One keyframe of a bone channel, storing TRS (Translation / Rotation / Scale)
/// at a given time in seconds.
/// </summary>
public class AnimationKeyframe
{
    public float     Time        { get; set; }
    public Vector3   Translation { get; set; }
    public Quaternion Rotation   { get; set; }
    public Vector3   Scale       { get; set; }
}

// ---------------------------------------------------------------------------
// Bone channel — animation data for one bone
// ---------------------------------------------------------------------------

/// <summary>
/// All keyframes for a single bone within a <see cref="SkeletalClip"/>.
/// Provides linear interpolation between keyframes.
/// </summary>
public class BoneChannel
{
    public int BoneIndex { get; set; }

    public List<AnimationKeyframe> Keyframes { get; set; } = new();

    /// <summary>
    /// Sample the channel at <paramref name="time"/> (seconds), returning
    /// interpolated TRS components.
    /// </summary>
    public (Vector3 t, Quaternion r, Vector3 s) SampleAt(float time)
    {
        if (Keyframes.Count == 0)
            return (Vector3.Zero, Quaternion.Identity, Vector3.One);

        if (Keyframes.Count == 1)
        {
            var kf = Keyframes[0];
            return (kf.Translation, kf.Rotation, kf.Scale);
        }

        // Clamp to available range.
        if (time <= Keyframes[0].Time)
        {
            var kf = Keyframes[0];
            return (kf.Translation, kf.Rotation, kf.Scale);
        }
        if (time >= Keyframes[^1].Time)
        {
            var kf = Keyframes[^1];
            return (kf.Translation, kf.Rotation, kf.Scale);
        }

        // Binary-search for the surrounding keyframe pair.
        int lo = 0;
        int hi = Keyframes.Count - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Keyframes[mid + 1].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }

        var a = Keyframes[lo];
        var b = Keyframes[lo + 1];

        float span = b.Time - a.Time;
        float t    = span > 1e-6f ? (time - a.Time) / span : 0f;

        Vector3    translation = Vector3.Lerp(a.Translation, b.Translation, t);
        Quaternion rotation    = Quaternion.Slerp(a.Rotation, b.Rotation, t);
        Vector3    scale       = Vector3.Lerp(a.Scale, b.Scale, t);

        return (translation, rotation, scale);
    }
}

// ---------------------------------------------------------------------------
// Skeletal clip
// ---------------------------------------------------------------------------

/// <summary>
/// One named animation clip for a skeleton: a set of per-bone channels
/// covering a time range of [0, <see cref="Duration"/>] seconds.
/// </summary>
public class SkeletalClip
{
    public string            Name           { get; set; } = "";

    /// <summary>Clip length in seconds.</summary>
    public float             Duration       { get; set; }

    /// <summary>Used during import from Assimp to convert tick times to seconds.</summary>
    public float             TicksPerSecond { get; set; } = 24f;

    public List<BoneChannel> Channels       { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Skeletal animator component
// ---------------------------------------------------------------------------

/// <summary>
/// Plays <see cref="SkeletalClip"/>s on a rigged mesh.  Each frame it builds the
/// <see cref="BonePalette"/> — an array of final skin matrices (one per bone) that
/// the mesh renderer uploads to the GPU as a shader constant.
///
/// Supports looping playback, one-shot stop, and cross-fading between clips.
/// </summary>
public class SkeletalAnimator : Component
{
    // -------------------------------------------------------------------------
    // Data
    // -------------------------------------------------------------------------

    /// <summary>Flat bone list in hierarchy order (parent index is always &lt; child index).</summary>
    public List<Bone>         Skeleton    { get; set; } = new();

    /// <summary>All available animation clips for this skeleton.</summary>
    public List<SkeletalClip> Clips       { get; set; } = new();

    /// <summary>
    /// Output bone palette.  Element [i] = worldTransform[i] * bone[i].InvBindPose.
    /// Upload to the vertex shader as a Matrix[] uniform (e.g. "Bones").
    /// </summary>
    public Matrix[]           BonePalette { get; private set; } = Array.Empty<Matrix>();

    // -------------------------------------------------------------------------
    // Playback state
    // -------------------------------------------------------------------------

    private SkeletalClip? _currentClip;
    private float         _currentTime;
    private bool          _loop;
    private bool          _playing;

    // Cross-fade state
    private SkeletalClip? _blendFromClip;
    private float         _blendFromTime;
    private float         _blendElapsed;
    private float         _blendDuration;
    private bool          _isCrossFading;

    // Reusable scratch buffers (allocated once, reused each frame).
    private Matrix[] _localTransforms = Array.Empty<Matrix>();
    private Matrix[] _worldTransforms = Array.Empty<Matrix>();

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void Awake()
    {
        AllocateBuffers();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Begin playing the named clip from time 0.</summary>
    public void Play(string clipName, bool loop = true)
    {
        var clip = FindClip(clipName);
        if (clip == null) return;

        _currentClip  = clip;
        _currentTime  = 0f;
        _loop         = loop;
        _playing      = true;
        _isCrossFading = false;

        AllocateBuffers();
    }

    /// <summary>Stop playback and freeze on the current pose.</summary>
    public void Stop()
    {
        _playing       = false;
        _isCrossFading = false;
    }

    /// <summary>
    /// Smoothly transition to <paramref name="clipName"/> over <paramref name="blendTime"/> seconds.
    /// The current pose is blended into the new clip's pose during the transition.
    /// </summary>
    public void CrossFade(string clipName, float blendTime)
    {
        var clip = FindClip(clipName);
        if (clip == null) return;

        // Snapshot what we're blending FROM.
        _blendFromClip = _currentClip;
        _blendFromTime = _currentTime;

        // Switch the current clip to the target.
        _currentClip   = clip;
        _currentTime   = 0f;
        _loop          = true;
        _playing       = true;

        _blendDuration = Math.Max(blendTime, 1e-4f);
        _blendElapsed  = 0f;
        _isCrossFading = blendTime > 0f;

        AllocateBuffers();
    }

    // -------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------

    public override void Update(float dt)
    {
        if (!_playing || _currentClip == null) return;

        // Advance playback time.
        _currentTime += dt;

        if (_currentClip.Duration > 0f)
        {
            if (_loop)
                _currentTime = WrapTime(_currentTime, _currentClip.Duration);
            else
                _currentTime = Math.Min(_currentTime, _currentClip.Duration);
        }

        // Advance cross-fade blend.
        if (_isCrossFading)
        {
            _blendElapsed += dt;
            if (_blendElapsed >= _blendDuration)
                _isCrossFading = false;
        }

        // Build bone transforms.
        if (_isCrossFading && _blendFromClip != null)
            ComputeBlendedPalette(_blendFromClip, _blendFromTime, _currentClip, _currentTime,
                                  _blendElapsed / _blendDuration);
        else
            ComputePalette(_currentClip, _currentTime);
    }

    // -------------------------------------------------------------------------
    // Palette computation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sample <paramref name="clip"/> at <paramref name="time"/> and build
    /// the bone palette into <see cref="BonePalette"/>.
    /// </summary>
    private void ComputePalette(SkeletalClip clip, float time)
    {
        int count = Skeleton.Count;
        if (count == 0) return;

        // 1. Compute local transforms from clip channels.
        SampleClip(clip, time, _localTransforms);

        // 2. Concatenate local → world by walking the hierarchy (assumes
        //    parent index is always less than child index).
        for (int i = 0; i < count; i++)
        {
            var bone = Skeleton[i];
            _worldTransforms[i] = bone.ParentIndex < 0
                ? _localTransforms[i]
                : _localTransforms[i] * _worldTransforms[bone.ParentIndex];
        }

        // 3. Final skin matrix = worldTransform * invBindPose.
        for (int i = 0; i < count; i++)
            BonePalette[i] = Skeleton[i].InvBindPose * _worldTransforms[i];
    }

    /// <summary>
    /// Blend two clips together using linear interpolation at weight <paramref name="alpha"/>
    /// (0 = fully from, 1 = fully to) and build the bone palette.
    /// </summary>
    private void ComputeBlendedPalette(
        SkeletalClip fromClip, float fromTime,
        SkeletalClip toClip,   float toTime,
        float alpha)
    {
        int count = Skeleton.Count;
        if (count == 0) return;

        // Build local transforms for both clips, blend them, then compute world and palette.
        Span<Matrix> fromLocals = stackalloc Matrix[count];
        Span<Matrix> toLocals   = stackalloc Matrix[count];

        SampleClipSpan(fromClip, fromTime, fromLocals, count);
        SampleClipSpan(toClip,   toTime,   toLocals,   count);

        for (int i = 0; i < count; i++)
            _localTransforms[i] = BlendMatrices(fromLocals[i], toLocals[i], alpha);

        // World concatenation.
        for (int i = 0; i < count; i++)
        {
            var bone = Skeleton[i];
            _worldTransforms[i] = bone.ParentIndex < 0
                ? _localTransforms[i]
                : _localTransforms[i] * _worldTransforms[bone.ParentIndex];
        }

        // Skin matrices.
        for (int i = 0; i < count; i++)
            BonePalette[i] = Skeleton[i].InvBindPose * _worldTransforms[i];
    }

    // -------------------------------------------------------------------------
    // Sampling helpers
    // -------------------------------------------------------------------------

    private void SampleClip(SkeletalClip clip, float time, Matrix[] localTransforms)
    {
        int count = Skeleton.Count;

        // Initialise all bones to their bind pose (handles bones with no channel).
        for (int i = 0; i < count; i++)
            localTransforms[i] = Skeleton[i].BindPose;

        // Apply channel samples on top.
        foreach (var channel in clip.Channels)
        {
            int idx = channel.BoneIndex;
            if ((uint)idx >= (uint)count) continue;

            var (t, r, s) = channel.SampleAt(time);
            localTransforms[idx] =
                Matrix.CreateScale(s) *
                Matrix.CreateFromQuaternion(r) *
                Matrix.CreateTranslation(t);
        }
    }

    private void SampleClipSpan(SkeletalClip clip, float time, Span<Matrix> localTransforms, int count)
    {
        for (int i = 0; i < count; i++)
            localTransforms[i] = Skeleton[i].BindPose;

        foreach (var channel in clip.Channels)
        {
            int idx = channel.BoneIndex;
            if ((uint)idx >= (uint)count) continue;

            var (t, r, s) = channel.SampleAt(time);
            localTransforms[idx] =
                Matrix.CreateScale(s) *
                Matrix.CreateFromQuaternion(r) *
                Matrix.CreateTranslation(t);
        }
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    private static Matrix BlendMatrices(Matrix a, Matrix b, float t)
    {
        // Decompose → lerp/slerp → recompose for correct blend.
        DecomposeMatrix(a, out Vector3 ta, out Quaternion ra, out Vector3 sa);
        DecomposeMatrix(b, out Vector3 tb, out Quaternion rb, out Vector3 sb);

        Vector3    tr  = Vector3.Lerp(ta, tb, t);
        Quaternion rot = Quaternion.Slerp(ra, rb, t);
        Vector3    sc  = Vector3.Lerp(sa, sb, t);

        return Matrix.CreateScale(sc) * Matrix.CreateFromQuaternion(rot) * Matrix.CreateTranslation(tr);
    }

    private static void DecomposeMatrix(Matrix m, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        // MonoGame's Matrix.Decompose is available and handles this correctly.
        if (!m.Decompose(out scale, out rotation, out translation))
        {
            // Fallback if decomposition fails (degenerate matrix).
            translation = m.Translation;
            rotation    = Quaternion.Identity;
            scale       = Vector3.One;
        }
    }

    private static float WrapTime(float t, float duration)
        => duration > 0f ? t - MathF.Floor(t / duration) * duration : 0f;

    private SkeletalClip? FindClip(string name)
    {
        foreach (var c in Clips)
            if (string.Equals(c.Name, name, StringComparison.Ordinal))
                return c;
        return null;
    }

    private void AllocateBuffers()
    {
        int count = Skeleton.Count;
        if (BonePalette.Length == count &&
            _localTransforms.Length == count &&
            _worldTransforms.Length == count)
            return;

        BonePalette      = new Matrix[count];
        _localTransforms = new Matrix[count];
        _worldTransforms = new Matrix[count];

        // Initialise palette to identity so the mesh renders correctly before
        // the first Update tick.
        for (int i = 0; i < count; i++)
            BonePalette[i] = Matrix.Identity;
    }
}
