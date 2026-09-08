using System.Text.Json;
using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Everything the sky, the sun and the ambient light look like at one moment.
/// </summary>
public readonly record struct SkySample(
    Color Zenith,
    Color Horizon,
    Color Sun,
    float SunIntensity,
    Color AmbientSky,
    Color AmbientGround,
    Color Fog,
    float FogDensity);

/// <summary>
/// The colour of a day, interpolated from the table both engines read.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <c>html5/src/rendering/SkyGradient.js</c>. A day/night cycle is almost
/// entirely colour, and colour written twice is two different dusks — the desktop build
/// warm and the web build blue, with nothing able to see it. So the keys live in one JSON
/// file, embedded here the same way the presets and the glyph table are, and a shared
/// fixture pins what both engines make of them.
/// </para>
/// <para>
/// Time runs 0 to 1 with 0 at midnight and 0.5 at noon, and the table wraps: the value at
/// 0.99 blends towards the key at 0, not off the end of the array.
/// </para>
/// </remarks>
public sealed class SkyGradient
{
    /// <summary>The table every caller uses. Parsed once.</summary>
    public static SkyGradient Shared { get; } = Load();

    private readonly List<Key> _keys = new();

    /// <summary>How many keys the table holds.</summary>
    public int KeyCount => _keys.Count;

    /// <summary>The name of each key, in order, for an editor's timeline.</summary>
    public IEnumerable<string> KeyNames => _keys.Select(k => k.Name);

    /// <summary>
    /// The sky at a moment. Any time is accepted; it is wrapped into one day first.
    /// </summary>
    public SkySample Sample(float time01)
    {
        if (_keys.Count == 0) return default;

        float t = Wrap01(time01);

        // The last key before t, and the one after it. The table wraps, so "after the
        // last key" is the first key again — which is what makes 23:59 blend into
        // midnight rather than snapping.
        int before = 0;
        for (int i = 0; i < _keys.Count; i++)
        {
            if (_keys[i].Time > t) break;
            before = i;
        }

        int after = Math.Min(before + 1, _keys.Count - 1);
        Key a = _keys[before], b = _keys[after];

        // In double, because the browser engine has no float: doing the blend in single
        // precision here and in double there puts the two a whole byte apart wherever a
        // channel lands near a rounding boundary.
        double span = (double)b.Time - a.Time;
        double f = span <= 0d ? 0d : Math.Clamp(((double)t - a.Time) / span, 0d, 1d);

        return new SkySample(
            Zenith:        Mix(a.Zenith, b.Zenith, f),
            Horizon:       Mix(a.Horizon, b.Horizon, f),
            Sun:           Mix(a.Sun, b.Sun, f),
            SunIntensity:  (float)(a.SunIntensity + (b.SunIntensity - a.SunIntensity) * f),
            AmbientSky:    Mix(a.AmbientSky, b.AmbientSky, f),
            AmbientGround: Mix(a.AmbientGround, b.AmbientGround, f),
            Fog:           Mix(a.Fog, b.Fog, f),
            FogDensity:    (float)(a.FogDensity + (b.FogDensity - a.FogDensity) * f));
    }

    /// <summary>
    /// Blends two colours per channel.
    /// </summary>
    /// <remarks>
    /// The rounding rule is stated rather than borrowed, and the browser engine states
    /// the same one: floor(x + 0.5), computed in doubles. MonoGame's
    /// <c>Color.Lerp</c> truncates a single-precision lerp, JavaScript's
    /// <c>Math.round</c> rounds half away from zero and <c>MathF.Round</c> rounds half
    /// to even — three different answers for the same blend, each off by a byte at some
    /// fractions and correct at most.
    /// </remarks>
    private static Color Mix(Color a, Color b, double f)
    {
        double amount = Math.Clamp(f, 0d, 1d);

        static byte Channel(byte x, byte y, double amount)
            => (byte)Math.Clamp(Math.Floor(x + (y - x) * amount + 0.5), 0, 255);

        return new Color(Channel(a.R, b.R, amount),
                         Channel(a.G, b.G, amount),
                         Channel(a.B, b.B, amount),
                         Channel(a.A, b.A, amount));
    }

    /// <summary>Folds any time into 0..1, so a clock that has run for days still works.</summary>
    public static float Wrap01(float t)
    {
        float wrapped = t % 1f;
        return wrapped < 0f ? wrapped + 1f : wrapped;
    }

    // -------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------

    private readonly record struct Key(float Time, string Name, Color Zenith, Color Horizon,
                                       Color Sun, float SunIntensity, Color AmbientSky,
                                       Color AmbientGround, Color Fog, float FogDensity);

    private static SkyGradient Load()
    {
        var gradient = new SkyGradient();

        using Stream? stream = typeof(SkyGradient).Assembly
            .GetManifestResourceStream("SexyBiscuit.Engine.sky-gradient.json");

        // An empty table samples to black, which is wrong but is not a crash: a broken
        // build should render something, not refuse to start.
        if (stream is null) return gradient;

        using var document = JsonDocument.Parse(stream);
        foreach (JsonElement key in document.RootElement.GetProperty("keys").EnumerateArray())
        {
            gradient._keys.Add(new Key(
                Time:          key.GetProperty("time").GetSingle(),
                Name:          key.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                Zenith:        Hex(key, "zenith"),
                Horizon:       Hex(key, "horizon"),
                Sun:           Hex(key, "sun"),
                SunIntensity:  key.GetProperty("sunIntensity").GetSingle(),
                AmbientSky:    Hex(key, "ambientSky"),
                AmbientGround: Hex(key, "ambientGround"),
                Fog:           Hex(key, "fog"),
                FogDensity:    key.GetProperty("fogDensity").GetSingle()));
        }

        return gradient;
    }

    /// <summary>Through the document codec, so one hex parser serves the whole engine.</summary>
    private static Color Hex(JsonElement key, string name)
        => UI.UiDocument.ParseColour(key.GetProperty(name).GetString()) ?? Color.Black;
}
