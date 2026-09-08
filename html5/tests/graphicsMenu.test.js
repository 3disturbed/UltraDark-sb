// -----------------------------------------------------------------------------
// The graphics menu, driven the way a player drives it.
//
// The mirror of SexyBiscuit.Tests/GraphicsMenuTests.cs. Both suites build the menu
// from html5/src/ui/graphics-menu.json and click the same controls, so a row that
// appears on one engine and not the other fails here.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import { UiCanvas } from '../src/ui/UiCanvas.js';
import { GraphicsMenu } from '../src/ui/GraphicsMenu.js';
import { GraphicsSettings } from '../src/rendering/GraphicsSettings.js';
import { GraphicsCapabilities } from '../src/rendering/GraphicsCapabilities.js';
import { VERSION } from '../src/core/EngineInfo.js';
import { UiKind } from '../src/ui/UiEnums.js';
import schema from '../src/ui/graphics-menu.json' with { type: 'json' };

/** A desktop-shaped machine: everything the native engine can do. */
function desktopCaps() {
    return new GraphicsCapabilities({
        renderer: 'MonoGame DesktopGL', adapter: 'Test Adapter',
        shadows: true, postProcessing: true, lighting2D: true,
        anisotropy: true, displayControl: true, pixelRatio: false, webgl2: false,
    });
}

/** A browser-shaped machine: no shadow pass, no post chain, no 2D light map. */
function webCaps() {
    return new GraphicsCapabilities({
        renderer: 'WebGL2', adapter: 'Test GPU', webgl2: true,
        shadows: false, postProcessing: false, lighting2D: false,
        anisotropy: true, displayControl: false, pixelRatio: true,
    });
}

function openMenu(caps = desktopCaps()) {
    UiCanvas.clearAll();

    const settings = new GraphicsSettings();
    settings.applyPreset('medium');

    const menu = new GraphicsMenu(settings, caps);
    menu.open();
    menu.canvas.setViewport(1280, 720);
    menu.canvas.layout();
    return menu;
}

/** One frame: lay out, feed input, then let the menu read what happened. */
function frame(menu, input) {
    menu.canvas.layout();
    menu.canvas.input.update(input ?? {});
    menu.tick();
}

/**
 * Switches tab and lets the rebuilt rows get rectangles.
 *
 * Two frames, because that is what really happens: tick() rebuilds the body when the
 * strip's selection moves, and the new rows are not laid out until the next frame's
 * layout pass. A test that clicked immediately would be clicking at (0, 0).
 */
function showTab(menu, id) {
    menu._tabs.selectedIndex = tabIndex(id);
    frame(menu);
    frame(menu);
}

/** A press and a release on a node's centre, which is one click. */
function click(menu, node) {
    const at = { x: node.rect.x + node.rect.width / 2, y: node.rect.y + node.rect.height / 2 };
    const screen = menu.canvas.canvasToScreen(at);
    frame(menu, { pointer: screen, pointerDown: true });
    frame(menu, { pointer: screen, pointerDown: false });
}

// -----------------------------------------------------------------------------

test('the menu carries the engine version on a line at its foot', () => {
    // The whole reason the status line exists, and the one thing asked for by name.
    const menu = openMenu();
    const status = menu.canvas.find('status');

    assert.ok(status, 'the menu has no status line');
    assert.match(status.text, new RegExp(VERSION.replaceAll('.', '\\.')));
    assert.match(status.text, /SexyBiscuit/);
    assert.match(status.text, /Preset: Medium/);

    // And it is the last thing in the panel, so it stays at the foot on every tab.
    const panel = menu.canvas.find('panel');
    assert.equal(panel.children.at(-2), status, 'the status line has drifted off the foot');
    UiCanvas.clearAll();
});

test('the status line follows the preset without being rebuilt', () => {
    const menu = openMenu();
    menu.settings.applyPreset('ultra');
    menu.refresh();
    assert.match(menu.canvas.find('status').text, /Preset: Ultra/);

    menu.settings.preset = 'custom';
    menu.refresh();
    assert.match(menu.canvas.find('status').text, /Preset: Custom/);
    UiCanvas.clearAll();
});

test('clicking a preset applies every one of its values', () => {
    const menu = openMenu();
    const battery = menu.canvas.find('preset:battery');
    assert.ok(battery, 'the Presets tab has no Battery Saver button');

    let announced = null;
    menu.onChanged = (s) => { announced = s; };
    click(menu, battery);

    assert.equal(menu.settings.preset, 'battery');
    assert.equal(menu.settings.renderScale, 0.5);
    assert.equal(menu.settings.shadows, 'Off');
    assert.equal(announced, menu.settings, 'the change was not announced');
    UiCanvas.clearAll();
});

test('every tab in the description can be opened and builds its rows', () => {
    const menu = openMenu();

    for (let i = 0; i < schema.tabs.length; i++) {
        menu._tabs.selectedIndex = i;
        frame(menu);
        assert.ok(menu._body.children.length > 0,
            `the "${schema.tabs[i].label}" tab built no rows`);
    }
    UiCanvas.clearAll();
});

test('a row the platform cannot honour is absent, not merely dead', () => {
    // A control that is visibly present and permanently dead reads as a bug, and the
    // browser renderer genuinely has no shadow pass to attach one to.
    const web = openMenu(webCaps());
    showTab(web, 'quality');
    assert.equal(web.canvas.find('shadows'), null, 'the web build offers a shadow control');
    assert.equal(web.canvas.find('lighting2D'), null, 'the web build offers a 2D lighting control');
    assert.ok(web.canvas.find('textureFiltering'), 'texture filtering should survive everywhere');
    UiCanvas.clearAll();

    const desktop = openMenu(desktopCaps());
    showTab(desktop, 'quality');
    assert.ok(desktop.canvas.find('shadows'), 'the desktop build has no shadow control');
    UiCanvas.clearAll();
});

test('each platform gets its own advanced knobs and not the other one\'s', () => {
    const web = openMenu(webCaps());
    showTab(web, 'advanced');
    assert.ok(web.canvas.find('shaderPrecision'), 'the web build has no shader precision control');
    assert.ok(web.canvas.find('powerPreference'), 'the web build has no GPU preference control');
    assert.equal(web.canvas.find('msaa'), null, 'the web build offers native multisampling');
    UiCanvas.clearAll();

    const desktop = openMenu(desktopCaps());
    showTab(desktop, 'advanced');
    assert.ok(desktop.canvas.find('msaa'), 'the desktop build has no multisampling control');
    assert.equal(desktop.canvas.find('shaderPrecision'), null, 'the desktop build offers a web-only knob');
    UiCanvas.clearAll();
});

test('dragging a slider writes the value through to the settings', () => {
    const menu = openMenu();
    showTab(menu, 'display');

    const slider = menu.canvas.find('renderScale');
    assert.ok(slider, 'the Display tab has no render scale slider');
    assert.equal(slider.kind, UiKind.Slider);

    // Just inside the end of the track. The last pixel belongs to no rectangle on
    // either engine -- rects are half-open so a pointer on a seam cannot hit two
    // adjacent controls -- and the step rounds the rest of the way to the maximum.
    const at = { x: slider.rect.x + slider.rect.width - 1, y: slider.rect.y + slider.rect.height / 2 };
    frame(menu, { pointer: menu.canvas.canvasToScreen(at), pointerDown: true });

    assert.equal(menu.settings.renderScale, 2);
    assert.equal(menu.settings.preset, 'custom', 'a hand-edited value is no longer a preset');
    UiCanvas.clearAll();
});

test('a slider shows its own value, because a bare handle says nothing', () => {
    const menu = openMenu();
    showTab(menu, 'display');

    const binding = menu._bindings.find((b) => b.row?.field === 'renderScale');
    assert.ok(binding.readout, 'the render scale slider has no readout');
    assert.equal(binding.readout.text, '100%', 'medium renders at full scale');
    UiCanvas.clearAll();
});

test('a toggle flips the setting it is bound to', () => {
    const menu = openMenu();
    showTab(menu, 'display');

    const vsync = menu.canvas.find('vsync');
    assert.ok(vsync, 'the Display tab has no V-Sync toggle');
    assert.equal(menu.settings.vsync, true);

    click(menu, vsync);
    assert.equal(menu.settings.vsync, false);
    UiCanvas.clearAll();
});

test('an integer setting never picks up a fraction from its control', () => {
    // A shadow map of 1023.9997 is a texture allocation the driver refuses.
    const menu = openMenu();
    showTab(menu, 'advanced');

    const lights = menu.canvas.find('maxLightsPerObject');
    const at = { x: lights.rect.x + lights.rect.width * 0.63, y: lights.rect.y + lights.rect.height / 2 };
    frame(menu, { pointer: menu.canvas.canvasToScreen(at), pointerDown: true });

    assert.ok(Number.isInteger(menu.settings.maxLightsPerObject),
        `maxLightsPerObject became ${menu.settings.maxLightsPerObject}`);
    UiCanvas.clearAll();
});

test('a settled menu reports no change, so it does not rewrite itself every frame', () => {
    // A strict comparison would see every dropdown as changed on every frame and the
    // menu would re-lay itself out sixty times a second.
    const menu = openMenu();
    showTab(menu, 'quality');

    let changes = 0;
    menu.onChanged = () => { changes++; };
    for (let i = 0; i < 5; i++) frame(menu);

    assert.equal(changes, 0);
    assert.equal(menu.settings.preset, 'medium');
    UiCanvas.clearAll();
});

test('close hides the menu and says so once', () => {
    const menu = openMenu();
    let closed = 0;
    menu.onClosed = () => { closed++; };

    click(menu, menu.canvas.find('close'));
    assert.equal(closed, 1);
    assert.equal(menu.isOpen, false);
    assert.equal(menu.canvas.interactive, false, 'a hidden menu must not still take input');
    UiCanvas.clearAll();
});

test('the benchmark button asks its owner to run one rather than running it itself', () => {
    const menu = openMenu();
    showTab(menu, 'benchmark');

    let asked = 0;
    menu.onBenchmark = () => { asked++; };
    click(menu, menu.canvas.find('benchmark'));

    assert.equal(asked, 1);
    UiCanvas.clearAll();
});

// -----------------------------------------------------------------------------

function tabIndex(id) {
    const index = schema.tabs.findIndex((t) => t.id === id);
    assert.notEqual(index, -1, `the description has no "${id}" tab`);
    return index;
}
