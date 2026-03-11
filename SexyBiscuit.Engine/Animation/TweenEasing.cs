using System;

namespace SexyBiscuit.Engine.Animation;

// ---------------------------------------------------------------------------
// Ease type catalogue
// ---------------------------------------------------------------------------
public enum EaseType
{
    Linear,
    InQuad,    OutQuad,    InOutQuad,
    InCubic,   OutCubic,   InOutCubic,
    InQuart,   OutQuart,   InOutQuart,
    InQuint,   OutQuint,   InOutQuint,
    InSine,    OutSine,    InOutSine,
    InExpo,    OutExpo,    InOutExpo,
    InCirc,    OutCirc,    InOutCirc,
    InBack,    OutBack,    InOutBack,
    InBounce,  OutBounce,  InOutBounce,
    InElastic, OutElastic, InOutElastic,
    Spring
}

// ---------------------------------------------------------------------------
// Static evaluator — all formulae operate on t ∈ [0, 1]
// ---------------------------------------------------------------------------
public static class Easing
{
    // Back overshoot constant (industry standard)
    private const float C1 = 1.70158f;
    private const float C2 = C1 * 1.525f;
    private const float C3 = C1 + 1f;

    // Elastic constants
    private const float C4 = (2f * MathF.PI) / 3f;
    private const float C5 = (2f * MathF.PI) / 4.5f;

    /// <summary>
    /// Evaluate an easing function for normalised time <paramref name="t"/> in [0, 1].
    /// Returns values that may exceed [0, 1] for overshoot easings (Back, Elastic, Spring).
    /// </summary>
    public static float Evaluate(EaseType type, float t)
    {
        // Clamp only the Linear-style evaluations; overshoot types are intentionally left free.
        t = Math.Clamp(t, 0f, 1f);

        return type switch
        {
            EaseType.Linear       => t,

            // ---- Quad -------------------------------------------------------
            EaseType.InQuad       => t * t,
            EaseType.OutQuad      => 1f - (1f - t) * (1f - t),
            EaseType.InOutQuad    => t < 0.5f
                                        ? 2f * t * t
                                        : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f,

            // ---- Cubic ------------------------------------------------------
            EaseType.InCubic      => t * t * t,
            EaseType.OutCubic     => 1f - MathF.Pow(1f - t, 3f),
            EaseType.InOutCubic   => t < 0.5f
                                        ? 4f * t * t * t
                                        : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,

            // ---- Quart ------------------------------------------------------
            EaseType.InQuart      => t * t * t * t,
            EaseType.OutQuart     => 1f - MathF.Pow(1f - t, 4f),
            EaseType.InOutQuart   => t < 0.5f
                                        ? 8f * t * t * t * t
                                        : 1f - MathF.Pow(-2f * t + 2f, 4f) / 2f,

            // ---- Quint ------------------------------------------------------
            EaseType.InQuint      => t * t * t * t * t,
            EaseType.OutQuint     => 1f - MathF.Pow(1f - t, 5f),
            EaseType.InOutQuint   => t < 0.5f
                                        ? 16f * t * t * t * t * t
                                        : 1f - MathF.Pow(-2f * t + 2f, 5f) / 2f,

            // ---- Sine -------------------------------------------------------
            EaseType.InSine       => 1f - MathF.Cos(t * MathF.PI / 2f),
            EaseType.OutSine      => MathF.Sin(t * MathF.PI / 2f),
            EaseType.InOutSine    => -(MathF.Cos(MathF.PI * t) - 1f) / 2f,

            // ---- Expo -------------------------------------------------------
            EaseType.InExpo       => t == 0f ? 0f : MathF.Pow(2f, 10f * t - 10f),
            EaseType.OutExpo      => t == 1f ? 1f : 1f - MathF.Pow(2f, -10f * t),
            EaseType.InOutExpo    => t == 0f ? 0f
                                   : t == 1f ? 1f
                                   : t < 0.5f
                                        ? MathF.Pow(2f, 20f * t - 10f) / 2f
                                        : (2f - MathF.Pow(2f, -20f * t + 10f)) / 2f,

            // ---- Circ -------------------------------------------------------
            EaseType.InCirc       => 1f - MathF.Sqrt(1f - t * t),
            EaseType.OutCirc      => MathF.Sqrt(1f - MathF.Pow(t - 1f, 2f)),
            EaseType.InOutCirc    => t < 0.5f
                                        ? (1f - MathF.Sqrt(1f - MathF.Pow(2f * t, 2f))) / 2f
                                        : (MathF.Sqrt(1f - MathF.Pow(-2f * t + 2f, 2f)) + 1f) / 2f,

            // ---- Back -------------------------------------------------------
            EaseType.InBack       => C3 * t * t * t - C1 * t * t,
            EaseType.OutBack      => 1f + C3 * MathF.Pow(t - 1f, 3f) + C1 * MathF.Pow(t - 1f, 2f),
            EaseType.InOutBack    => t < 0.5f
                                        ? MathF.Pow(2f * t, 2f) * ((C2 + 1f) * 2f * t - C2) / 2f
                                        : (MathF.Pow(2f * t - 2f, 2f) * ((C2 + 1f) * (2f * t - 2f) + C2) + 2f) / 2f,

            // ---- Bounce -----------------------------------------------------
            EaseType.OutBounce    => OutBounce(t),
            EaseType.InBounce     => 1f - OutBounce(1f - t),
            EaseType.InOutBounce  => t < 0.5f
                                        ? (1f - OutBounce(1f - 2f * t)) / 2f
                                        : (1f + OutBounce(2f * t - 1f)) / 2f,

            // ---- Elastic ----------------------------------------------------
            EaseType.InElastic    => InElastic(t),
            EaseType.OutElastic   => OutElastic(t),
            EaseType.InOutElastic => InOutElastic(t),

            // ---- Spring -----------------------------------------------------
            // Damped harmonic oscillator: e^(-6t) * cos(2π * t * 1.5) gives a
            // spring that settles toward 1.  We invert the decay so the value
            // starts at 0 and asymptotically settles at 1 with overshoot.
            EaseType.Spring       => Spring(t),

            _                     => t
        };
    }

    // -------------------------------------------------------------------------
    // Bounce helpers
    // -------------------------------------------------------------------------
    private static float OutBounce(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;

        if (t < 1f / d1)
            return n1 * t * t;
        else if (t < 2f / d1)
        {
            t -= 1.5f / d1;
            return n1 * t * t + 0.75f;
        }
        else if (t < 2.5f / d1)
        {
            t -= 2.25f / d1;
            return n1 * t * t + 0.9375f;
        }
        else
        {
            t -= 2.625f / d1;
            return n1 * t * t + 0.984375f;
        }
    }

    // -------------------------------------------------------------------------
    // Elastic helpers
    // -------------------------------------------------------------------------
    private static float InElastic(float t)
    {
        if (t == 0f) return 0f;
        if (t == 1f) return 1f;
        return -MathF.Pow(2f, 10f * t - 10f) * MathF.Sin((t * 10f - 10.75f) * C4);
    }

    private static float OutElastic(float t)
    {
        if (t == 0f) return 0f;
        if (t == 1f) return 1f;
        return MathF.Pow(2f, -10f * t) * MathF.Sin((t * 10f - 0.75f) * C4) + 1f;
    }

    private static float InOutElastic(float t)
    {
        if (t == 0f) return 0f;
        if (t == 1f) return 1f;
        return t < 0.5f
            ? -(MathF.Pow(2f, 20f * t - 10f) * MathF.Sin((20f * t - 11.125f) * C5)) / 2f
            : MathF.Pow(2f, -20f * t + 10f) * MathF.Sin((20f * t - 11.125f) * C5) / 2f + 1f;
    }

    // -------------------------------------------------------------------------
    // Spring: damped oscillation that settles at 1
    // Formula: 1 - e^(-decay * t) * cos(2π * frequency * t)
    // With decay=6, frequency=1.5 we get ~2 overshoots before settling.
    // -------------------------------------------------------------------------
    private static float Spring(float t)
    {
        const float decay     = 6f;
        const float frequency = 1.5f;
        return 1f - MathF.Exp(-decay * t) * MathF.Cos(2f * MathF.PI * frequency * t);
    }
}
