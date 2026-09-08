using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Who has focus, which subtree they are trapped in, and which way is "next".
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a gamepad, a D-pad and a TV remote able to drive a UI at all: the
/// engine had no focus model of any kind, so nothing but a pointer could reach a widget.
/// </para>
/// <para>
/// Scopes are derived from the tree rather than kept in a stack the caller has to
/// maintain. A stack and a tree get out of step the first time something is destroyed
/// while a dialog is open, and then focus escapes to a panel that is no longer on screen.
/// </para>
/// </remarks>
public sealed class UiFocus
{
    private readonly UiNode _root;

    public UiFocus(UiNode root) => _root = root;

    /// <summary>The node holding focus, or null.</summary>
    public UiNode? Focused { get; private set; }

    /// <summary>Which class of device the player is currently driving the UI with.</summary>
    public UiInputMode Mode => Modes.Mode;

    /// <summary>Tracks which device is driving, so the focus ring appears only when it should.</summary>
    public UiInputModeTracker Modes { get; } = new();

    // -------------------------------------------------------------------------
    // Scopes
    // -------------------------------------------------------------------------

    /// <summary>
    /// The subtree focus is currently confined to: the top-most visible modal, or the
    /// whole tree when there is none.
    /// </summary>
    public UiNode ActiveScope
    {
        get
        {
            UiNode scope = _root;
            foreach (UiNode node in InPaintOrder(_root))
                if (node.Modal && IsVisibleInHierarchy(node)) scope = node;
            return scope;
        }
    }

    // -------------------------------------------------------------------------
    // Focusability
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether a node can hold focus right now.
    /// </summary>
    /// <remarks>
    /// Being scrolled out of sight deliberately does <em>not</em> disqualify a node. A
    /// clipped node is not clickable, because a pointer cannot reach what it cannot see,
    /// but it must stay navigable or a long list would be impossible to move through with
    /// a stick — the focus simply scrolls it back into view.
    /// </remarks>
    public static bool IsFocusable(UiNode node)
    {
        if (node.Focusable == Focusability.No) return false;
        if (!IsVisibleInHierarchy(node)) return false;
        if (node.Rect.IsEmpty) return false;

        return node.Focusable == Focusability.Yes || KindTakesFocus(node.Kind);
    }

    private static bool KindTakesFocus(UiKind kind) => kind
        is UiKind.Button or UiKind.Slider or UiKind.Toggle
        or UiKind.TextField or UiKind.Dropdown or UiKind.TabStrip;

    private static bool IsVisibleInHierarchy(UiNode node)
    {
        for (UiNode? n = node; n != null; n = n.Parent)
            if (!n.Visible || !n.Interactive || n.Opacity <= 0.01f) return false;
        return true;
    }

    /// <summary>Everything inside the current scope that could take focus.</summary>
    public List<UiNode> Candidates()
    {
        var found = new List<UiNode>();
        UiNode scope = ActiveScope;

        if (IsFocusable(scope)) found.Add(scope);
        foreach (UiNode node in InPaintOrder(scope))
            if (IsFocusable(node)) found.Add(node);

        return found;
    }

    // -------------------------------------------------------------------------
    // Moving focus
    // -------------------------------------------------------------------------

    /// <summary>Gives focus to a node, or clears it when handed null. Returns whether it moved.</summary>
    public bool Focus(UiNode? node)
    {
        if (ReferenceEquals(Focused, node)) return false;
        if (node != null && !IsFocusable(node)) return false;

        Focused = node;
        return true;
    }

    /// <summary>
    /// Focuses whatever a scope should start on: an explicit request, else the first
    /// candidate in reading order.
    /// </summary>
    /// <remarks>
    /// Reading order rather than tree order, because a two-column dialog built column by
    /// column would otherwise open with the top of the second column focused.
    /// </remarks>
    public bool FocusFirst()
    {
        List<UiNode> candidates = Candidates();
        if (candidates.Count == 0) return Focus(null);

        foreach (UiNode node in candidates)
            if (node.AutoFocus) return Focus(node);

        return Focus(InReadingOrder(candidates, ActiveScope)[0]);
    }

    /// <summary>
    /// Moves focus one step in a direction. Returns whether anything moved.
    /// </summary>
    public bool Navigate(NavDirection direction)
    {
        if (Focused == null || !IsFocusable(Focused)) return FocusFirst();

        List<UiNode> candidates = Candidates();
        candidates.Remove(Focused);
        if (candidates.Count == 0) return false;

        if (ResolveOverride(Focused, direction, candidates) is { } explicitTarget)
            return Focus(explicitTarget);

        var rects = new List<NavCandidate>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++) rects.Add(new NavCandidate(i, candidates[i].Rect));

        int index = UiNavigation.Find(Focused.Rect, rects, direction);
        return index >= 0 && Focus(candidates[index]);
    }

    /// <summary>
    /// Follows a chain of explicit overrides, skipping links that are not focusable.
    /// </summary>
    /// <remarks>
    /// Hopping onwards rather than giving up is what lets an author wire a fixed order
    /// once and have it survive an item being disabled, without writing any conditionals.
    /// The hop limit stops a cycle of dead links spinning forever.
    /// </remarks>
    private UiNode? ResolveOverride(UiNode from, NavDirection direction, List<UiNode> candidates)
    {
        UiNode current = from;

        for (int hop = 0; hop < 8; hop++)
        {
            string name = direction switch
            {
                NavDirection.Up    => current.NavUp,
                NavDirection.Down  => current.NavDown,
                NavDirection.Left  => current.NavLeft,
                NavDirection.Right => current.NavRight,
                _                  => "",
            };

            if (string.IsNullOrEmpty(name)) return null;

            UiNode? target = ActiveScope.Find(name);
            if (target == null) return null;
            if (candidates.Contains(target)) return target;

            current = target;
        }

        return null;
    }

    /// <summary>
    /// Puts focus somewhere sensible after the focused node was hidden or destroyed.
    /// </summary>
    /// <remarks>
    /// The nearest remaining candidate by plain centre distance, with no direction
    /// involved — "the first child" would teleport the player to the top of a list every
    /// time they deleted a row near the bottom of it.
    /// </remarks>
    public bool Repair()
    {
        if (Focused != null && IsFocusable(Focused)) return false;

        // Nothing has ever held focus here, so this is a scope opening rather than a
        // repair: AutoFocus and plain reading order decide, not the geometric accident
        // of which control happens to sit nearest the middle of the screen.
        if (Focused == null) return FocusFirst();

        Vector2 was = Focused.Rect.Centre;
        List<UiNode> candidates = Candidates();
        if (candidates.Count == 0) return Focus(null);

        UiNode? nearest = null;
        float best = float.MaxValue;

        foreach (UiNode node in candidates)
        {
            float distance = Vector2.DistanceSquared(node.Rect.Centre, was);
            if (distance >= best) continue;
            best = distance;
            nearest = node;
        }

        Focused = nearest;
        return true;
    }

    // -------------------------------------------------------------------------
    // Ordering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Left to right, top to bottom, with rows banded so that items which look level are
    /// treated as level even when their tops differ by a pixel or two.
    /// </summary>
    private static List<UiNode> InReadingOrder(List<UiNode> nodes, UiNode scope)
    {
        float band = MathF.Max(1f, scope.Rect.Height / 8f);

        var ordered = new List<UiNode>(nodes);
        ordered.Sort((a, b) =>
        {
            int rowA = (int)MathF.Floor(a.Rect.Y / band);
            int rowB = (int)MathF.Floor(b.Rect.Y / band);
            if (rowA != rowB) return rowA.CompareTo(rowB);
            return a.Rect.X.CompareTo(b.Rect.X);
        });
        return ordered;
    }

    /// <summary>Every descendant, in the order they are painted, so the last one is on top.</summary>
    private static IEnumerable<UiNode> InPaintOrder(UiNode node)
    {
        foreach (UiNode child in UiLayout.PaintOrder(node))
        {
            yield return child;
            foreach (UiNode deeper in InPaintOrder(child)) yield return deeper;
        }
    }
}

/// <summary>
/// Decides which class of device is driving the UI, and resists changing its mind.
/// </summary>
/// <remarks>
/// Without hysteresis this thrashes: a worn thumbstick resting at 0.4 makes the focus ring
/// flicker on while somebody is using the mouse, and a jittery mouse or a trackpad resting
/// under a palm steals the mode back from a gamepad. So a switch needs a deliberate event —
/// travel that outruns a decay, or an axis that actually crosses a threshold from below it —
/// never a level that merely happens to be high.
/// </remarks>
public sealed class UiInputModeTracker
{
    public UiInputMode Mode { get; private set; } = UiInputMode.Pointer;

    /// <summary>Canvas units the mouse must travel, against the decay, to claim the mode.</summary>
    public float MouseWakeDistance { get; set; } = 8f;

    /// <summary>How fast accumulated mouse travel bleeds away, in canvas units a second.</summary>
    public float MouseTravelDecay { get; set; } = 40f;

    /// <summary>An axis must reach this to count as pushed.</summary>
    public float EngageThreshold { get; set; } = 0.5f;

    /// <summary>And must fall below this before it can engage again.</summary>
    public float ReleaseThreshold { get; set; } = 0.35f;

    /// <summary>A shove this hard is believed immediately, without waiting a second frame.</summary>
    public float DecisiveMagnitude { get; set; } = 0.75f;

    /// <summary>How long after a touch the mouse is ignored, so a stylus or palm cannot flip back.</summary>
    public float TouchLockoutSeconds { get; set; } = 0.5f;

    private float _travel;
    private float _sinceTouch = float.MaxValue;
    private bool  _axisEngaged;
    private int   _framesAbove;

    /// <summary>True when the ring should be drawn — only ever under directional input.</summary>
    public bool ShowFocusRing => Mode == UiInputMode.Directional;

    /// <summary>True when hover states apply — a finger has no hover to show.</summary>
    public bool ShowHover => Mode == UiInputMode.Pointer;

    public void Tick(float dt)
    {
        _travel = MathF.Max(0f, _travel - MouseTravelDecay * dt);
        if (_sinceTouch < float.MaxValue) _sinceTouch += dt;
    }

    /// <summary>Mouse movement. Jitter never outruns the decay; a real hand movement does.</summary>
    public bool NoteMouseMotion(Vector2 delta)
    {
        if (_sinceTouch < TouchLockoutSeconds) return false;

        _travel += delta.Length();
        if (_travel < MouseWakeDistance) return false;

        _travel = 0f;
        return SwitchTo(UiInputMode.Pointer);
    }

    /// <summary>A click or a wheel notch is unambiguous, so it bypasses the accumulator.</summary>
    public bool NoteMouseButton() => SwitchTo(UiInputMode.Pointer);

    public bool NoteTouch()
    {
        _sinceTouch = 0f;
        return SwitchTo(UiInputMode.Touch);
    }

    /// <summary>
    /// A navigation axis this frame. Only a crossing counts, so a stick resting past the
    /// threshold engages once and then has to be released before it can engage again.
    /// </summary>
    public bool NoteNavigationAxis(float magnitude)
    {
        magnitude = MathF.Abs(magnitude);

        if (magnitude < ReleaseThreshold)
        {
            _axisEngaged = false;
            _framesAbove = 0;
            return false;
        }

        if (magnitude < EngageThreshold || _axisEngaged) return false;

        _framesAbove++;
        if (_framesAbove < 2 && magnitude < DecisiveMagnitude) return false;

        _axisEngaged = true;
        return SwitchTo(UiInputMode.Directional);
    }

    /// <summary>A button press from a keyboard or pad, which is never ambiguous.</summary>
    public bool NoteNavigationButton() => SwitchTo(UiInputMode.Directional);

    private bool SwitchTo(UiInputMode mode)
    {
        if (Mode == mode) return false;
        Mode = mode;
        return true;
    }
}
