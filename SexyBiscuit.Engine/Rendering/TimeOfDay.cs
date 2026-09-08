using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>Where the sun is, in the only terms a game usually cares about.</summary>
public enum DayPhase { Night, Dawn, Day, Dusk }

/// <summary>
/// An in-game clock that drives the sun, the sky and the ambient light.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Component"/> rather than a subsystem, and deliberately: the scene
/// serialiser writes components and nothing else, <c>WorldSubsystem</c> is declared but
/// never instantiated anywhere, and a clock that could not be authored into a level and
/// saved would be a clock every game had to wire up in script.
/// </para>
/// <para>
/// This is the thing <c>CookieJar/day-night-cycle</c> says it is not. That cookie's own
/// notes are explicit — "No lighting. This is a flat tint over everything, not a light
/// model… Real 2D lighting is engine work" — because a script has no viewport and cannot
/// reach a light. From inside the engine it can: this moves a real directional light,
/// repaints the sky gradient, and sets the hemisphere ambient and the 2D darkness floor,
/// so dusk is graded rather than a navy square following the player about.
/// </para>
/// <para>
/// Time runs 0 to 1 with 0 at midnight and 0.5 at noon, which is what makes
/// <see cref="Hour"/> and a sun elevation fall out of it rather than being invented.
/// </para>
/// </remarks>
public sealed class TimeOfDay : Component
{
    // -------------------------------------------------------------------------
    // The one that drives the scene
    // -------------------------------------------------------------------------

    private static TimeOfDay? _active;

    /// <summary>
    /// The clock currently driving the scene, or null.
    /// </summary>
    /// <remarks>
    /// Last one to wake wins, which is the opposite of <see cref="SkyLight"/>'s
    /// first-wins and matches what the browser engine does for both. A level that
    /// additively loads a lit interior wants that interior's clock, not the one the
    /// outdoor scene registered first.
    /// </remarks>
    public static TimeOfDay? Active => _active;

    /// <summary>Forgets the active clock. A scene not destroyed keeps driving the next one.</summary>
    internal static void ClearActive() => _active = null;

    // -------------------------------------------------------------------------
    // Serialised state
    // -------------------------------------------------------------------------

    /// <summary>Real seconds in one in-game day. Zero or less stops the clock.</summary>
    public float DayLength { get; set; } = 150f;

    /// <summary>Where the first day starts, 0 to 1. The default opens at dawn.</summary>
    public float StartAt { get; set; } = 0.26f;

    /// <summary>Whether the clock is stopped where it stands.</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Degrees the sun's arc is tilted off vertical, which is what gives a season.
    /// </summary>
    /// <remarks>
    /// Zero puts the sun straight overhead at noon, which reads as the tropics and makes
    /// for very short shadows. Around 35 is the familiar temperate arc.
    /// </remarks>
    public float SunTilt { get; set; } = 35f;

    /// <summary>Compass direction the sun rises from, in degrees.</summary>
    public float SunAzimuth { get; set; } = 90f;

    /// <summary>Whether to move and colour the scene's directional light.</summary>
    public bool DriveSun { get; set; } = true;

    /// <summary>Whether to repaint <see cref="Skybox"/> and the hemisphere ambient.</summary>
    public bool DriveSky { get; set; } = true;

    /// <summary>Whether to set the 2D light map's ambient floor, so a 2D game darkens too.</summary>
    public bool Drive2D { get; set; } = true;

    // -------------------------------------------------------------------------
    // Live state
    // -------------------------------------------------------------------------

    /// <summary>The moment of the current day, 0 at midnight and 0.5 at noon.</summary>
    public float Time01
    {
        get => _time01;
        set
        {
            float wrapped = SkyGradient.Wrap01(value);

            // A clock set backwards past midnight has gone back a day, and a game that
            // counts days off this must not see day 4 turn into day 5 on a rewind.
            if (wrapped < _time01 - 0.5f) DayNumber++;
            else if (wrapped > _time01 + 0.5f) DayNumber = Math.Max(1, DayNumber - 1);

            _time01 = wrapped;
        }
    }

    /// <summary>Days since the game began, counting from one.</summary>
    public int DayNumber { get; set; } = 1;

    /// <summary>The hour, 0 to 24.</summary>
    public float Hour => Time01 * 24f;

    /// <summary>The clock as a game would print it, such as "06:42".</summary>
    /// <remarks>
    /// Rounded to whole minutes first, rather than flooring the hour and then the
    /// minute. 0.3 of a day is exactly 07:12, but it is not exactly representable:
    /// this engine's float landed a hair above the boundary and the browser's double
    /// a hair below, so the same moment printed 07:12 here and 07:11 there. A minute
    /// is the smallest thing this returns, so it is the thing to round to.
    /// </remarks>
    public string Clock
    {
        get
        {
            int minutes = (int)MathF.Round(Time01 * 1440f) % 1440;
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }
    }

    /// <summary>
    /// The sun's height above the horizon, in degrees. Negative is below it.
    /// </summary>
    /// <remarks>
    /// The real quantity behind every other answer here. Phases are read off this rather
    /// than off magic fractions of a day, so changing <see cref="SunTilt"/> moves dawn
    /// and dusk to where the light actually changes instead of leaving them behind.
    /// </remarks>
    public float SunElevation
    {
        get
        {
            // Peaks at noon, bottoms at midnight, scaled by the tilt off vertical.
            float angle = (Time01 - 0.25f) * MathHelper.TwoPi;
            return MathHelper.ToDegrees(MathF.Asin(Math.Clamp(
                MathF.Sin(angle) * MathF.Cos(MathHelper.ToRadians(SunTilt) * 0.5f), -1f, 1f)));
        }
    }

    /// <summary>Which way the sun is shining, pointing from the sun towards the ground.</summary>
    public Vector3 SunDirection
    {
        get
        {
            float elevation = MathHelper.ToRadians(SunElevation);
            float azimuth   = MathHelper.ToRadians(SunAzimuth + Time01 * 360f);

            // Negated because a light's direction is where its light goes, not where the
            // sun is: at noon the sun is overhead and the light travels downwards.
            return -Vector3.Normalize(new Vector3(
                MathF.Cos(elevation) * MathF.Cos(azimuth),
                MathF.Sin(elevation),
                MathF.Cos(elevation) * MathF.Sin(azimuth)));
        }
    }

    /// <summary>Twilight band, in degrees either side of the horizon.</summary>
    public const float TwilightDegrees = 6f;

    /// <summary>Where the sun is, in the terms a game usually cares about.</summary>
    public DayPhase Phase
    {
        get
        {
            float elevation = SunElevation;
            if (elevation > TwilightDegrees) return DayPhase.Day;
            if (elevation < -TwilightDegrees) return DayPhase.Night;

            // Inside the twilight band, which one depends on whether the sun is rising.
            return Time01 < 0.5f ? DayPhase.Dawn : DayPhase.Dusk;
        }
    }

    /// <summary>
    /// How dark it is, 0 in full day and 1 at the deepest night.
    /// </summary>
    /// <remarks>
    /// The curve a game reads to make night mean something — more enemies, worse
    /// accuracy, better stealth — and named to match the cookie's <c>getDarkness</c> so
    /// a script that already used one reads the same against the other.
    /// </remarks>
    public float Darkness
    {
        get
        {
            float elevation = SunElevation;
            if (elevation >= TwilightDegrees) return 0f;
            if (elevation <= -TwilightDegrees) return 1f;
            return 1f - (elevation + TwilightDegrees) / (TwilightDegrees * 2f);
        }
    }

    /// <summary>Whether the sun is below the horizon.</summary>
    public bool IsNight => SunElevation < 0f;

    /// <summary>The sky's colours at this moment.</summary>
    public SkySample Sky => SkyGradient.Shared.Sample(Time01);

    // -------------------------------------------------------------------------
    // Seams
    // -------------------------------------------------------------------------

    /// <summary>Raised once each time the phase changes, with what it was and what it is.</summary>
    public Action<DayPhase, DayPhase>? PhaseChanged { get; set; }

    /// <summary>Raised once each time the clock passes midnight, with the new day number.</summary>
    public Action<int>? NewDay { get; set; }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private float     _time01;
    private DayPhase  _lastPhase;

    public override void Awake()
    {
        base.Awake();
        _time01    = SkyGradient.Wrap01(StartAt);
        _lastPhase = Phase;
        _active    = this;
    }

    public override void OnDestroy()
    {
        // Without this a destroyed clock keeps driving the renderer forever, which is
        // the leak wiki/02-core-architecture.md warns about for every static registry.
        if (ReferenceEquals(_active, this)) _active = null;
        base.OnDestroy();
    }

    public override void Update(float deltaTime)
    {
        Advance(deltaTime);
        Apply();
    }

    /// <summary>
    /// Moves the clock on and raises whatever that crossed.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Apply"/> so a test can run a whole day in one call with
    /// no renderer, no scene and no window anywhere.
    /// </remarks>
    public void Advance(float deltaSeconds)
    {
        if (Paused || DayLength <= 0f || deltaSeconds <= 0f) return;

        float advanced = _time01 + deltaSeconds / DayLength;

        // A frame long enough to cross more than one midnight still counts every day it
        // crossed, so a game left paused at a breakpoint does not lose a week.
        int days = (int)MathF.Floor(advanced);
        _time01 = advanced - days;

        for (int i = 0; i < days; i++)
        {
            DayNumber++;
            NewDay?.Invoke(DayNumber);
        }

        DayPhase phase = Phase;
        if (phase == _lastPhase) return;

        DayPhase was = _lastPhase;
        _lastPhase = phase;
        PhaseChanged?.Invoke(was, phase);
    }

    /// <summary>
    /// Pushes this moment onto the scene's lights, sky and ambient.
    /// </summary>
    /// <remarks>
    /// Everything it touches already existed and was simply never driven by anything:
    /// the sun is an ordinary <see cref="Light3D"/>, the sky is an ordinary
    /// <see cref="Skybox"/>, and the C# renderer has bound <c>SkyColor</c> and
    /// <c>GroundColor</c> shader parameters all along that no shipped effect reads yet.
    /// </remarks>
    public void Apply()
    {
        SkySample sky = Sky;

        if (DriveSun && FindSun() is { } sun)
        {
            sun.Color     = sky.Sun;
            sun.Intensity = sky.SunIntensity;

            // Light3D.GetDirection returns its transform's Forward, which is derived from
            // the rotation and cannot be assigned, so the actor is turned to face the
            // direction rather than having a direction written onto it.
            if (sun.Actor?.GetComponent<Transform3D>() is { } transform)
                transform.Rotation = LookRotation(SunDirection);
        }

        if (DriveSky)
        {
            if (SkyLight.Active is { } ambient)
            {
                ambient.SkyColor    = sky.AmbientSky;
                ambient.GroundColor = sky.AmbientGround;
            }

            foreach (Skybox box in Skyboxes())
            {
                box.GradientTop    = sky.Zenith;
                box.GradientBottom = sky.Horizon;
            }
        }

        if (Drive2D && Lighting2DAmbient is { } lighting) lighting.AmbientColor = sky.AmbientGround;
    }

    /// <summary>The 2D light map this clock darkens, if the game has one.</summary>
    public Lighting2D? Lighting2DAmbient { get; set; }

    private Light3D? FindSun()
    {
        foreach (Light3D light in Light3D.All)
            if (light.Enabled && light.Type == LightType.Directional) return light;
        return null;
    }

    private IEnumerable<Skybox> Skyboxes()
    {
        Core.Scene? scene = Actor?.Scene;
        if (scene is null) yield break;

        foreach (Core.Layer layer in scene.Layers)
            foreach (Actor actor in layer.Actors)
                if (actor.GetComponent<Skybox>() is { } box) yield return box;
    }

    /// <summary>
    /// A rotation whose forward is the given direction.
    /// </summary>
    /// <remarks>
    /// World up as the reference, except when the sun is directly overhead or underfoot,
    /// where up and forward are parallel and the cross product collapses to nothing —
    /// which would leave the light pointing wherever it last happened to be, once a day,
    /// for one frame.
    /// </remarks>
    private static Quaternion LookRotation(Vector3 forward)
    {
        forward = Vector3.Normalize(forward);

        Vector3 reference = MathF.Abs(Vector3.Dot(forward, Vector3.Up)) > 0.999f
            ? Vector3.Forward
            : Vector3.Up;

        Vector3 right = Vector3.Normalize(Vector3.Cross(reference, forward));
        Vector3 up    = Vector3.Cross(forward, right);

        return Quaternion.CreateFromRotationMatrix(new Matrix(
            right.X,   right.Y,   right.Z,   0f,
            up.X,      up.Y,      up.Z,      0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f,        0f,        0f,        1f));
    }
}
