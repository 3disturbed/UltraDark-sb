// -----------------------------------------------------------------------------
// UiInput — turns a frame of input into hover, press, click, focus and control
// behaviour on one UiCanvas.
//
// The mirror of SexyBiscuit.Engine/UI/UiInput.cs.
//
// The seven kinds after Image have no painter of their own and had no behaviour of
// their own either — a Slider was a tag that made a node focusable and nothing more.
// This is where dragging, toggling, opening and scrolling actually happen, so that a
// document describing a settings screen becomes a settings screen rather than a
// picture of one.
//
// A frame is a plain object rather than a reference to the InputManager on purpose:
// the router is then a pure function of that object and the tree, so a fixture can
// drive it with no canvas, no device and no gamepad, which is the only way the two
// engines' interaction behaviour can be pinned by one shared file.
// -----------------------------------------------------------------------------

import { UiKind, ScrollMode } from './UiEnums.js';
import { contains as rectContains } from './UiLayout.js';
import { UiFocus } from './UiFocus.js';
import { NavDirection } from './UiNavigation.js';
import { tabRect, dropdownListRect } from './UiPainter.js';

/** The fields a frame may carry, and what they mean when they are missing. */
export const EMPTY_FRAME = Object.freeze({
    deltaTime: 0,
    pointer: { x: -1, y: -1 },
    pointerDelta: { x: 0, y: 0 },
    pointerDown: false,
    pointerIsTouch: false,
    wheel: 0,
    navAxis: { x: 0, y: 0 },
    navUp: false,
    navDown: false,
    navLeft: false,
    navRight: false,
    confirm: false,
    cancel: false,
    typed: '',
    backspace: false,
});

export class UiInput {
    constructor(canvas) {
        this.canvas = canvas;
        this.focus = new UiFocus(canvas.root);

        /** The node under the pointer this frame, or null. */
        this.hovered = null;

        /** How far one wheel notch scrolls, in canvas units. */
        this.wheelStep = 48;

        /** The node the current press started on, which is the only one that can click. */
        this._pressed = null;

        /** The slider the pointer is dragging, if any. */
        this._dragging = null;

        this._wasDown = false;
    }

    // -------------------------------------------------------------------------
    // The frame
    // -------------------------------------------------------------------------

    /**
     * Resolves one frame. Call after the canvas has been laid out, so every
     * rectangle the hit test reads is this frame's.
     */
    update(input = {}) {
        const frame = { ...EMPTY_FRAME, ...input };

        clearTransient(this.canvas.root);
        this.focus.modes.tick(frame.deltaTime);

        if (!this.canvas.interactive) {
            this._wasDown = false;
            this._pressed = null;
            this._dragging = null;
            this.hovered = null;
            return;
        }

        this._noteDevices(frame);

        // Before anything reads it. Focus resolved afterwards would mean the first frame
        // a menu is open swallows its own input: a key press with nothing focused yet has
        // nowhere to go, and the player has to press twice.
        this.focus.repair();

        const hit = this._pick(frame.pointer);
        this.hovered = this.focus.modes.showHover ? hit : null;
        if (this.hovered) this.hovered.hovered = true;

        this._pointer(frame, hit);
        this._wheel(frame, hit);
        this._directional(frame);
        this._typing(frame);

        syncFocusFlags(this.canvas.root, this.focus.focused);

        this._wasDown = frame.pointerDown;
    }

    // -------------------------------------------------------------------------
    // Devices
    // -------------------------------------------------------------------------

    _noteDevices(frame) {
        if (frame.pointerIsTouch && frame.pointerDown) this.focus.modes.noteTouch();
        else if (frame.pointerDelta.x !== 0 || frame.pointerDelta.y !== 0) {
            this.focus.modes.noteMouseMotion(frame.pointerDelta);
        }

        if (frame.pointerDown && !this._wasDown && !frame.pointerIsTouch) this.focus.modes.noteMouseButton();
        if (frame.wheel !== 0) this.focus.modes.noteMouseButton();

        if (frame.navAxis.x !== 0 || frame.navAxis.y !== 0) {
            this.focus.modes.noteNavigationAxis(Math.max(Math.abs(frame.navAxis.x), Math.abs(frame.navAxis.y)));
        }

        if (frame.navUp || frame.navDown || frame.navLeft || frame.navRight || frame.confirm) {
            this.focus.modes.noteNavigationButton();
        }
    }

    // -------------------------------------------------------------------------
    // Pointer
    // -------------------------------------------------------------------------

    /**
     * The node under a device-pixel point, an open dropdown list winning over the
     * tree. A list is painted outside its control's rectangle and outside every
     * clip, so the ordinary hit test cannot see it; without this a player can see
     * the options and clicks straight through them onto whatever is behind.
     */
    _pick(devicePoint) {
        const point = this.canvas.screenToCanvas(devicePoint);
        const open = this._expandedDropdown();

        if (open && rectContains(dropdownListRect(open), point)) return open;
        return this.canvas.hitTest(devicePoint);
    }

    _pointer(frame, hit) {
        const pressedNow = frame.pointerDown && !this._wasDown;
        const releasedNow = !frame.pointerDown && this._wasDown;

        if (pressedNow) this._beginPress(frame, hit);
        else if (frame.pointerDown) this._continuePress(frame);
        else if (releasedNow) this._endPress(frame, hit);

        if (this._pressed) this._pressed.pressed = frame.pointerDown;
    }

    _beginPress(frame, hit) {
        const point = this.canvas.screenToCanvas(frame.pointer);

        // A press that lands anywhere but the open list closes it, including on the
        // control itself — which is what makes a second click on a dropdown shut it.
        const open = this._expandedDropdown();
        if (open && open !== hit) open.expanded = false;

        // A disabled node still blocks the pointer — it is not a hole — but it must not
        // press, click or take focus, so the press lands on nothing at all.
        this._pressed = usable(hit) ? hit : null;
        this._dragging = null;
        if (!this._pressed) return;

        if (UiFocus.isFocusable(hit)) this.focus.focus(hit);

        if (hit.kind === UiKind.Slider) {
            this._dragging = hit;
            setSliderFromPointer(hit, point);
        } else if (hit.kind === UiKind.TabStrip) {
            selectTabAt(hit, point);
        } else if (hit.kind === UiKind.Dropdown && hit.expanded) {
            selectOptionAt(hit, point);
        }
    }

    _continuePress(frame) {
        if (this._dragging && this._dragging.kind === UiKind.Slider) {
            setSliderFromPointer(this._dragging, this.canvas.screenToCanvas(frame.pointer));
        }
    }

    _endPress(frame, hit) {
        const pressed = this._pressed;
        this._pressed = null;
        this._dragging = null;

        if (!pressed) return;
        pressed.pressed = false;

        // The release has to land on the same node the press started on.
        if (pressed !== hit) return;

        pressed.clicked = true;
        activate(pressed, 'pointer');
    }

    // -------------------------------------------------------------------------
    // Wheel
    // -------------------------------------------------------------------------

    _wheel(frame, hit) {
        if (frame.wheel === 0 || !hit) return;

        const view = nearestScrollable(hit);
        if (!view) return;

        const max = scrollRange(view);
        const vertical = (view.scroll === ScrollMode.Vertical || view.scroll === ScrollMode.Both) && max.y > 0;

        const offset = { ...view.scrollOffset };
        if (vertical) offset.y = clamp(offset.y - frame.wheel * this.wheelStep, 0, max.y);
        else offset.x = clamp(offset.x - frame.wheel * this.wheelStep, 0, max.x);

        view.scrollOffset = offset;
    }

    // -------------------------------------------------------------------------
    // Directional
    // -------------------------------------------------------------------------

    _directional(frame) {
        if (frame.cancel) {
            const open = this._expandedDropdown();
            if (open) { open.expanded = false; return; }
        }

        const focused = this.focus.focused;

        // An open list swallows up and down: they move through the options, and only
        // once it is closed do they move between controls again.
        if (focused && focused.kind === UiKind.Dropdown && focused.expanded) {
            if (frame.navUp) stepSelection(focused, -1);
            if (frame.navDown) stepSelection(focused, +1);
            if (frame.confirm) { focused.expanded = false; focused.clicked = true; }
            return;
        }

        // A focused slider takes left and right as its own, because navigating away
        // from a volume slider by pressing right is not what anybody means by it.
        if (focused && focused.kind === UiKind.Slider && (frame.navLeft || frame.navRight)) {
            const step = focused.step > 0 ? focused.step : (focused.maxValue - focused.minValue) / 20;
            setValue(focused, focused.value + (frame.navRight ? step : -step));
            return;
        }

        // So does a tab strip, which is a row of choices however it is laid out.
        if (focused && focused.kind === UiKind.TabStrip && (frame.navLeft || frame.navRight)) {
            stepSelection(focused, frame.navRight ? +1 : -1);
            return;
        }

        if (frame.navUp) this.focus.navigate(NavDirection.Up);
        if (frame.navDown) this.focus.navigate(NavDirection.Down);
        if (frame.navLeft) this.focus.navigate(NavDirection.Left);
        if (frame.navRight) this.focus.navigate(NavDirection.Right);

        if (frame.confirm && this.focus.focused) {
            this.focus.focused.clicked = true;
            activate(this.focus.focused, 'directional');
        }
    }

    // -------------------------------------------------------------------------
    // Typing
    // -------------------------------------------------------------------------

    _typing(frame) {
        const field = this.focus.focused;
        if (!field || field.kind !== UiKind.TextField) return;

        if (frame.backspace && field.text.length > 0) field.text = field.text.slice(0, -1);
        if (frame.typed) field.text += frame.typed;
    }

    // -------------------------------------------------------------------------
    // Tree bookkeeping
    // -------------------------------------------------------------------------

    _expandedDropdown() {
        if (this.canvas.root.kind === UiKind.Dropdown && this.canvas.root.expanded) return this.canvas.root;
        for (const node of this.canvas.root.descendants()) {
            if (node.kind === UiKind.Dropdown && node.expanded && node.visible) return node;
        }
        return null;
    }
}

// -----------------------------------------------------------------------------
// Behaviour
// -----------------------------------------------------------------------------

/**
 * What a kind does when it is activated, over and above reporting a click.
 *
 * A tab strip and an open dropdown are deliberately absent: the pointer already
 * chose which tab and which option on the way down, and re-running that here would
 * undo the choice using the release position.
 */
function activate(node, source) {
    if (node.kind === UiKind.Toggle) {
        node.checked = !node.checked;
    } else if (node.kind === UiKind.Dropdown && !node.expanded) {
        node.expanded = node.options.length > 0;
    } else if (node.kind === UiKind.TabStrip && source === 'directional') {
        // Confirm on a tab strip is a no-op: left and right already moved it.
    }
}

function setSliderFromPointer(slider, point) {
    if (slider.rect.width <= 0) return;
    const f = clamp((point.x - slider.rect.x) / slider.rect.width, 0, 1);
    setValue(slider, slider.minValue + (slider.maxValue - slider.minValue) * f);
}

function setValue(node, value) {
    let min = node.minValue;
    let max = node.maxValue;
    if (max < min) [min, max] = [max, min];

    let v = value;
    if (node.step > 0) v = min + Math.round((v - min) / node.step) * node.step;
    node.value = clamp(v, min, max);
}

/** Whether a node and every ancestor of it is visible and interactive. */
function usable(node) {
    if (!node) return false;
    for (let n = node; n; n = n.parent) {
        if (!n.visible || !n.interactive) return false;
    }
    return true;
}

function selectTabAt(strip, point) {
    for (let i = 0; i < strip.options.length; i++) {
        if (rectContains(tabRect(strip, i), point)) { strip.selectedIndex = i; return; }
    }
}

function selectOptionAt(dropdown, point) {
    const list = dropdownListRect(dropdown);
    if (dropdown.rect.height <= 0 || !rectContains(list, point)) return;

    const index = Math.floor((point.y - list.y) / dropdown.rect.height);
    if (index < 0 || index >= dropdown.options.length) return;

    dropdown.selectedIndex = index;
    dropdown.expanded = false;
    dropdown.clicked = true;
}

function stepSelection(node, by) {
    const count = node.options.length;
    if (count === 0) return;
    node.selectedIndex = clamp(node.selectedIndex + by, 0, count - 1);
}

/** The first ancestor that scrolls and has something to scroll over. */
function nearestScrollable(from) {
    for (let n = from; n; n = n.parent) {
        if (n.scroll === ScrollMode.None) continue;
        const range = scrollRange(n);
        if (range.x > 0 || range.y > 0) return n;
    }
    return null;
}

function scrollRange(view) {
    return {
        x: Math.max(0, view.contentSize.x - view.contentRect.width),
        y: Math.max(0, view.contentSize.y - view.contentRect.height),
    };
}

/**
 * Clears the states that last exactly one frame, everywhere. `clicked` in
 * particular: a caller that reads it once a frame must never see the same click
 * twice, and a node that stopped being hit must stop reporting a hover.
 */
function clearTransient(node) {
    node.hovered = false;
    node.clicked = false;
    for (const child of node.children) clearTransient(child);
}

function syncFocusFlags(node, focused) {
    node.focused = node === focused;
    for (const child of node.children) syncFocusFlags(child, focused);
}

function clamp(v, min, max) { return Math.min(max, Math.max(min, v)); }
