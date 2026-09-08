using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// One frame of input, in the form the UI needs it. The mirror of the object
/// <c>html5/src/ui/UiInput.js</c> takes.
/// </summary>
/// <remarks>
/// A plain struct rather than a reference to the <c>InputManager</c> on purpose. The router
/// is then a pure function of this and the tree, so a fixture can drive it with no window,
/// no device and no gamepad — which is the only way the two engines' interaction behaviour
/// can be pinned by the same file rather than by two hand-written test suites that drift.
/// </remarks>
public struct UiInputFrame
{
    /// <summary>Unscaled seconds since the last frame. Drives the caret and the mode tracker.</summary>
    public float DeltaTime;

    /// <summary>Pointer position in device pixels. Off-screen is fine; nothing will be hit.</summary>
    public Vector2 Pointer;

    /// <summary>Pointer movement since the last frame, in device pixels.</summary>
    public Vector2 PointerDelta;

    /// <summary>Whether the primary button or the finger is held. Held, not pressed.</summary>
    public bool PointerDown;

    /// <summary>Whether the pointer is a finger, which has no hover state to show.</summary>
    public bool PointerIsTouch;

    /// <summary>Wheel notches this frame. Positive scrolls the content up, as every OS does.</summary>
    public float Wheel;

    /// <summary>Navigation stick, each axis -1..1. Feeds the input-mode hysteresis.</summary>
    public Vector2 NavAxis;

    /// <summary>Discrete navigation presses, from a D-pad, the arrow keys or a stick edge.</summary>
    public bool NavUp, NavDown, NavLeft, NavRight;

    /// <summary>Activate the focused node — A, Enter or Space.</summary>
    public bool Confirm;

    /// <summary>Back out — B or Escape. Closes an open list before it does anything else.</summary>
    public bool Cancel;

    /// <summary>Characters typed this frame, for whichever text field has focus.</summary>
    public string Typed;

    /// <summary>Whether backspace was pressed this frame.</summary>
    public bool Backspace;
}

/// <summary>
/// Turns a frame of input into hover, press, click, focus and control behaviour on one
/// <see cref="UiCanvas"/>. The mirror of <c>html5/src/ui/UiInput.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// The seven kinds after <see cref="UiKind.Image"/> have no painter of their own and no
/// behaviour of their own either — a <see cref="UiKind.Slider"/> was a tag that made a node
/// focusable and nothing more. This is where dragging, toggling, opening and scrolling
/// actually happen, so that a document describing a settings screen becomes a settings
/// screen rather than a picture of one.
/// </para>
/// <para>
/// A click is a release inside the node the press started on. Dragging off a button and
/// letting go must not fire it, which is what every other toolkit means by a click.
/// </para>
/// </remarks>
public sealed class UiInput
{
    private readonly UiCanvas _canvas;

    /// <summary>The node the current press started on, which is the only one that can click.</summary>
    private UiNode? _pressed;

    /// <summary>The slider or scroll bar the pointer is dragging, if any.</summary>
    private UiNode? _dragging;

    private bool    _wasDown;
    private Vector2 _lastPointer;

    public UiInput(UiCanvas canvas)
    {
        _canvas = canvas;
        Focus   = new UiFocus(canvas.Root);
    }

    /// <summary>Focus and the input-mode tracker for this canvas.</summary>
    public UiFocus Focus { get; }

    /// <summary>The node under the pointer this frame, or null.</summary>
    public UiNode? Hovered { get; private set; }

    /// <summary>How far one wheel notch scrolls, in canvas units.</summary>
    public float WheelStep { get; set; } = 48f;

    // -------------------------------------------------------------------------
    // The frame
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves one frame. Call after the canvas has been laid out, so every
    /// rectangle the hit test reads is this frame's.
    /// </summary>
    public void Update(UiInputFrame frame)
    {
        ClearTransient(_canvas.Root);
        Focus.Modes.Tick(frame.DeltaTime);

        if (!_canvas.Interactive)
        {
            _wasDown     = false;
            _pressed     = null;
            _dragging    = null;
            Hovered      = null;
            return;
        }

        NoteDevices(frame);

        // Before anything reads it. Focus resolved afterwards would mean the first frame
        // a menu is open swallows its own input: a key press with nothing focused yet has
        // nowhere to go, and the player has to press twice.
        Focus.Repair();

        UiNode? hit = Pick(frame.Pointer);
        Hovered = Focus.Modes.ShowHover ? hit : null;
        if (Hovered != null) Hovered.Hovered = true;

        Pointer(frame, hit);
        Wheel(frame, hit);
        Directional(frame);
        Typing(frame);

        SyncFocusFlags(_canvas.Root);

        _wasDown     = frame.PointerDown;
        _lastPointer = frame.Pointer;
    }

    // -------------------------------------------------------------------------
    // Devices
    // -------------------------------------------------------------------------

    private void NoteDevices(UiInputFrame frame)
    {
        if (frame.PointerIsTouch && frame.PointerDown) Focus.Modes.NoteTouch();
        else if (frame.PointerDelta != Vector2.Zero)   Focus.Modes.NoteMouseMotion(frame.PointerDelta);

        if (frame.PointerDown && !_wasDown && !frame.PointerIsTouch) Focus.Modes.NoteMouseButton();
        if (frame.Wheel != 0f) Focus.Modes.NoteMouseButton();

        if (frame.NavAxis != Vector2.Zero)
            Focus.Modes.NoteNavigationAxis(MathF.Max(MathF.Abs(frame.NavAxis.X), MathF.Abs(frame.NavAxis.Y)));

        if (frame.NavUp || frame.NavDown || frame.NavLeft || frame.NavRight || frame.Confirm)
            Focus.Modes.NoteNavigationButton();
    }

    // -------------------------------------------------------------------------
    // Pointer
    // -------------------------------------------------------------------------

    /// <summary>
    /// The node under a device-pixel point, an open dropdown list winning over the tree.
    /// </summary>
    /// <remarks>
    /// A list is painted outside its control's rectangle and outside every clip, so the
    /// ordinary hit test cannot see it. Without this a player can see the options and
    /// clicks straight through them onto whatever is behind.
    /// </remarks>
    private UiNode? Pick(Vector2 devicePoint)
    {
        Vector2 point = _canvas.ScreenToCanvas(devicePoint);

        if (ExpandedDropdown() is { } open && UiPainter.DropdownListRect(open).Contains(point))
            return open;

        return _canvas.HitTest(devicePoint);
    }

    private void Pointer(UiInputFrame frame, UiNode? hit)
    {
        bool pressedNow  = frame.PointerDown && !_wasDown;
        bool releasedNow = !frame.PointerDown && _wasDown;

        if (pressedNow) BeginPress(frame, hit);
        else if (frame.PointerDown) ContinuePress(frame);
        else if (releasedNow) EndPress(frame, hit);

        if (_pressed != null) _pressed.Pressed = frame.PointerDown;
    }

    private void BeginPress(UiInputFrame frame, UiNode? hit)
    {
        Vector2 point = _canvas.ScreenToCanvas(frame.Pointer);

        // A press that lands anywhere but the open list closes it, including on the
        // control itself — which is what makes a second click on a dropdown shut it.
        if (ExpandedDropdown() is { } open && open != hit) open.Expanded = false;

        // A disabled node still blocks the pointer — it is not a hole — but it must not
        // press, click or take focus, so the press lands on nothing at all.
        _pressed  = Usable(hit) ? hit : null;
        _dragging = null;
        if (_pressed is null || hit is null) return;

        if (UiFocus.IsFocusable(hit)) Focus.Focus(hit);

        switch (hit.Kind)
        {
            case UiKind.Slider:
                _dragging = hit;
                SetSliderFromPointer(hit, point);
                break;

            case UiKind.TabStrip:
                SelectTabAt(hit, point);
                break;

            case UiKind.Dropdown when hit.Expanded:
                SelectOptionAt(hit, point);
                break;
        }
    }

    /// <summary>
    /// Whether a press is currently being dragged across a control.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="UiCanvas.ScreenToCanvas"/> in world space, where a pointer ray can
    /// stop meeting the canvas's plane mid-drag if the camera swings. Holding the last good
    /// point is better than reporting a miss, which would read as a jump to the far edge.
    /// </remarks>
    internal bool IsDragging => _dragging != null;

    private void ContinuePress(UiInputFrame frame)
    {
        if (_dragging is { Kind: UiKind.Slider } slider)
            SetSliderFromPointer(slider, _canvas.ScreenToCanvas(frame.Pointer));
    }

    private void EndPress(UiInputFrame frame, UiNode? hit)
    {
        UiNode? pressed = _pressed;
        _pressed  = null;
        _dragging = null;

        if (pressed is null) return;
        pressed.Pressed = false;

        // The release has to land on the same node the press started on.
        if (!ReferenceEquals(pressed, hit)) return;

        pressed.Clicked = true;
        Activate(pressed, ActivationSource.Pointer);
    }

    private void SetSliderFromPointer(UiNode slider, Vector2 point)
    {
        if (slider.Rect.Width <= 0f) return;

        float fraction = Math.Clamp((point.X - slider.Rect.X) / slider.Rect.Width, 0f, 1f);
        SetValue(slider, slider.MinValue + (slider.MaxValue - slider.MinValue) * fraction);
    }

    /// <summary>Whether a node and every ancestor of it is visible and interactive.</summary>
    private static bool Usable(UiNode? node)
    {
        for (UiNode? n = node; n != null; n = n.Parent)
            if (!n.Visible || !n.Interactive) return false;
        return node != null;
    }

    private static void SelectTabAt(UiNode strip, Vector2 point)
    {
        for (int i = 0; i < strip.Options.Count; i++)
            if (UiPainter.TabRect(strip, i).Contains(point)) { strip.SelectedIndex = i; return; }
    }

    private static void SelectOptionAt(UiNode dropdown, Vector2 point)
    {
        RectangleF list = UiPainter.DropdownListRect(dropdown);
        if (dropdown.Rect.Height <= 0f || !list.Contains(point)) return;

        int index = (int)((point.Y - list.Y) / dropdown.Rect.Height);
        if (index < 0 || index >= dropdown.Options.Count) return;

        dropdown.SelectedIndex = index;
        dropdown.Expanded      = false;
        dropdown.Clicked       = true;
    }

    // -------------------------------------------------------------------------
    // Wheel
    // -------------------------------------------------------------------------

    private void Wheel(UiInputFrame frame, UiNode? hit)
    {
        if (frame.Wheel == 0f || hit is null) return;
        if (NearestScrollable(hit) is not { } view) return;

        Vector2 max = ScrollRange(view);
        bool vertical = view.Scroll is ScrollMode.Vertical or ScrollMode.Both && max.Y > 0f;

        Vector2 offset = view.ScrollOffset;
        if (vertical) offset.Y = Math.Clamp(offset.Y - frame.Wheel * WheelStep, 0f, max.Y);
        else          offset.X = Math.Clamp(offset.X - frame.Wheel * WheelStep, 0f, max.X);

        view.ScrollOffset = offset;
    }

    /// <summary>The first ancestor that scrolls and has something to scroll over.</summary>
    private static UiNode? NearestScrollable(UiNode from)
    {
        for (UiNode? n = from; n != null; n = n.Parent)
        {
            if (n.Scroll == ScrollMode.None) continue;
            Vector2 range = ScrollRange(n);
            if (range.X > 0f || range.Y > 0f) return n;
        }
        return null;
    }

    private static Vector2 ScrollRange(UiNode view) => new(
        MathF.Max(0f, view.ContentSize.X - view.ContentRect.Width),
        MathF.Max(0f, view.ContentSize.Y - view.ContentRect.Height));

    // -------------------------------------------------------------------------
    // Directional
    // -------------------------------------------------------------------------

    private void Directional(UiInputFrame frame)
    {
        if (frame.Cancel && ExpandedDropdown() is { } open)
        {
            open.Expanded = false;
            return;
        }

        UiNode? focused = Focus.Focused;

        // An open list swallows up and down: they move through the options, and only
        // once it is closed do they move between controls again.
        if (focused is { Kind: UiKind.Dropdown, Expanded: true })
        {
            if (frame.NavUp)   StepSelection(focused, -1);
            if (frame.NavDown) StepSelection(focused, +1);
            if (frame.Confirm) { focused.Expanded = false; focused.Clicked = true; }
            return;
        }

        // A focused slider takes left and right as its own, because navigating away
        // from a volume slider by pressing right is not what anybody means by it.
        if (focused is { Kind: UiKind.Slider } slider && (frame.NavLeft || frame.NavRight))
        {
            float step = slider.Step > 0f ? slider.Step : (slider.MaxValue - slider.MinValue) / 20f;
            SetValue(slider, slider.Value + (frame.NavRight ? step : -step));
            return;
        }

        // So does a tab strip, which is a row of choices however it is laid out.
        if (focused is { Kind: UiKind.TabStrip } strip && (frame.NavLeft || frame.NavRight))
        {
            StepSelection(strip, frame.NavRight ? +1 : -1);
            return;
        }

        if (frame.NavUp)    Focus.Navigate(NavDirection.Up);
        if (frame.NavDown)  Focus.Navigate(NavDirection.Down);
        if (frame.NavLeft)  Focus.Navigate(NavDirection.Left);
        if (frame.NavRight) Focus.Navigate(NavDirection.Right);

        if (frame.Confirm && Focus.Focused is { } target)
        {
            target.Clicked = true;
            Activate(target, ActivationSource.Directional);
        }
    }

    private static void StepSelection(UiNode node, int by)
    {
        int count = node.Options.Count;
        if (count == 0) return;
        node.SelectedIndex = Math.Clamp(node.SelectedIndex + by, 0, count - 1);
    }

    // -------------------------------------------------------------------------
    // Typing
    // -------------------------------------------------------------------------

    private void Typing(UiInputFrame frame)
    {
        if (Focus.Focused is not { Kind: UiKind.TextField } field) return;

        if (frame.Backspace && field.Text.Length > 0)
            field.Text = field.Text[..^1];

        if (!string.IsNullOrEmpty(frame.Typed))
            field.Text += frame.Typed;
    }

    // -------------------------------------------------------------------------
    // Activation
    // -------------------------------------------------------------------------

    private enum ActivationSource { Pointer, Directional }

    /// <summary>
    /// What a kind does when it is activated, over and above reporting a click.
    /// </summary>
    /// <remarks>
    /// A tab strip and an open dropdown are deliberately absent: the pointer already
    /// chose which tab and which option on the way down, and re-running that here would
    /// undo the choice using the release position.
    /// </remarks>
    private static void Activate(UiNode node, ActivationSource source)
    {
        switch (node.Kind)
        {
            case UiKind.Toggle:
                node.Checked = !node.Checked;
                break;

            case UiKind.Dropdown when !node.Expanded:
                node.Expanded = node.Options.Count > 0;
                break;

            case UiKind.TabStrip when source == ActivationSource.Directional:
                // Confirm on a tab strip is a no-op: left and right already moved it.
                break;
        }
    }

    private static void SetValue(UiNode node, float value)
    {
        float min = node.MinValue, max = node.MaxValue;
        if (max < min) (min, max) = (max, min);

        if (node.Step > 0f) value = min + MathF.Round((value - min) / node.Step) * node.Step;
        node.Value = Math.Clamp(value, min, max);
    }

    // -------------------------------------------------------------------------
    // Tree bookkeeping
    // -------------------------------------------------------------------------

    private UiNode? ExpandedDropdown()
    {
        if (_canvas.Root is { Kind: UiKind.Dropdown, Expanded: true }) return _canvas.Root;
        foreach (UiNode node in _canvas.Root.Descendants())
            if (node is { Kind: UiKind.Dropdown, Expanded: true, Visible: true }) return node;
        return null;
    }

    /// <summary>
    /// Clears the states that last exactly one frame, everywhere.
    /// </summary>
    /// <remarks>
    /// <see cref="UiNode.Clicked"/> in particular: a caller that reads it once a frame must
    /// never see the same click twice, and a node that stopped being hit must stop
    /// reporting a hover it can no longer have.
    /// </remarks>
    private static void ClearTransient(UiNode node)
    {
        node.Hovered = false;
        node.Clicked = false;
        foreach (UiNode child in node.Children) ClearTransient(child);
    }

    private void SyncFocusFlags(UiNode node)
    {
        node.Focused = ReferenceEquals(node, Focus.Focused);
        foreach (UiNode child in node.Children) SyncFocusFlags(child);
    }
}
