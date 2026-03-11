using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Animation;

// ---------------------------------------------------------------------------
// Internal step types
// ---------------------------------------------------------------------------

/// <summary>Represents one segment of work inside a <see cref="Tween"/> or sequence.</summary>
internal abstract class TweenStep
{
    internal float Duration { get; set; }

    /// <summary>
    /// Advance <paramref name="dt"/> seconds.  Returns how many seconds were
    /// "consumed" (may be less than dt if the step finished early).
    /// </summary>
    internal abstract float Update(float dt);

    internal abstract void Reset();
    internal abstract bool IsComplete { get; }
}

// ---------------------------------------------------------------------------
// Delay step
// ---------------------------------------------------------------------------

internal sealed class DelayStep : TweenStep
{
    private float _elapsed;

    internal DelayStep(float seconds) { Duration = seconds; }

    internal override bool  IsComplete => _elapsed >= Duration;
    internal override float Update(float dt)
    {
        float remaining = Duration - _elapsed;
        float consume   = Math.Min(dt, remaining);
        _elapsed += consume;
        return consume;
    }
    internal override void Reset() => _elapsed = 0f;
}

// ---------------------------------------------------------------------------
// Callback step
// ---------------------------------------------------------------------------

internal sealed class CallbackStep : TweenStep
{
    private readonly Action _callback;
    private bool _fired;

    internal CallbackStep(Action callback) { _callback = callback; Duration = 0f; }

    internal override bool  IsComplete => _fired;
    internal override float Update(float dt)
    {
        if (!_fired) { _fired = true; _callback(); }
        return 0f;
    }
    internal override void Reset() => _fired = false;
}

// ---------------------------------------------------------------------------
// Value step (the core workhorse)
// ---------------------------------------------------------------------------

internal sealed class ValueStep : TweenStep
{
    private readonly Func<float>   _getter;
    private readonly Action<float> _setter;
    private readonly float         _target;
    private readonly EaseType      _ease;

    private float _startValue;
    private float _elapsed;
    private bool  _initialised;

    internal ValueStep(Func<float> getter, Action<float> setter, float target, float duration, EaseType ease)
    {
        _getter  = getter;
        _setter  = setter;
        _target  = target;
        Duration = duration;
        _ease    = ease;
    }

    internal override bool IsComplete => Duration <= 0f || _elapsed >= Duration;

    internal override float Update(float dt)
    {
        if (!_initialised) { _startValue = _getter(); _initialised = true; }

        if (Duration <= 0f)
        {
            _setter(_target);
            _elapsed = Duration;
            return dt; // instant
        }

        float remaining = Duration - _elapsed;
        float consume   = Math.Min(dt, remaining);
        _elapsed += consume;

        float t      = Math.Clamp(_elapsed / Duration, 0f, 1f);
        float eased  = Easing.Evaluate(_ease, t);
        _setter(_startValue + (_target - _startValue) * eased);

        return consume;
    }

    internal override void Reset()
    {
        _elapsed     = 0f;
        _initialised = false;
    }
}

// ---------------------------------------------------------------------------
// Parallel group step (joins multiple steps that run simultaneously)
// ---------------------------------------------------------------------------

internal sealed class ParallelStep : TweenStep
{
    internal readonly List<TweenStep> Inner = new();

    internal ParallelStep() { Duration = 0f; }

    internal void Add(TweenStep step)
    {
        Inner.Add(step);
        Duration = Math.Max(Duration, step.Duration);
    }

    internal override bool IsComplete => Inner.TrueForAll(s => s.IsComplete);

    internal override float Update(float dt)
    {
        float maxConsumed = 0f;
        foreach (var s in Inner)
        {
            if (!s.IsComplete)
                maxConsumed = Math.Max(maxConsumed, s.Update(dt));
        }
        return maxConsumed;
    }

    internal override void Reset()
    {
        foreach (var s in Inner) s.Reset();
    }
}

// ---------------------------------------------------------------------------
// Tween
// ---------------------------------------------------------------------------

/// <summary>
/// A lightweight tween that drives one or more value interpolations.
/// Constructed fluently via <see cref="Create"/>.
/// Call <see cref="Play"/> to register with the global update loop.
/// </summary>
public class Tween
{
    // -------------------------------------------------------------------------
    // Global registry
    // -------------------------------------------------------------------------

    private static readonly List<Tween> _activeTweens = new();

    /// <summary>
    /// Advance all active tweens by <paramref name="dt"/> seconds.
    /// Call this once per frame from the engine update (e.g. <c>SBEngine.Update</c>).
    /// </summary>
    public static void UpdateAll(float dt)
    {
        // Iterate a snapshot so tweens may Kill themselves inside their callback.
        var snapshot = _activeTweens.ToArray();
        foreach (var t in snapshot)
        {
            if (!t._playing) continue;
            t.UpdateInternal(dt);
        }

        // Remove completed / killed tweens.
        _activeTweens.RemoveAll(t => !t._playing && t.IsComplete);
    }

    /// <summary>Kill every active tween.</summary>
    public static void KillAll()
    {
        foreach (var t in _activeTweens) t._playing = false;
        _activeTweens.Clear();
    }

    /// <summary>
    /// Kill all tweens whose <see cref="BoundActor"/> matches <paramref name="actor"/>.
    /// </summary>
    public static void KillAllFor(Actor actor)
    {
        for (int i = _activeTweens.Count - 1; i >= 0; i--)
        {
            if (_activeTweens[i].BoundActor == actor)
            {
                _activeTweens[i]._playing = false;
                _activeTweens.RemoveAt(i);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------

    public static Tween Create() => new Tween();

    /// <summary>Create a new empty <see cref="TweenSequence"/>.</summary>
    public static TweenSequence Sequence() => new TweenSequence();

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    /// <summary>Actor associated with this tween; used by <see cref="KillAllFor"/>.</summary>
    public Actor? BoundActor { get; set; }

    public bool IsPlaying  => _playing;
    public bool IsComplete { get; private set; }

    private bool   _playing;
    private int    _loopCount;       // -1 = infinite
    private int    _loopsRemaining;
    private Action? _onComplete;

    // Internal step list (sequential within this tween).
    private readonly List<TweenStep> _steps      = new();
    private int                      _stepIndex  = 0;

    // -------------------------------------------------------------------------
    // Fluent API — targets
    // -------------------------------------------------------------------------

    public Tween TweenValue(Func<float> getter, Action<float> setter, float target, float duration,
                            EaseType ease = EaseType.Linear)
    {
        AppendStep(new ValueStep(getter, setter, target, duration, ease));
        return this;
    }

    public Tween TweenPosition(Transform t, Vector2 target, float duration,
                               EaseType ease = EaseType.Linear)
    {
        // X and Y run in parallel.
        var group = new ParallelStep();
        group.Add(new ValueStep(() => t.Position.X, v => t.Position = new Vector2(v, t.Position.Y), target.X, duration, ease));
        group.Add(new ValueStep(() => t.Position.Y, v => t.Position = new Vector2(t.Position.X, v), target.Y, duration, ease));
        AppendStep(group);
        return this;
    }

    public Tween TweenRotation(Transform t, float target, float duration,
                               EaseType ease = EaseType.Linear)
    {
        AppendStep(new ValueStep(() => t.Rotation, v => t.Rotation = v, target, duration, ease));
        return this;
    }

    public Tween TweenScale(Transform t, Vector2 target, float duration,
                            EaseType ease = EaseType.Linear)
    {
        var group = new ParallelStep();
        group.Add(new ValueStep(() => t.Scale.X, v => t.Scale = new Vector2(v, t.Scale.Y), target.X, duration, ease));
        group.Add(new ValueStep(() => t.Scale.Y, v => t.Scale = new Vector2(t.Scale.X, v), target.Y, duration, ease));
        AppendStep(group);
        return this;
    }

    public Tween TweenColor(SpriteRenderer sr, Color target, float duration,
                            EaseType ease = EaseType.Linear)
    {
        var group = new ParallelStep();
        group.Add(new ValueStep(() => sr.Tint.R / 255f, v => sr.Tint = new Color(v, sr.Tint.G / 255f, sr.Tint.B / 255f, sr.Tint.A / 255f), target.R / 255f, duration, ease));
        group.Add(new ValueStep(() => sr.Tint.G / 255f, v => sr.Tint = new Color(sr.Tint.R / 255f, v, sr.Tint.B / 255f, sr.Tint.A / 255f), target.G / 255f, duration, ease));
        group.Add(new ValueStep(() => sr.Tint.B / 255f, v => sr.Tint = new Color(sr.Tint.R / 255f, sr.Tint.G / 255f, v, sr.Tint.A / 255f), target.B / 255f, duration, ease));
        group.Add(new ValueStep(() => sr.Tint.A / 255f, v => sr.Tint = new Color(sr.Tint.R / 255f, sr.Tint.G / 255f, sr.Tint.B / 255f, v), target.A / 255f, duration, ease));
        AppendStep(group);
        return this;
    }

    /// <summary>
    /// Tween a named float property on an arbitrary object via reflection.
    /// Use the strongly-typed overloads where possible; this is a convenience fallback.
    /// </summary>
    public Tween TweenFloat(object targetObj, string propertyName, float endValue, float duration,
                            EaseType ease = EaseType.Linear)
    {
        var prop = targetObj.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Tween.TweenFloat: no public property '{propertyName}' on {targetObj.GetType().Name}.");

        float Get()  => Convert.ToSingle(prop.GetValue(targetObj));
        void  Set(float v) => prop.SetValue(targetObj, v);

        AppendStep(new ValueStep(Get, Set, endValue, duration, ease));
        return this;
    }

    // -------------------------------------------------------------------------
    // Fluent API — control
    // -------------------------------------------------------------------------

    public Tween Delay(float seconds)
    {
        AppendStep(new DelayStep(seconds));
        return this;
    }

    /// <summary>
    /// Loop the entire tween <paramref name="times"/> times (-1 = infinite).
    /// Must be called before <see cref="Play"/>.
    /// </summary>
    public Tween Loop(int times = -1)
    {
        _loopCount = times;
        return this;
    }

    public Tween OnComplete(Action callback)
    {
        _onComplete += callback;
        return this;
    }

    public Tween Play()
    {
        IsComplete      = false;
        _playing        = true;
        _stepIndex      = 0;
        _loopsRemaining = _loopCount;

        foreach (var s in _steps) s.Reset();

        if (!_activeTweens.Contains(this))
            _activeTweens.Add(this);

        return this;
    }

    public void Kill()
    {
        _playing = false;
        _activeTweens.Remove(this);
    }

    public void Pause()  => _playing = false;
    public void Resume() => _playing = true;

    // -------------------------------------------------------------------------
    // Internal update
    // -------------------------------------------------------------------------

    internal void UpdateInternal(float dt)
    {
        if (_steps.Count == 0) { Finish(); return; }

        float remaining = dt;

        while (remaining > 0f && _stepIndex < _steps.Count)
        {
            var step     = _steps[_stepIndex];
            float used   = step.Update(remaining);
            remaining   -= used;

            if (step.IsComplete) _stepIndex++;
        }

        if (_stepIndex >= _steps.Count)
        {
            // All steps done — handle looping.
            if (_loopCount == -1 || _loopsRemaining > 1)
            {
                if (_loopsRemaining > 0) _loopsRemaining--;
                _stepIndex = 0;
                foreach (var s in _steps) s.Reset();
            }
            else
            {
                Finish();
            }
        }
    }

    private void Finish()
    {
        IsComplete = true;
        _playing   = false;
        _onComplete?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Step management
    // -------------------------------------------------------------------------

    private void AppendStep(TweenStep step) => _steps.Add(step);

    /// <summary>
    /// Internal: used by <see cref="TweenSequence"/> to add a pre-built step directly.
    /// </summary>
    internal void AddStepDirect(TweenStep step) => _steps.Add(step);

    internal List<TweenStep> Steps => _steps;
}

// ---------------------------------------------------------------------------
// TweenSequence
// ---------------------------------------------------------------------------

/// <summary>
/// Builder for a chain of tweens and intervals that plays them one after another
/// (with optional parallel "joins"). Backed by a single internal <see cref="Tween"/>.
/// </summary>
public class TweenSequence
{
    private readonly Tween            _root    = Tween.Create();
    private          ParallelStep?    _pending;   // last step that can be Join'd

    // -------------------------------------------------------------------------
    // Builder
    // -------------------------------------------------------------------------

    /// <summary>Append a tween to run after everything queued so far.</summary>
    public TweenSequence Append(Tween tween)
    {
        FlushPending();

        // Collapse the tween's own steps into a parallel group so they all run
        // simultaneously (a single Tween is itself sequential internally, but when
        // appended to a sequence we treat it as one unit).
        var group = new ParallelStep();
        foreach (var s in tween.Steps)
            group.Add(s);

        _pending = group;
        return this;
    }

    public TweenSequence AppendInterval(float seconds)
    {
        FlushPending();
        _root.AddStepDirect(new DelayStep(seconds));
        return this;
    }

    public TweenSequence AppendCallback(Action callback)
    {
        FlushPending();
        _root.AddStepDirect(new CallbackStep(callback));
        return this;
    }

    /// <summary>
    /// Run the provided tween in parallel with the previously appended step.
    /// </summary>
    public TweenSequence Join(Tween tween)
    {
        if (_pending == null)
        {
            // Nothing to join to — treat as Append.
            return Append(tween);
        }

        foreach (var s in tween.Steps)
            _pending.Add(s);

        return this;
    }

    public TweenSequence Loop(int times = -1)
    {
        _root.Loop(times);
        return this;
    }

    public TweenSequence OnComplete(Action callback)
    {
        _root.OnComplete(callback);
        return this;
    }

    public TweenSequence Play()
    {
        FlushPending();
        _root.Play();
        return this;
    }

    public void Kill() => _root.Kill();

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private void FlushPending()
    {
        if (_pending == null) return;
        _root.AddStepDirect(_pending);
        _pending = null;
    }
}
