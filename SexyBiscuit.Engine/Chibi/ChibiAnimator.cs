using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Chibi;

/// <summary>
/// Plays procedural and keyed chibi clips, cross-fading between them.
/// </summary>
/// <remarks>
/// <para>
/// The joints are actors, so animation is nothing more exotic than writing
/// <see cref="Transform3D.LocalEulerAngles"/> on sixteen transforms a frame. Those
/// transforms are looked up once and kept: <see cref="Actor.Transform3D"/> is a component
/// search on every access, which is cheap once and is not cheap sixteen times a frame per
/// character.
/// </para>
/// <para>
/// This deliberately has nothing to do with <see cref="Animation.SkeletalAnimator"/>. That
/// drives a bone palette for a skinning shader, has no browser counterpart, and cannot be
/// saved in a scene file — going near it would cost MakeChibi its second engine.
/// </para>
/// <para>Mirrored in <c>html5/src/chibi/ChibiAnimator.js</c>.</para>
/// </remarks>
public sealed class ChibiAnimator : Component
{
    /// <summary>One clip in flight: which clip, how far through, and whether it is done.</summary>
    private sealed class Playhead
    {
        public Playhead(ChibiClip clip) => Clip = clip;

        public ChibiClip Clip { get; }
        public float Time { get; private set; }
        public bool Finished { get; private set; }

        public void Advance(float dt, float speed)
        {
            Time += dt * speed;
            float duration = MathF.Max(1e-4f, Clip.Duration);
            if (Time < duration) return;

            if (Clip.Loop) Time %= duration;
            else { Time = duration; Finished = true; }
        }

        /// <summary>Normalised position through the clip, 0 to 1.</summary>
        public float Phase => MathF.Min(1f, Time / MathF.Max(1e-4f, Clip.Duration));
    }

    /// <summary>The clip to play. An unknown name leaves the character at rest.</summary>
    public string Clip { get; set; } = "idle";

    /// <summary>Playback rate.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Scales a procedural clip's swing; keyed clips ignore it.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>Whether a clip is running.</summary>
    public bool Playing { get; set; } = true;

    /// <summary>Seconds to cross-fade when the clip changes.</summary>
    public float BlendTime { get; set; } = 0.18f;

    /// <summary>Fires with the clip's name when a non-looping clip reaches its end.</summary>
    public event Action<string>? ClipFinished;

    private readonly Dictionary<string, Transform3D> _joints = new(StringComparer.Ordinal);
    private Transform3D? _root;
    private Vector3 _rootRest;

    private Playhead? _current;
    private Playhead? _previous;
    private float _blend;
    private float _blendLeft;

    private readonly ChibiPose _poseA = new();
    private readonly ChibiPose _poseB = new();
    private readonly ChibiPose _poseOut = new();
    private bool _resolved;

    /// <summary>Every clip name this animator will accept.</summary>
    public static IReadOnlyList<string> Clips => ChibiClips.Names;

    /// <inheritdoc />
    public override void Start()
    {
        Resolve();
        if (Playing) Play(Clip, 0f);
    }

    /// <summary>
    /// Finds the joints to drive. Called automatically, and again by anything that rebuilds
    /// the body underneath — the joints it cached are destroyed actors.
    /// </summary>
    public bool Resolve()
    {
        _joints.Clear();
        _root = null;

        var character = GetComponent<ChibiCharacter>();
        if (character?.Chibi != null)
        {
            foreach (var (name, transform) in character.Chibi.Joints) _joints[name] = transform;
            _root = character.Chibi.Actor.GetComponent<Transform3D>();
        }
        else if (Actor != null)
        {
            // No component to ask: find the joints by name in whatever subtree is there,
            // so a baked-out chibi still animates.
            foreach (Actor actor in Actor.Descendants())
            {
                if (!ChibiParts.JointNames.Contains(actor.Name) || _joints.ContainsKey(actor.Name)) continue;
                if (actor.GetComponent<Transform3D>() is { } transform) _joints[actor.Name] = transform;
            }
        }

        if (_root != null) _rootRest = _root.LocalPosition;
        _resolved = _joints.Count > 0;
        return _resolved;
    }

    /// <summary>Starts a clip, cross-fading from whatever was playing.</summary>
    /// <param name="name">A procedural or keyed clip name.</param>
    /// <param name="blend">Seconds to fade over, or null for <see cref="BlendTime"/>.</param>
    public bool Play(string name, float? blend = null)
    {
        ChibiClip? clip = ChibiClips.Find(name);
        if (clip == null) return false;
        if (_current?.Clip.Name == name && Playing) return true;

        _previous = _current;
        _current = new Playhead(clip);
        _blend = _previous != null ? MathF.Max(0f, blend ?? BlendTime) : 0f;
        _blendLeft = _blend;
        Clip = name;
        Playing = true;
        return true;
    }

    /// <summary>Stops, returning every joint to rest.</summary>
    public void Stop()
    {
        Playing = false;
        _current = null;
        _previous = null;
        ApplyPose(_poseOut.Clear());
    }

    /// <inheritdoc />
    public override void Update(float dt)
    {
        if (!_resolved && !Resolve()) return;
        if (!Playing || _current == null) return;

        _current.Advance(dt, Speed);

        _poseB.Clear();
        _current.Clip.Sample(_current.Phase, _poseB, Intensity);
        ChibiPose pose = _poseB;

        if (_previous != null && _blend > 0f)
        {
            _blendLeft = MathF.Max(0f, _blendLeft - dt);
            float amount = 1f - _blendLeft / _blend;

            _poseA.Clear();
            _previous.Clip.Sample(_previous.Phase, _poseA, Intensity);
            pose = ChibiPose.Blend(_poseOut, _poseA, _poseB, amount);

            if (_blendLeft <= 0f) _previous = null;
        }

        ApplyPose(pose);

        if (!_current.Finished) return;

        ChibiClip finished = _current.Clip;
        if (!finished.Hold) _current = null;
        Playing = finished.Hold;
        ClipFinished?.Invoke(finished.Name);
    }

    /// <summary>
    /// Writes a pose onto the joints.
    /// </summary>
    /// <remarks>
    /// Joints the clip did not mention go back to rest rather than keeping whatever the
    /// last clip left there — a wave must not leave the legs mid-stride.
    /// </remarks>
    public void ApplyPose(ChibiPose pose)
    {
        foreach (string name in ChibiParts.JointNames)
        {
            if (_joints.TryGetValue(name, out Transform3D? transform))
                transform.LocalEulerAngles = pose.Get(name);
        }

        if (_root != null) _root.LocalPosition = _rootRest + pose.Offset;
    }
}
