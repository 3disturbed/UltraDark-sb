// -----------------------------------------------------------------------------
// UiFocus — who has focus, which subtree they are trapped in, and which way is
// "next". The mirror of SexyBiscuit.Engine/UI/Focus/UiFocus.cs.
//
// This is what makes a gamepad, a D-pad and a TV remote able to drive a UI at all:
// the engine had no focus model of any kind, so nothing but a pointer could reach
// a widget.
//
// Scopes are derived from the tree rather than kept in a stack the caller has to
// maintain. A stack and a tree get out of step the first time something is
// destroyed while a dialog is open, and then focus escapes to a panel that is no
// longer on screen.
// -----------------------------------------------------------------------------

import { UiKind, Focusability, UiInputMode } from './UiEnums.js';
import { find as navigateFrom, NavDirection } from './UiNavigation.js';
import { paintOrder } from './UiLayout.js';

export class UiFocus {
    constructor(root) {
        this.root = root;
        this.focused = null;
        this.modes = new UiInputModeTracker();
    }

    /** Which class of device the player is currently driving the UI with. */
    get mode() { return this.modes.mode; }

    // -------------------------------------------------------------------------
    // Scopes
    // -------------------------------------------------------------------------

    /**
     * The subtree focus is currently confined to: the top-most visible modal, or the
     * whole tree when there is none.
     */
    get activeScope() {
        let scope = this.root;
        for (const node of inPaintOrder(this.root)) {
            if (node.modal && isVisibleInHierarchy(node)) scope = node;
        }
        return scope;
    }

    // -------------------------------------------------------------------------
    // Focusability
    // -------------------------------------------------------------------------

    /**
     * Whether a node can hold focus right now.
     *
     * Being scrolled out of sight deliberately does NOT disqualify a node. A clipped node
     * is not clickable, because a pointer cannot reach what it cannot see, but it must
     * stay navigable or a long list would be impossible to move through with a stick --
     * the focus simply scrolls it back into view.
     */
    static isFocusable(node) {
        if (node.focusable === Focusability.No) return false;
        if (!isVisibleInHierarchy(node)) return false;
        if (node.rect.width <= 0 || node.rect.height <= 0) return false;

        return node.focusable === Focusability.Yes || kindTakesFocus(node.kind);
    }

    /** Everything inside the current scope that could take focus. */
    candidates() {
        const scope = this.activeScope;
        const found = [];

        if (UiFocus.isFocusable(scope)) found.push(scope);
        for (const node of inPaintOrder(scope)) {
            if (UiFocus.isFocusable(node)) found.push(node);
        }
        return found;
    }

    // -------------------------------------------------------------------------
    // Moving focus
    // -------------------------------------------------------------------------

    /** Gives focus to a node, or clears it when handed null. Returns whether it moved. */
    focus(node) {
        if (this.focused === node) return false;
        if (node && !UiFocus.isFocusable(node)) return false;

        this.focused = node ?? null;
        return true;
    }

    /**
     * Focuses whatever a scope should start on: an explicit request, else the first
     * candidate in reading order -- because a two-column dialog built column by column
     * would otherwise open with the top of the second column focused.
     */
    focusFirst() {
        const candidates = this.candidates();
        if (candidates.length === 0) return this.focus(null);

        for (const node of candidates) {
            if (node.autoFocus) return this.focus(node);
        }
        return this.focus(inReadingOrder(candidates, this.activeScope)[0]);
    }

    /** Moves focus one step in a direction. Returns whether anything moved. */
    navigate(direction) {
        if (!this.focused || !UiFocus.isFocusable(this.focused)) return this.focusFirst();

        const candidates = this.candidates().filter((n) => n !== this.focused);
        if (candidates.length === 0) return false;

        const explicit = this._resolveOverride(this.focused, direction, candidates);
        if (explicit) return this.focus(explicit);

        const index = navigateFrom(this.focused.rect, candidates.map((n) => n.rect), direction);
        return index >= 0 ? this.focus(candidates[index]) : false;
    }

    /**
     * Follows a chain of explicit overrides, skipping links that are not focusable.
     *
     * Hopping onwards rather than giving up is what lets an author wire a fixed order
     * once and have it survive an item being disabled, without writing any conditionals.
     * The hop limit stops a cycle of dead links spinning forever.
     */
    _resolveOverride(from, direction, candidates) {
        let current = from;

        for (let hop = 0; hop < 8; hop++) {
            let name = '';
            if (direction === NavDirection.Up) name = current.navUp;
            else if (direction === NavDirection.Down) name = current.navDown;
            else if (direction === NavDirection.Left) name = current.navLeft;
            else if (direction === NavDirection.Right) name = current.navRight;

            if (!name) return null;

            const target = this.activeScope.find(name);
            if (!target) return null;
            if (candidates.includes(target)) return target;

            current = target;
        }
        return null;
    }

    /**
     * Puts focus somewhere sensible after the focused node was hidden or destroyed:
     * the nearest remaining candidate by plain centre distance, with no direction
     * involved. "The first child" would teleport the player to the top of a list every
     * time they deleted a row near the bottom of it.
     */
    repair() {
        if (this.focused && UiFocus.isFocusable(this.focused)) return false;

        // Nothing has ever held focus here, so this is a scope opening rather than a
        // repair: autoFocus and plain reading order decide, not the geometric accident
        // of which control happens to sit nearest the middle of the screen.
        if (!this.focused) return this.focusFirst();

        const was = centreOf(this.focused.rect);
        const candidates = this.candidates();
        if (candidates.length === 0) return this.focus(null);

        let nearest = null;
        let best = Infinity;

        for (const node of candidates) {
            const c = centreOf(node.rect);
            const distance = (c.x - was.x) ** 2 + (c.y - was.y) ** 2;
            if (distance >= best) continue;
            best = distance;
            nearest = node;
        }

        this.focused = nearest;
        return true;
    }
}

// -----------------------------------------------------------------------------
// Input mode
// -----------------------------------------------------------------------------

/**
 * Decides which class of device is driving the UI, and resists changing its mind.
 *
 * Without hysteresis this thrashes: a worn thumbstick resting at 0.4 makes the focus
 * ring flicker on while somebody is using the mouse, and a jittery mouse or a trackpad
 * resting under a palm steals the mode back from a gamepad. So a switch needs a
 * deliberate event -- travel that outruns a decay, or an axis that actually crosses a
 * threshold from below it -- never a level that merely happens to be high.
 */
export class UiInputModeTracker {
    constructor() {
        this.mode = UiInputMode.Pointer;

        /** Canvas units the mouse must travel, against the decay, to claim the mode. */
        this.mouseWakeDistance = 8;
        /** How fast accumulated mouse travel bleeds away, in canvas units a second. */
        this.mouseTravelDecay = 40;
        /** An axis must reach this to count as pushed. */
        this.engageThreshold = 0.5;
        /** And must fall below this before it can engage again. */
        this.releaseThreshold = 0.35;
        /** A shove this hard is believed immediately, without waiting a second frame. */
        this.decisiveMagnitude = 0.75;
        /** How long after a touch the mouse is ignored, so a stylus or palm cannot flip back. */
        this.touchLockoutSeconds = 0.5;

        this._travel = 0;
        this._sinceTouch = Infinity;
        this._axisEngaged = false;
        this._framesAbove = 0;
    }

    /** True when the ring should be drawn -- only ever under directional input. */
    get showFocusRing() { return this.mode === UiInputMode.Directional; }

    /** True when hover states apply -- a finger has no hover to show. */
    get showHover() { return this.mode === UiInputMode.Pointer; }

    tick(dt) {
        this._travel = Math.max(0, this._travel - this.mouseTravelDecay * dt);
        if (this._sinceTouch < Infinity) this._sinceTouch += dt;
    }

    /** Mouse movement. Jitter never outruns the decay; a real hand movement does. */
    noteMouseMotion(delta) {
        if (this._sinceTouch < this.touchLockoutSeconds) return false;

        this._travel += Math.hypot(delta.x ?? 0, delta.y ?? 0);
        if (this._travel < this.mouseWakeDistance) return false;

        this._travel = 0;
        return this._switchTo(UiInputMode.Pointer);
    }

    /** A click or a wheel notch is unambiguous, so it bypasses the accumulator. */
    noteMouseButton() { return this._switchTo(UiInputMode.Pointer); }

    noteTouch() {
        this._sinceTouch = 0;
        return this._switchTo(UiInputMode.Touch);
    }

    /**
     * A navigation axis this frame. Only a crossing counts, so a stick resting past the
     * threshold engages once and then has to be released before it can engage again.
     */
    noteNavigationAxis(magnitude) {
        const m = Math.abs(magnitude);

        if (m < this.releaseThreshold) {
            this._axisEngaged = false;
            this._framesAbove = 0;
            return false;
        }

        if (m < this.engageThreshold || this._axisEngaged) return false;

        this._framesAbove++;
        if (this._framesAbove < 2 && m < this.decisiveMagnitude) return false;

        this._axisEngaged = true;
        return this._switchTo(UiInputMode.Directional);
    }

    /** A button press from a keyboard or pad, which is never ambiguous. */
    noteNavigationButton() { return this._switchTo(UiInputMode.Directional); }

    _switchTo(mode) {
        if (this.mode === mode) return false;
        this.mode = mode;
        return true;
    }
}

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

function kindTakesFocus(kind) {
    return kind === UiKind.Button || kind === UiKind.Slider || kind === UiKind.Toggle
        || kind === UiKind.TextField || kind === UiKind.Dropdown || kind === UiKind.TabStrip;
}

function isVisibleInHierarchy(node) {
    for (let n = node; n; n = n.parent) {
        if (!n.visible || !n.interactive || n.opacity <= 0.01) return false;
    }
    return true;
}

/**
 * Left to right, top to bottom, with rows banded so that items which look level are
 * treated as level even when their tops differ by a pixel or two.
 */
function inReadingOrder(nodes, scope) {
    const band = Math.max(1, scope.rect.height / 8);
    return nodes.slice().sort((a, b) => {
        const rowA = Math.floor(a.rect.y / band);
        const rowB = Math.floor(b.rect.y / band);
        if (rowA !== rowB) return rowA - rowB;
        return a.rect.x - b.rect.x;
    });
}

/** Every descendant, in the order they are painted, so the last one is on top. */
function* inPaintOrder(node) {
    for (const child of paintOrder(node)) {
        yield child;
        yield* inPaintOrder(child);
    }
}

function centreOf(r) { return { x: r.x + r.width * 0.5, y: r.y + r.height * 0.5 }; }
