using System.Text.Json;
using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Chibi;

/// <summary>
/// A pose: a rotation per joint in euler degrees, plus a displacement of the whole
/// character.
/// </summary>
/// <remarks>
/// Joints left out of a pose stay at rest, which is what lets a wave animate one arm and
/// leave the legs alone.
/// </remarks>
public sealed class ChibiPose
{
    /// <summary>Joint name to its euler rotation in degrees.</summary>
    public Dictionary<string, Vector3> Joints { get; } = new(StringComparer.Ordinal);

    /// <summary>How far the whole character is displaced, for a bob or a jump.</summary>
    public Vector3 Offset { get; set; }

    /// <summary>Sets one joint's rotation.</summary>
    public ChibiPose Set(string joint, float x, float y, float z)
    {
        Joints[joint] = new Vector3(x, y, z);
        return this;
    }

    /// <summary>Empties the pose, returning every joint to rest.</summary>
    public ChibiPose Clear()
    {
        Joints.Clear();
        Offset = Vector3.Zero;
        return this;
    }

    /// <summary>One joint's rotation, or zero when the clip did not set it.</summary>
    public Vector3 Get(string joint)
        => Joints.TryGetValue(joint, out Vector3 rotation) ? rotation : Vector3.Zero;

    /// <summary>Blends two poses into a third, covering every joint either of them names.</summary>
    public static ChibiPose Blend(ChibiPose into, ChibiPose from, ChibiPose to, float amount)
    {
        into.Clear();
        foreach (string name in from.Joints.Keys.Concat(to.Joints.Keys).Distinct())
            into.Joints[name] = Vector3.Lerp(from.Get(name), to.Get(name), amount);

        into.Offset = Vector3.Lerp(from.Offset, to.Offset, amount);
        return into;
    }
}

/// <summary>One clip, whether it came from code or from the shared file.</summary>
/// <param name="Name">The name a game plays it by.</param>
/// <param name="Duration">Seconds at speed 1.</param>
/// <param name="Loop">Whether it repeats rather than ending.</param>
/// <param name="Hold">Whether it keeps its last pose instead of returning to rest.</param>
/// <param name="Sample">Writes the pose at normalised time into the given pose.</param>
public sealed record ChibiClip(
    string Name, float Duration, bool Loop, bool Hold,
    Action<float, ChibiPose, float> Sample);

/// <summary>
/// The motion, in two halves.
/// </summary>
/// <remarks>
/// <para>
/// Locomotion is procedural: idle, walk and run are functions of phase, so a walk cycle can
/// scale with how fast the actor is actually moving and a game needs no files to have a
/// character that is alive. Everything expressive is keyed, out of
/// <c>html5/src/chibi/chibi-clips.json</c>, because a wave is a performance and a sine wave
/// is not.
/// </para>
/// <para>Mirrored in <c>html5/src/chibi/ChibiClips.js</c>.</para>
/// </remarks>
public static class ChibiClips
{
    private const float Tau = MathF.PI * 2f;

    /// <summary>Arms hang a little away from the body; dead-straight arms read as a mannequin.</summary>
    private const float ArmRest = 7f;

    /// <summary>And the elbows keep a slight bend for the same reason.</summary>
    private const float ElbowRest = -12f;

    private static readonly Dictionary<string, ChibiClip> _keyed = LoadKeyed();

    private static readonly Dictionary<string, ChibiClip> _procedural = new(StringComparer.Ordinal)
    {
        ["idle"] = new("idle", 3.2f, true, false, Idle),
        ["walk"] = new("walk", 0.86f, true, false,
            (t, pose, k) => Stride(t, pose, 30f * k, 34f * k, 3f * k, 0.018f * k, ElbowRest)),
        ["run"] = new("run", 0.56f, true, false,
            (t, pose, k) => Stride(t, pose, 48f * k, 62f * k, 14f * k, 0.032f * k, -58f)),
    };

    /// <summary>Every clip name a game may play, procedural and keyed together.</summary>
    public static IReadOnlyList<string> Names { get; } =
        _procedural.Keys.Concat(_keyed.Keys).ToArray();

    /// <summary>Looks a clip up by name. Null for one that does not exist.</summary>
    public static ChibiClip? Find(string name)
        => _procedural.TryGetValue(name, out var procedural) ? procedural
            : _keyed.GetValueOrDefault(name);

    // -------------------------------------------------------------------------
    // Procedural locomotion
    // -------------------------------------------------------------------------

    private static void Idle(float t, ChibiPose pose, float intensity)
    {
        float b = MathF.Sin(t * Tau) * intensity;

        pose.Set("Torso", 1.5f + b * 1.6f, 0, 0);
        pose.Set("Head", -b * 2f, MathF.Sin(t * Tau * 0.5f) * 4f, 0);
        pose.Set("ArmL", b * 1.5f, 0, ArmRest + b);
        pose.Set("ArmR", b * 1.5f, 0, -ArmRest - b);
        pose.Set("ForearmL", ElbowRest, 0, 0);
        pose.Set("ForearmR", ElbowRest, 0, 0);
        pose.Offset = new Vector3(0, b * 0.006f, 0);
    }

    private static void Stride(float t, ChibiPose pose,
                               float swing, float knee, float lean, float bob, float elbow)
    {
        float s = MathF.Sin(t * Tau);
        float c = MathF.Cos(t * Tau);

        float thighL = s * swing;
        float thighR = -s * swing;
        // The knee only folds on the back half of the stride; a leg that bends going
        // forward is the single thing that makes a walk cycle look wrong.
        float shinL = -MathF.Max(0, -s) * knee;
        float shinR = -MathF.Max(0, s) * knee;

        pose.Set("ThighL", thighL, 0, 0);
        pose.Set("ThighR", thighR, 0, 0);
        pose.Set("ShinL", shinL, 0, 0);
        pose.Set("ShinR", shinR, 0, 0);
        // Feet stay roughly flat by undoing half of what the leg above them did.
        pose.Set("FootL", -(thighL + shinL) * 0.5f, 0, 0);
        pose.Set("FootR", -(thighR + shinR) * 0.5f, 0, 0);

        pose.Set("ArmL", -s * swing * 0.85f, 0, ArmRest);
        pose.Set("ArmR", s * swing * 0.85f, 0, -ArmRest);
        pose.Set("ForearmL", elbow, 0, 0);
        pose.Set("ForearmR", elbow, 0, 0);

        pose.Set("Hips", 0, -s * 6f, 0);
        pose.Set("Torso", lean, s * 5f, 0);
        pose.Set("Head", -lean * 0.5f, -s * 3f, 0);

        // Two bobs per stride: one for each foot that lands.
        pose.Offset = new Vector3(0, MathF.Abs(c) * bob, 0);
    }

    // -------------------------------------------------------------------------
    // Keyed clips
    // -------------------------------------------------------------------------

    private readonly record struct Key(float Time, Vector3 Value);

    private static Vector3 SampleTrack(Key[] keys, float t)
    {
        if (keys.Length == 0) return Vector3.Zero;
        if (t <= keys[0].Time) return keys[0].Value;

        for (int i = 1; i < keys.Length; i++)
        {
            if (t > keys[i].Time) continue;
            Key a = keys[i - 1];
            Key b = keys[i];
            float span = b.Time - a.Time;
            return Vector3.Lerp(a.Value, b.Value, span <= 0 ? 0 : (t - a.Time) / span);
        }
        return keys[^1].Value;
    }

    private static Dictionary<string, ChibiClip> LoadKeyed()
    {
        var clips = new Dictionary<string, ChibiClip>(StringComparer.Ordinal);
        try
        {
            using Stream? stream = typeof(ChibiClips).Assembly
                .GetManifestResourceStream("SexyBiscuit.Engine.chibi-clips.json");
            if (stream == null) return clips;

            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("clips", out JsonElement all)) return clips;

            foreach (JsonProperty entry in all.EnumerateObject())
            {
                JsonElement clip = entry.Value;
                var tracks = new Dictionary<string, Key[]>(StringComparer.Ordinal);

                if (clip.TryGetProperty("tracks", out JsonElement trackList))
                {
                    foreach (JsonProperty track in trackList.EnumerateObject())
                        tracks[track.Name] = ReadKeys(track.Value, "rot");
                }

                Key[] offset = clip.TryGetProperty("offset", out JsonElement o)
                    ? ReadKeys(o, "pos") : Array.Empty<Key>();

                clips[entry.Name] = new ChibiClip(
                    entry.Name,
                    clip.TryGetProperty("duration", out var d) ? (float)d.GetDouble() : 1f,
                    clip.TryGetProperty("loop", out var l) && l.GetBoolean(),
                    clip.TryGetProperty("hold", out var h) && h.GetBoolean(),
                    (t, pose, _) =>
                    {
                        foreach (var (joint, keys) in tracks) pose.Joints[joint] = SampleTrack(keys, t);
                        if (offset.Length > 0) pose.Offset = SampleTrack(offset, t);
                    });
            }
        }
        catch (Exception ex)
        {
            // Missing clips must not take the game down: locomotion still works, and the
            // reason is on the console rather than nowhere.
            Console.Error.WriteLine($"[ChibiClips] could not load the shared clips: {ex.Message}");
        }
        return clips;
    }

    private static Key[] ReadKeys(JsonElement array, string field)
    {
        var keys = new List<Key>();
        foreach (JsonElement key in array.EnumerateArray())
        {
            if (!key.TryGetProperty(field, out JsonElement value)) continue;
            keys.Add(new Key(
                (float)key.GetProperty("t").GetDouble(),
                new Vector3((float)value[0].GetDouble(), (float)value[1].GetDouble(),
                            (float)value[2].GetDouble())));
        }
        return keys.ToArray();
    }
}
