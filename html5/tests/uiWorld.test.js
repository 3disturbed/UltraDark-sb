// -----------------------------------------------------------------------------
// Holds the browser's world-space UI geometry to the same golden cases the C#
// engine reads from the same file (SexyBiscuit.Tests/UiWorldTests.cs).
//
// World UI is basis vectors and ray intersections all the way down, and the
// regex-over-the-other-engine's-source parity tests can see a missing member but
// never a number that is quietly wrong. A canvas mirrored left-to-right would
// still face the camera; a canvas flipped top-to-bottom would still be hit-tested
// self-consistently. Only a fixture both engines read catches those.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { Vector3 } from '../src/math/Vector3.js';
import { Matrix4 } from '../src/math/Matrix4.js';
import { build, rayToCanvas, rayDistance, canvasToWorld } from '../src/ui/UiWorld.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(fs.readFileSync(path.join(here, 'fixtures/ui-world-cases.json'), 'utf8'));

const TOLERANCE = 1e-4;

const v3 = ([x, y, z]) => new Vector3(x, y, z);
const xy = ([x, y]) => ({ x, y });

function basisFor(c) {
    const camera = c.camera;
    const view = Matrix4.lookAt(v3(camera.position), v3(camera.target), v3(camera.up));

    return build(
        v3(c.canvas.anchor),
        c.canvas.facing,
        quaternion(c.canvas.planeRotation),
        view,
        v3(camera.position),
        xy(c.canvas.worldSize));
}

function quaternion(q) {
    return q ? { x: q[0], y: q[1], z: q[2], w: q[3] } : { x: 0, y: 0, z: 0, w: 1 };
}

function close(actual, expected, what) {
    assert.ok(Math.abs(actual - expected) < TOLERANCE,
        `${what}: expected ${expected}, got ${actual}`);
}

function closeVector(actual, expected, what) {
    close(actual.x, expected[0], `${what}.x`);
    close(actual.y, expected[1], `${what}.y`);
    close(actual.z, expected[2], `${what}.z`);
}

for (const c of fixture.cases) {
    test(`world case: ${c.name}`, () => {
        const basis = basisFor(c);
        const canvasSize = xy(c.canvas.canvasSize);

        if (c.expectBasis) {
            closeVector(basis.right,  c.expectBasis.right,  'right');
            closeVector(basis.up,     c.expectBasis.up,     'up');
            closeVector(basis.normal, c.expectBasis.normal, 'normal');
        }

        if (c.canvasToWorld) {
            const world = canvasToWorld(basis, xy(c.canvasToWorld), canvasSize);
            closeVector(world, c.expect.world, 'world');
        }

        if (c.ray) {
            const origin = v3(c.ray.origin);
            const direction = v3(c.ray.direction).normalize();

            const point = rayToCanvas(basis, origin, direction, canvasSize);

            if (c.expect.miss) {
                assert.equal(point, null, 'expected the ray to miss the plane');
                assert.equal(rayDistance(basis, origin, direction, canvasSize), null);
                return;
            }

            assert.ok(point, 'expected the ray to hit the plane');
            close(point.x, c.expect.canvas[0], 'canvas.x');
            close(point.y, c.expect.canvas[1], 'canvas.y');

            const distance = rayDistance(basis, origin, direction, canvasSize);
            if (c.expect.distance === null) assert.equal(distance, null);
            else if (c.expect.distance !== undefined) close(distance, c.expect.distance, 'distance');
        }
    });
}

// -----------------------------------------------------------------------------
// The behaviour that makes a saved widget usable in either space.
// Twin of SexyBiscuit.Tests/UiWorldTests.cs's UiSpaceTests.
// -----------------------------------------------------------------------------

import { UiCanvas } from '../src/ui/UiCanvas.js';
import { UiNode } from '../src/ui/UiNode.js';
import { UiKind, UiSpace, UiScaleMode, PositionMode, UiDocumentError } from '../src/ui/UiEnums.js';
import { fromJson, toJson } from '../src/ui/UiDocument.js';

const DOCUMENT = `{
  "name": "panel",
  "layout": "column",
  "gap": 8,
  "padding": 12,
  "background": "#161920e6",
  "children": [
    { "name": "title", "kind": "label", "text": "TERMINAL", "scale": 2 },
    { "name": "go", "kind": "button", "text": "Engage", "width": "*" }
  ]
}`;

test('one document adopted into either space produces the same tree', () => {
    // The whole promise of putting the space on the canvas rather than in the document.
    // A widget saved once has to be usable on the screen and on a wall, and the only way to
    // say so is to adopt the same source into both and compare what came out.
    UiCanvas.clearAll();

    const screen = new UiCanvas();
    screen.space = UiSpace.Screen;

    const world = new UiCanvas();
    world.space = UiSpace.World;
    world.referenceResolution = { x: 400, y: 200 };

    screen.adopt(fromJson(DOCUMENT));
    world.adopt(fromJson(DOCUMENT));

    assert.equal(toJson(screen.root), toJson(world.root));
    assert.equal(world.root.name, 'panel');
    assert.ok(world.find('go'));

    UiCanvas.clearAll();
});

test('a world canvas is its reference resolution whatever the window is', () => {
    // A canvas standing in the scene is a fixed sheet, not something fitted to a window. If
    // the viewport leaked into its size, the same wall-mounted UI would be laid out differently
    // on a phone and a monitor.
    UiCanvas.clearAll();

    const canvas = new UiCanvas();
    canvas.space = UiSpace.World;
    canvas.scaleMode = UiScaleMode.ScaleToFit;     // ignored in world space, deliberately
    canvas.referenceResolution = { x: 400, y: 200 };

    canvas.setViewport(1920, 1080);
    canvas.layout();
    assert.deepEqual(canvas.canvasSize, { x: 400, y: 200 });
    assert.deepEqual(canvas.scale, { x: 1, y: 1 });

    canvas.setViewport(640, 360);
    canvas.layout();
    assert.deepEqual(canvas.canvasSize, { x: 400, y: 200 });
    assert.deepEqual(canvas.canvasOffset, { x: 0, y: 0 });

    UiCanvas.clearAll();
});

test('pixelsPerUnit is the only thing that sizes a world canvas', () => {
    UiCanvas.clearAll();

    const canvas = new UiCanvas();
    canvas.space = UiSpace.World;
    canvas.referenceResolution = { x: 400, y: 200 };
    canvas.pixelsPerUnit = 100;

    canvas.layout();
    assert.deepEqual(canvas.worldSize, { x: 4, y: 2 });

    canvas.pixelsPerUnit = 200;
    assert.deepEqual(canvas.worldSize, { x: 2, y: 1 });

    UiCanvas.clearAll();
});

test('a pointer that meets no world plane hits nothing rather than the nearest edge', () => {
    // The miss has to be a coordinate the ordinary rectangle test rejects, and it has to be
    // finite. An infinity would reach UiInput's slider arithmetic as a NaN and stop being a
    // miss at all.
    UiCanvas.clearAll();

    const canvas = new UiCanvas();
    canvas.space = UiSpace.World;
    canvas.referenceResolution = { x: 400, y: 200 };
    canvas.layout();

    // No Camera3D exists in this test, so every ray misses by construction.
    const point = canvas.screenToCanvas({ x: 640, y: 360 });

    assert.deepEqual(point, { ...UiCanvas.NOWHERE });
    assert.ok(Number.isFinite(point.x) && Number.isFinite(point.y));
    assert.equal(canvas.hitTest({ x: 640, y: 360 }), null);

    UiCanvas.clearAll();
});

test('a following node is hidden when there is no camera to project through', () => {
    // Without a camera the projection has no answer, and the wrong answer is to leave the
    // marker where it was -- which parks a damage number over the wrong enemy for good.
    UiCanvas.clearAll();

    const canvas = new UiCanvas();
    const marker = new UiNode({ kind: UiKind.Label, text: '12' });
    marker.worldFollow = true;
    canvas.root.add(marker);

    canvas.setViewport(1280, 720);
    canvas.layout();

    assert.equal(marker.visible, false);
    assert.equal(marker.positioning, PositionMode.Absolute);

    UiCanvas.clearAll();
});

test('following survives the document round trip', () => {
    // A marker authored in a .ui file has to come back a marker. The three properties go
    // through the codec's own key list, so this also proves the list was extended.
    const node = fromJson('{ "kind": "label", "worldFollow": true, "worldAnchor": [1, 2, 3], "worldFollowDistance": 40 }');

    assert.equal(node.worldFollow, true);
    assert.deepEqual(node.worldAnchor, { x: 1, y: 2, z: 3 });
    assert.equal(node.worldFollowDistance, 40);

    assert.match(toJson(node).replace(/ /g, ''), /"worldAnchor":\[1,2,3\]/);
});

test('a world anchor needs three numbers on both engines', () => {
    // Two numbers is a typo with a plausible result. The native codec throws here for the same
    // reason, so a mistake cannot mean different things on the two engines.
    assert.throws(() => fromJson('{ "worldAnchor": [1, 2] }'), UiDocumentError);
});
