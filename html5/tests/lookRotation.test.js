// -----------------------------------------------------------------------------
// Which way a 3D thing ends up facing.
//
// `Quaternion.lookRotation` built its basis as (right, up, -forward) with
// `right = cross(up, forward)` and `up = cross(forward, right)`. That is a
// LEFT-handed set: the determinant is -1 for every input, whichever way `right`
// is taken, so the matrix is a reflection rather than a rotation. The extraction
// below it then reads a non-unit quaternion out of it and returns a 180-degree
// roll about Z that ignores `forward` completely.
//
// Nothing threw, nothing warned, and nothing in this repository noticed for as
// long as the function has existed -- because until Jake01 went 3D, nothing had
// ever aimed a camera with it. The bundled 3D Scene template positions its
// orbit camera and then carries a comment where the lookAt should be. A camera
// asked to look down at the ground kept staring at the horizon.
//
// The native side was always right: it defers to MonoGame's Matrix.CreateWorld.
// So this is also a parity test, and `Transform3DLookAtTests` is its twin.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import { Vector3, Quaternion } from '../src/math/index.js';
import { Scene, Actor } from '../src/index.js';
import { Transform3D } from '../src/core/Transform3D.js';

/** The forward axis a rotation produces, which is what a camera actually uses. */
function forwardOf(q) {
    const t = new Transform3D();
    t.localRotation = q;
    return t.forward;
}

const DIRECTIONS = [
    ['level, north',      [0, 0, -1]],
    ['level, east',       [1, 0, 0]],
    ['down at 65 degrees', [0, -0.903, -0.429]],
    ['down at 45',        [0.5, -0.707, -0.5]],
    ['up steeply',        [0.2, 0.9, 0.35]],
    ['straight down',     [0, -1, 0]],
    ['straight up',       [0, 1, 0]],
];

for (const [name, dir] of DIRECTIONS) {
    test(`lookRotation faces ${name}`, () => {
        const want = new Vector3(...dir).normalize();
        const got = forwardOf(Quaternion.lookRotation(want, Vector3.up));

        assert.ok(Math.abs(got.x - want.x) < 1e-4 &&
                  Math.abs(got.y - want.y) < 1e-4 &&
                  Math.abs(got.z - want.z) < 1e-4,
            `forward should be (${want.x.toFixed(3)}, ${want.y.toFixed(3)}, ${want.z.toFixed(3)}) ` +
            `but is (${got.x.toFixed(3)}, ${got.y.toFixed(3)}, ${got.z.toFixed(3)})`);
    });
}

test('the basis it builds is right-handed', () => {
    // The determinant is the thing that was wrong, so it is the thing to check.
    // A reflection still produces a plausible-looking unit-ish quaternion, which
    // is exactly why the bug was invisible.
    for (const [, dir] of DIRECTIONS) {
        const f = new Vector3(...dir).normalize();
        const t = new Transform3D();
        t.localRotation = Quaternion.lookRotation(f, Vector3.up);

        const r = t.right, u = t.up, fwd = t.forward;
        const cross = Vector3.cross(u, new Vector3(-fwd.x, -fwd.y, -fwd.z));
        const det = r.x * cross.x + r.y * cross.y + r.z * cross.z;

        assert.ok(Math.abs(det - 1) < 1e-4,
            `det(right, up, -forward) must be +1, not ${det.toFixed(3)}, for ${JSON.stringify(dir)}`);
    }
});

test('a transform told to look at a point faces that point', () => {
    // The camera case, end to end: high above the ground, aimed at a spot on it.
    const scene = new Scene('lookAt');
    const actor = scene.addActor(new Actor('Camera'));
    const t = actor.addComponent(Transform3D);
    scene.flushPendingActors();

    try {
        t.position = new Vector3(4565, 400, 4755);
        t.lookAt(new Vector3(4565, 0, 4565));

        const f = t.forward;
        assert.ok(f.y < -0.5, `a camera above the ground must look DOWN at it, not along y=${f.y.toFixed(3)}`);

        // Follow the ray to the ground and check where it lands.
        const k = -t.position.y / f.y;
        assert.ok(Math.abs(t.position.x + f.x * k - 4565) < 0.5, 'the ray must land on the target in x');
        assert.ok(Math.abs(t.position.z + f.z * k - 4565) < 0.5, 'the ray must land on the target in z');
    } finally {
        scene.destroy();
    }
});
