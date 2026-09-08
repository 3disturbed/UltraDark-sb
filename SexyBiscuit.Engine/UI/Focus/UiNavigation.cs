using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>Which way the player asked to move.</summary>
public enum NavDirection { Up, Down, Left, Right }

/// <summary>One thing that could be moved to, and the caller's own handle on it.</summary>
public readonly record struct NavCandidate(int Index, RectangleF Rect);

/// <summary>The numbers that decide which neighbour a direction means.</summary>
/// <remarks>
/// These constants are the whole behaviour, and a number that is quietly different on one
/// engine is exactly the drift a regular expression over the other engine's source cannot
/// see. They are pinned by <c>html5/tests/fixtures/nav-cases.json</c>, which both suites read.
/// </remarks>
public readonly record struct NavSettings(
    float PerpendicularPenalty = 2.0f,
    float CentreWeight         = 0.25f,
    float OverlapBonus         = 12.0f,
    float ReachFactor          = 2.0f,
    float Epsilon              = 0.5f)
{
    public static readonly NavSettings Default = new();
}

/// <summary>
/// Picks the neighbour a direction means, from resolved rectangles alone.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a pure function over rectangles with no engine types in sight: it is the
/// part of controller and TV-remote support most likely to feel wrong, and being pure is
/// what lets both engines run the identical fixture and compare answers.
/// </para>
/// <para>
/// The scoring is nearest-in-the-direction-of-travel first, with a doubled penalty for
/// moving sideways, so a grid steps along its row before it jumps rows. Overlap on the
/// perpendicular axis earns a bonus, so a properly aligned item beats one that is barely
/// clipping the line and marginally closer — which is the difference between a menu that
/// feels right under a thumbstick and one that feels arbitrary.
/// </para>
/// </remarks>
public static class UiNavigation
{
    /// <summary>
    /// The index of the candidate to move to, or -1 when the direction leads nowhere.
    /// </summary>
    public static int Find(RectangleF from, IReadOnlyList<NavCandidate> candidates,
                           NavDirection direction, NavSettings settings = default)
    {
        if (settings == default) settings = NavSettings.Default;
        if (candidates.Count == 0) return -1;

        RectangleF f = Project(from, direction);

        int best = -1;
        float bestScore = float.MaxValue;
        float bestCentreDelta = float.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            RectangleF t = Project(candidates[i].Rect, direction);
            if (t.IsEmpty) continue;

            float primaryGap = t.Left - f.Right;

            // Ahead of us, by centre or by a clean edge. Centre-beyond-centre rather than
            // edge-beyond-edge, so a tall sidebar item beside a short one still reads as
            // being "to the right" rather than being skipped for overlapping slightly.
            bool ahead = t.Centre.X > f.Centre.X + settings.Epsilon
                      || t.Left >= f.Right - settings.Epsilon;
            if (!ahead) continue;

            float overlap = MathF.Max(0f, MathF.Min(f.Bottom, t.Bottom) - MathF.Max(f.Top, t.Top));
            float smaller = MathF.Max(1f, MathF.Min(f.Height, t.Height));
            float overlapFraction = Math.Clamp(overlap / smaller, 0f, 1f);

            float secondaryGap = overlap > 0f
                ? 0f
                : MathF.Max(t.Top - f.Bottom, f.Top - t.Bottom);

            float centreDelta = MathF.Abs(t.Centre.Y - f.Centre.Y);

            // Anything this far off to one side is not what the player meant, however
            // close it is; without this, Right can select something two rows down and
            // half a screen across.
            float reach = MathF.Max(f.Height, t.Height) * settings.ReachFactor + MathF.Max(0f, primaryGap);
            if (centreDelta > reach) continue;

            float score = MathF.Max(0f, primaryGap)
                        + secondaryGap * settings.PerpendicularPenalty
                        + centreDelta  * settings.CentreWeight
                        - overlapFraction * settings.OverlapBonus;

            // Ties resolve by alignment and then by the order the caller listed them, so
            // the answer is always the same one and a test can assert an exact winner.
            bool better = score < bestScore - 0.0001f
                       || (MathF.Abs(score - bestScore) <= 0.0001f && centreDelta < bestCentreDelta - 0.0001f);

            if (!better) continue;

            best = i;
            bestScore = score;
            bestCentreDelta = centreDelta;
        }

        return best >= 0 ? best : FindInCone(f, candidates, direction, settings);
    }

    /// <summary>
    /// The fallback for layouts with no rectangular alignment at all.
    /// </summary>
    /// <remarks>
    /// A radial or diamond menu overlaps nothing in any direction, so the strict pass
    /// rejects every candidate and the menu becomes unusable on a pad. Accepting anything
    /// within 45 degrees of the direction, scored by plain distance, makes a diamond
    /// navigate the way it looks, and leaves a scattered layout at least moving.
    /// </remarks>
    private static int FindInCone(RectangleF f, IReadOnlyList<NavCandidate> candidates,
                                  NavDirection direction, NavSettings settings)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        float bestCentreDelta = float.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            RectangleF t = Project(candidates[i].Rect, direction);
            if (t.IsEmpty) continue;

            float dx = t.Centre.X - f.Centre.X;
            float dy = t.Centre.Y - f.Centre.Y;

            if (dx <= settings.Epsilon) continue;
            if (MathF.Abs(dy) > MathF.Abs(dx)) continue;

            float distance = dx * dx + dy * dy;
            float centreDelta = MathF.Abs(dy);

            bool better = distance < bestDistance - 0.0001f
                       || (MathF.Abs(distance - bestDistance) <= 0.0001f && centreDelta < bestCentreDelta);

            if (!better) continue;

            best = i;
            bestDistance = distance;
            bestCentreDelta = centreDelta;
        }

        return best;
    }

    /// <summary>
    /// Rotates and mirrors a rectangle so that "forward" is always +X.
    /// </summary>
    /// <remarks>
    /// Four directions written out four times is four chances to get one of them subtly
    /// wrong, and the wrong one is usually Up because it is the one nobody tests by hand.
    /// </remarks>
    private static RectangleF Project(RectangleF r, NavDirection direction) => direction switch
    {
        NavDirection.Right => new RectangleF(r.X, r.Y, r.Width, r.Height),
        NavDirection.Left  => new RectangleF(-r.Right, r.Y, r.Width, r.Height),
        NavDirection.Down  => new RectangleF(r.Y, r.X, r.Height, r.Width),
        NavDirection.Up    => new RectangleF(-r.Bottom, r.X, r.Height, r.Width),
        _                  => r,
    };
}
