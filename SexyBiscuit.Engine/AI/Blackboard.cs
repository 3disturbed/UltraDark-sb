using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.AI;

/// <summary>
/// A typed key-value store shared between the nodes of a <see cref="BehaviorTree"/> —
/// the equivalent of Unreal's Blackboard asset.
/// </summary>
/// <remarks>
/// Keeping state here rather than in node fields is what makes trees reusable: a
/// <c>MoveTo</c> node reads its destination from a named key, so the same node instance
/// works for "go to the player" and "go to cover" without subclassing.
/// </remarks>
/// <example>
/// <code>
/// blackboard.Set("Target", playerActor);
/// blackboard.Set("Alertness", 0.8f);
///
/// if (blackboard.TryGet&lt;Actor&gt;("Target", out var target))
///     Chase(target);
/// </code>
/// </example>
public sealed class Blackboard
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>Raised whenever a key is written, with the key name.</summary>
    public SBEvent<string> ValueChanged { get; } = new();

    /// <summary>Every key currently set.</summary>
    public IEnumerable<string> Keys => _values.Keys;

    /// <summary>Writes a value, replacing any existing entry under <paramref name="key"/>.</summary>
    public void Set<T>(string key, T value)
    {
        _values[key] = value;
        ValueChanged.Broadcast(key);
    }

    /// <summary>
    /// Reads a value. Returns false when the key is absent or holds a different type,
    /// so a stale key of the wrong type cannot throw mid-tick.
    /// </summary>
    public bool TryGet<T>(string key, out T value)
    {
        if (_values.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Reads a value, returning <paramref name="fallback"/> when absent or mistyped.</summary>
    public T Get<T>(string key, T fallback = default!)
        => TryGet<T>(key, out var v) ? v : fallback;

    /// <summary>True when the key exists and holds a non-null value.</summary>
    public bool Has(string key) => _values.TryGetValue(key, out var v) && v != null;

    /// <summary>Removes a key. Returns false when it was not present.</summary>
    public bool Clear(string key)
    {
        bool removed = _values.Remove(key);
        if (removed) ValueChanged.Broadcast(key);
        return removed;
    }

    /// <summary>Removes every key.</summary>
    public void ClearAll()
    {
        _values.Clear();
    }

    // -------------------------------------------------------------------------
    // Convenience accessors for the types AI code reaches for most
    // -------------------------------------------------------------------------

    /// <summary>Reads a world position, either stored directly or taken from a stored actor.</summary>
    /// <remarks>
    /// Accepting either form means a node can be pointed at a moving actor or a fixed spot
    /// through the same key, which is how "investigate last known position" is usually written.
    /// </remarks>
    public bool TryGetPosition(string key, out Vector3 position)
    {
        if (TryGet<Vector3>(key, out position)) return true;

        if (TryGet<Actor>(key, out var actor))
        {
            var t = actor.GetComponent<Transform3D>();
            if (t != null) { position = t.Position; return true; }
        }

        position = Vector3.Zero;
        return false;
    }
}
