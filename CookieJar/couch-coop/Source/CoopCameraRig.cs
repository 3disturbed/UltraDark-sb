using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;

namespace Cookies.CouchCoop;

/// <summary>
/// One camera that keeps every living player in frame, pulling back as they spread out. The
/// shared-screen model: Overcooked and Castle Crashers work this way.
/// </summary>
/// <remarks>
/// Split-screen would need a viewport rectangle per camera and a render pass per view, neither of
/// which the engine has. This needs nothing: it moves one camera.
/// </remarks>
public sealed class CoopCameraRig : Component
{
    /// <summary>How far behind and above the group the camera sits at the closest zoom.</summary>
    public Vector3 Offset { get; set; } = new(0f, 6f, 10f);

    /// <summary>Never closer than this, so a lone player is not inside the wall.</summary>
    public float MinDistance { get; set; } = 8f;

    /// <summary>Never further than this, so four players spread out are still visible.</summary>
    public float MaxDistance { get; set; } = 40f;

    /// <summary>Slack around the group, in world units, so nobody sits on the screen edge.</summary>
    public float Padding { get; set; } = 4f;

    /// <summary>How quickly the camera catches up, per second. Higher is snappier.</summary>
    public float Smoothing { get; set; } = 6f;

    /// <summary>Looks this far above the group's centre, roughly head height.</summary>
    public float LookHeight { get; set; } = 1.2f;

    /// <summary>Follows this instead of the players, when a cutscene wants the camera.</summary>
    public Transform3D? OverrideTarget { get; set; }

    private Transform3D _transform = null!;

    public override void Start()
        => _transform = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void LateUpdate(float dt)
    {
        if (!TryGetFocus(out var centre, out float spread)) return;

        // Pull back with the group: the offset's own length is the closest the camera ever sits.
        float distance = Math.Clamp(Offset.Length() + spread + Padding, MinDistance, MaxDistance);
        var   direction = Offset.LengthSquared() > 0f ? Vector3.Normalize(Offset) : new Vector3(0f, 0.5f, 1f);

        var wanted = centre + direction * distance;
        float t    = 1f - MathF.Exp(-Smoothing * dt);   // frame-rate independent, unlike a raw lerp

        _transform.Position = Vector3.Lerp(_transform.Position, wanted, t);
        _transform.LookAt(centre + new Vector3(0f, LookHeight, 0f));
    }

    /// <summary>The centre of the living players, and how far the furthest is from it.</summary>
    private bool TryGetFocus(out Vector3 centre, out float spread)
    {
        centre = Vector3.Zero;
        spread = 0f;

        if (OverrideTarget is { } target)
        {
            centre = target.Position;
            return true;
        }

        var positions = new List<Vector3>();
        if (GameMode.Current is { } mode)
            foreach (var controller in mode.Controllers)
                if (controller.ControlledPawn?.GetComponent<Transform3D>() is { } pawn)
                    positions.Add(pawn.Position);

        if (positions.Count == 0) return false;

        foreach (var position in positions) centre += position;
        centre /= positions.Count;

        foreach (var position in positions)
            spread = MathF.Max(spread, Vector3.Distance(position, centre));

        return true;
    }
}
