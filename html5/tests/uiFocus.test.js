// -----------------------------------------------------------------------------
// The focus model, which is the whole reason a gamepad or a TV remote can drive a
// UI at all -- the engine had none, so nothing but a pointer could reach a widget.
// The mirror of SexyBiscuit.Tests/UiFocusTests.cs.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import { UiNode } from '../src/ui/UiNode.js';
import { measure, arrange } from '../src/ui/UiLayout.js';
import { UiFocus, UiInputModeTracker } from '../src/ui/UiFocus.js';
import {
    UiKind, SizeMode, LayoutMode, PositionMode, ScrollMode, UiAnchor,
    Focusability, UiInputMode,
} from '../src/ui/UiEnums.js';
import { NavDirection } from '../src/ui/UiNavigation.js';

function layout(root, width = 800, height = 600) {
    const area = { x: 0, y: 0, width, height };
    root.invalidateMeasure();
    measure(root, { x: width, y: height });
    arrange(root, area, area);
}

function button(name, x, y, w = 200, h = 40) {
    return new UiNode({
        name,
        kind: UiKind.Button,
        positioning: PositionMode.Absolute,
        offset: { x, y },
        widthMode: SizeMode.Fixed, width: w,
        heightMode: SizeMode.Fixed, height: h,
    });
}

// -----------------------------------------------------------------------------
// What can take focus
// -----------------------------------------------------------------------------

test('a label is not focusable but a button is', () => {
    const root = new UiNode();
    const label = root.add(new UiNode({ kind: UiKind.Label, name: 'title', text: 'Paused' }));
    const play = root.add(button('play', 0, 60));
    layout(root);

    assert.equal(UiFocus.isFocusable(label), false);
    assert.equal(UiFocus.isFocusable(play), true);
});

test('a button inside a hidden panel cannot take focus', () => {
    // Otherwise focus lands off-screen and the player is driving something invisible.
    const root = new UiNode();
    const panel = root.add(new UiNode({ name: 'panel', widthMode: SizeMode.Stretch, heightMode: SizeMode.Stretch }));
    const hidden = panel.add(button('hidden', 0, 0));
    layout(root);
    assert.equal(UiFocus.isFocusable(hidden), true);

    panel.visible = false;
    layout(root);
    assert.equal(UiFocus.isFocusable(hidden), false);
});

test('a row scrolled out of sight stays navigable even though it is not clickable', () => {
    // A pointer cannot reach what it cannot see, but a stick must still be able to
    // walk a long list -- the focus simply scrolls the row back into view.
    const root = new UiNode({ layout: LayoutMode.Column, scroll: ScrollMode.Vertical });
    const rows = [];
    for (let i = 0; i < 5; i++) {
        rows.push(root.add(new UiNode({
            name: `row${i}`, kind: UiKind.Button,
            widthMode: SizeMode.Fixed, width: 200,
            heightMode: SizeMode.Fixed, height: 80,
        })));
    }

    root.scrollOffset = { x: 0, y: 200 };
    layout(root, 400, 100);

    assert.ok(rows[0].rect.y < 0, 'the first row should be above the viewport');
    assert.equal(UiFocus.isFocusable(rows[0]), true);
});

// -----------------------------------------------------------------------------
// Scopes
// -----------------------------------------------------------------------------

test('a modal traps focus inside itself', () => {
    // A pause menu must not be able to lose the cursor to the HUD behind it. One flag
    // does it for every input class at once.
    const root = new UiNode();
    const hud = root.add(button('hudButton', 0, 0));

    const dialog = root.add(new UiNode({
        name: 'dialog', modal: true,
        positioning: PositionMode.Absolute, anchor: UiAnchor.Center,
        widthMode: SizeMode.Fixed, width: 300,
        heightMode: SizeMode.Fixed, height: 200,
        layout: LayoutMode.Column,
    }));
    const ok = dialog.add(new UiNode({
        name: 'ok', kind: UiKind.Button,
        widthMode: SizeMode.Fixed, width: 100, heightMode: SizeMode.Fixed, height: 40,
    }));
    dialog.add(new UiNode({
        name: 'cancel', kind: UiKind.Button,
        widthMode: SizeMode.Fixed, width: 100, heightMode: SizeMode.Fixed, height: 40,
    }));

    layout(root);
    const focus = new UiFocus(root);

    assert.equal(focus.activeScope, dialog);
    assert.ok(!focus.candidates().includes(hud));

    focus.focusFirst();
    assert.equal(focus.focused, ok);

    for (const d of [NavDirection.Up, NavDirection.Down, NavDirection.Left, NavDirection.Right]) {
        for (let i = 0; i < 4; i++) {
            focus.navigate(d);
            assert.notEqual(focus.focused, hud, 'focus escaped the modal');
        }
    }

    dialog.visible = false;
    layout(root);
    assert.equal(focus.activeScope, root);
});

// -----------------------------------------------------------------------------
// Moving focus
// -----------------------------------------------------------------------------

test('navigating a column walks it in order and stops at the end', () => {
    const root = new UiNode();
    const a = root.add(button('a', 0, 0));
    const b = root.add(button('b', 0, 60));
    const c = root.add(button('c', 0, 120));
    layout(root);

    const focus = new UiFocus(root);
    focus.focusFirst();
    assert.equal(focus.focused, a);

    assert.equal(focus.navigate(NavDirection.Down), true);
    assert.equal(focus.focused, b);

    assert.equal(focus.navigate(NavDirection.Down), true);
    assert.equal(focus.focused, c);

    // Nothing below the last one, so nothing happens rather than wrapping surprisingly.
    assert.equal(focus.navigate(NavDirection.Down), false);
    assert.equal(focus.focused, c);

    assert.equal(focus.navigate(NavDirection.Up), true);
    assert.equal(focus.focused, b);
});

test('an override chain hops over a link that cannot take focus', () => {
    // An explicit order should survive one of its items being disabled, without the
    // author writing any conditionals.
    const root = new UiNode();
    const a = root.add(button('a', 0, 0));
    const b = root.add(button('b', 0, 60));
    const c = root.add(button('c', 0, 120));

    a.navDown = 'b';
    b.navDown = 'c';
    layout(root);

    const focus = new UiFocus(root);
    focus.focus(a);

    assert.equal(focus.navigate(NavDirection.Down), true);
    assert.equal(focus.focused, b);

    focus.focus(a);
    b.focusable = Focusability.No;
    assert.equal(focus.navigate(NavDirection.Down), true);
    assert.equal(focus.focused, c);
});

test('losing the focused node moves to the nearest one rather than the first', () => {
    // Deleting a row near the bottom of a list must not teleport the player back to
    // the top of it, which is what "focus the first child" would do.
    const root = new UiNode();
    root.add(button('first', 0, 0));
    const fourth = root.add(button('fourth', 0, 180));
    const fifth = root.add(button('fifth', 0, 240));
    layout(root);

    const focus = new UiFocus(root);
    focus.focus(fifth);

    fifth.visible = false;
    layout(root);

    assert.equal(focus.repair(), true);
    assert.equal(focus.focused, fourth);
});

test('initial focus follows reading order rather than the order nodes were added', () => {
    const root = new UiNode();
    root.add(button('rightColumnTop', 400, 0, 150, 40));
    const topLeft = root.add(button('leftColumnTop', 0, 0, 150, 40));
    root.add(button('leftColumnNext', 0, 60, 150, 40));
    layout(root);

    const focus = new UiFocus(root);
    focus.focusFirst();

    assert.equal(focus.focused, topLeft);
});

// -----------------------------------------------------------------------------
// Input mode
// -----------------------------------------------------------------------------

test('a thumbstick resting off centre engages once and then stays quiet', () => {
    // A worn stick must not make the focus ring appear while somebody uses the mouse.
    const modes = new UiInputModeTracker();
    modes.noteMouseButton();
    assert.equal(modes.mode, UiInputMode.Pointer);

    assert.equal(modes.noteNavigationAxis(0.6), false);  // first frame above: not yet believed
    assert.equal(modes.noteNavigationAxis(0.6), true);   // second frame: believed
    assert.equal(modes.mode, UiInputMode.Directional);

    modes.noteMouseButton();
    assert.equal(modes.mode, UiInputMode.Pointer);

    // Still resting at 0.6 and never released: it must not claim the mode again.
    for (let i = 0; i < 60; i++) assert.equal(modes.noteNavigationAxis(0.6), false);
    assert.equal(modes.mode, UiInputMode.Pointer);
});

test('a decisive push is believed on the first frame', () => {
    const modes = new UiInputModeTracker();
    assert.equal(modes.noteNavigationAxis(0.9), true);
    assert.equal(modes.mode, UiInputMode.Directional);
});

test('a jittering mouse never outruns the decay but a real movement does', () => {
    const modes = new UiInputModeTracker();
    modes.noteNavigationButton();
    assert.equal(modes.mode, UiInputMode.Directional);

    for (let i = 0; i < 120; i++) {
        modes.tick(1 / 60);
        assert.equal(modes.noteMouseMotion({ x: 0.5, y: 0 }), false);
    }
    assert.equal(modes.mode, UiInputMode.Directional);

    // Somebody actually reaching for the mouse crosses in a couple of frames.
    modes.noteMouseMotion({ x: 6, y: 0 });
    assert.equal(modes.noteMouseMotion({ x: 6, y: 0 }), true);
    assert.equal(modes.mode, UiInputMode.Pointer);
});

test('the ring and the hover belong to different devices', () => {
    const modes = new UiInputModeTracker();

    modes.noteMouseButton();
    assert.equal(modes.showHover, true);
    assert.equal(modes.showFocusRing, false);

    modes.noteNavigationButton();
    assert.equal(modes.showHover, false);
    assert.equal(modes.showFocusRing, true);

    modes.noteTouch();
    assert.equal(modes.showHover, false);
    assert.equal(modes.showFocusRing, false);
});

test('a mouse event just after a touch is ignored', () => {
    const modes = new UiInputModeTracker();
    modes.noteTouch();

    modes.tick(0.1);
    assert.equal(modes.noteMouseMotion({ x: 50, y: 0 }), false);
    assert.equal(modes.mode, UiInputMode.Touch);

    modes.tick(1);
    assert.equal(modes.noteMouseMotion({ x: 50, y: 0 }), true);
    assert.equal(modes.mode, UiInputMode.Pointer);
});
