using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Animation;

// ---------------------------------------------------------------------------
// Parameter system
// ---------------------------------------------------------------------------

public enum ParameterType { Bool, Int, Float, Trigger }

/// <summary>
/// A named runtime parameter that <see cref="TransitionCondition"/>s read when
/// deciding whether to cross a transition.
/// </summary>
public class AnimatorParameter
{
    public string        Name  { get; set; } = "";
    public ParameterType Type  { get; set; }

    /// <summary>
    /// Boxed current value. Bool → bool, Int → int, Float → float, Trigger → bool
    /// (true = armed, reset to false after it fires).
    /// </summary>
    public object        Value { get; set; } = false;
}

// ---------------------------------------------------------------------------
// Transition system
// ---------------------------------------------------------------------------

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Greater,
    Less,
    TrueValue,   // parameter must be bool/trigger == true
    FalseValue   // parameter must be bool/trigger == false
}

/// <summary>
/// A single boolean test on a parameter that must pass for the owning
/// <see cref="AnimatorTransition"/> to fire.
/// </summary>
public class TransitionCondition
{
    /// <summary>Name of the <see cref="AnimatorParameter"/> to test.</summary>
    public string            Parameter { get; set; } = "";

    public ConditionOperator Op        { get; set; }

    /// <summary>
    /// The threshold value compared using <see cref="Op"/>.
    /// Unused for TrueValue / FalseValue operators.
    /// </summary>
    public object            Value     { get; set; } = 0;
}

/// <summary>
/// Declares that the owning <see cref="AnimatorState"/> can transition to
/// <see cref="Target"/> when all <see cref="Conditions"/> are met (and, optionally,
/// the current clip has reached <see cref="ExitTime"/>).
/// </summary>
public class AnimatorTransition
{
    /// <summary>Name of the destination <see cref="AnimatorState"/>.</summary>
    public string Target      { get; set; } = "";

    public List<TransitionCondition> Conditions { get; } = new();

    /// <summary>
    /// When true the transition will only fire after the current clip has played
    /// at least <see cref="ExitTime"/> (0–1 normalised) of its total duration.
    /// </summary>
    public bool   HasExitTime { get; set; } = false;

    /// <summary>Normalised playback position (0–1) required before this transition can fire.</summary>
    public float  ExitTime    { get; set; } = 1f;
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/// <summary>
/// One node in the animator graph. Maps to exactly one <see cref="AnimationClip"/>
/// (via its name) and holds the outgoing transitions evaluated each frame.
/// </summary>
public class AnimatorState
{
    public string Name { get; set; } = "";

    /// <summary>Name of the <see cref="AnimationClip"/> played while in this state.</summary>
    public string Clip { get; set; } = "";

    public List<AnimatorTransition> Transitions { get; } = new();
}

// ---------------------------------------------------------------------------
// Controller component
// ---------------------------------------------------------------------------

/// <summary>
/// State machine that drives a sibling <see cref="SpriteAnimator"/>.
/// Analogous to Unity's Animator component: states play clips; transitions
/// move between states based on parameter conditions and optional exit times.
/// </summary>
[RequireComponent(typeof(SpriteAnimator))]
public class AnimatorController : Component
{
    // -------------------------------------------------------------------------
    // Graph
    // -------------------------------------------------------------------------

    public Dictionary<string, AnimatorState>     States     { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AnimatorParameter> Parameters { get; } = new(StringComparer.Ordinal);

    /// <summary>Currently active state name.</summary>
    public string? CurrentState   { get; private set; }

    /// <summary>
    /// Optional name of a virtual "AnyState" node whose transitions are evaluated
    /// every frame regardless of the current state (like Unity's AnyState).
    /// Set to a state name (conventionally "AnyState") before calling <see cref="Start"/>.
    /// </summary>
    public string? AnyStateSource { get; set; }

    // -------------------------------------------------------------------------
    // Parameter setters
    // -------------------------------------------------------------------------

    public void SetBool(string name, bool value)
    {
        var p = GetOrThrowParam(name, ParameterType.Bool);
        p.Value = value;
    }

    public void SetInt(string name, int value)
    {
        var p = GetOrThrowParam(name, ParameterType.Int);
        p.Value = value;
    }

    public void SetFloat(string name, float value)
    {
        var p = GetOrThrowParam(name, ParameterType.Float);
        p.Value = value;
    }

    /// <summary>
    /// Arms a trigger parameter. It will be reset to false automatically after
    /// it fires a transition.
    /// </summary>
    public void SetTrigger(string name)
    {
        var p = GetOrThrowParam(name, ParameterType.Trigger);
        p.Value = true;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void Start()
    {
        // Enter the first state that isn't the AnyState virtual node.
        foreach (var (name, _) in States)
        {
            if (name == AnyStateSource) continue;
            EnterState(name);
            break;
        }
    }

    public override void Update(float dt)
    {
        if (CurrentState == null) return;

        var animator = GetComponent<SpriteAnimator>();

        // Gather triggered parameters so we can reset them after they fire.
        List<string>? triggersToReset = null;

        // Try transitions from the AnyState virtual node first (lower specificity
        // than the current state, but evaluated before so it mirrors Unity behaviour
        // where AnyState can interrupt at any time).
        if (AnyStateSource != null && States.TryGetValue(AnyStateSource, out var anyState))
        {
            if (TryFireTransition(anyState.Transitions, animator, ref triggersToReset))
            {
                ResetTriggers(triggersToReset);
                return;
            }
        }

        // Try transitions from the current state.
        if (States.TryGetValue(CurrentState, out var current))
        {
            if (TryFireTransition(current.Transitions, animator, ref triggersToReset))
            {
                ResetTriggers(triggersToReset);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers — transition evaluation
    // -------------------------------------------------------------------------

    private bool TryFireTransition(
        List<AnimatorTransition> transitions,
        SpriteAnimator?          animator,
        ref List<string>?        triggersToReset)
    {
        foreach (var transition in transitions)
        {
            // Skip self-transitions to avoid restart loops.
            if (transition.Target == CurrentState) continue;

            // Exit-time check.
            if (transition.HasExitTime && animator != null && animator.CurrentClip != null)
            {
                int    totalFrames  = animator.CurrentClip.EndFrame - animator.CurrentClip.StartFrame + 1;
                int    localFrame   = animator.CurrentFrame - animator.CurrentClip.StartFrame;
                float  normalised   = totalFrames > 0 ? localFrame / (float)totalFrames : 1f;
                if (normalised < transition.ExitTime) continue;
            }

            // Condition check.
            if (!AllConditionsMet(transition.Conditions, ref triggersToReset)) continue;

            // Fire.
            EnterState(transition.Target);
            return true;
        }
        return false;
    }

    private bool AllConditionsMet(List<TransitionCondition> conditions, ref List<string>? triggersToReset)
    {
        foreach (var cond in conditions)
        {
            if (!Parameters.TryGetValue(cond.Parameter, out var param)) return false;
            if (!EvaluateCondition(param, cond)) return false;

            // Collect trigger names so we can reset them after the transition fires.
            if (param.Type == ParameterType.Trigger)
            {
                triggersToReset ??= new List<string>();
                if (!triggersToReset.Contains(param.Name))
                    triggersToReset.Add(param.Name);
            }
        }
        return true;
    }

    private static bool EvaluateCondition(AnimatorParameter param, TransitionCondition cond)
    {
        return cond.Op switch
        {
            ConditionOperator.TrueValue  => param.Value is true,
            ConditionOperator.FalseValue => param.Value is false or null,
            ConditionOperator.Equals     => CompareValues(param, cond.Value) == 0,
            ConditionOperator.NotEquals  => CompareValues(param, cond.Value) != 0,
            ConditionOperator.Greater    => CompareValues(param, cond.Value) > 0,
            ConditionOperator.Less       => CompareValues(param, cond.Value) < 0,
            _                            => false
        };
    }

    /// <summary>
    /// Returns a negative / zero / positive int like IComparable.CompareTo would.
    /// Handles Int and Float parameters; others fall back to equality.
    /// </summary>
    private static int CompareValues(AnimatorParameter param, object threshold)
    {
        try
        {
            if (param.Type == ParameterType.Int)
            {
                int a = Convert.ToInt32(param.Value);
                int b = Convert.ToInt32(threshold);
                return a.CompareTo(b);
            }
            if (param.Type == ParameterType.Float)
            {
                float a = Convert.ToSingle(param.Value);
                float b = Convert.ToSingle(threshold);
                return a.CompareTo(b);
            }
        }
        catch { /* fall through */ }

        return Equals(param.Value, threshold) ? 0 : -1;
    }

    // -------------------------------------------------------------------------
    // State switching
    // -------------------------------------------------------------------------

    private void EnterState(string stateName)
    {
        if (!States.TryGetValue(stateName, out var state)) return;

        CurrentState = stateName;

        var animator = GetComponent<SpriteAnimator>();
        if (animator == null) return;

        if (!string.IsNullOrEmpty(state.Clip))
        {
            // Use Play so looping/non-looping is controlled by the clip definition.
            if (animator.Clips.ContainsKey(state.Clip))
                animator.Play(state.Clip, restart: true);
        }
    }

    // -------------------------------------------------------------------------
    // Trigger reset
    // -------------------------------------------------------------------------

    private void ResetTriggers(List<string>? triggers)
    {
        if (triggers == null) return;
        foreach (var name in triggers)
        {
            if (Parameters.TryGetValue(name, out var p))
                p.Value = false;
        }
    }

    // -------------------------------------------------------------------------
    // Parameter lookup
    // -------------------------------------------------------------------------

    private AnimatorParameter GetOrThrowParam(string name, ParameterType expectedType)
    {
        if (!Parameters.TryGetValue(name, out var p))
            throw new KeyNotFoundException($"AnimatorController: no parameter '{name}'.");
        if (p.Type != expectedType)
            throw new InvalidOperationException(
                $"AnimatorController: parameter '{name}' is {p.Type}, not {expectedType}.");
        return p;
    }
}
