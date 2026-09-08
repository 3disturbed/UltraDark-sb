// -----------------------------------------------------------------------------
// Pins which camera Camera3D.main elects, and its world-to-screen projection.
//
// Both behaviours existed on one engine only. This one fell back from
// 'MainCamera3D' to 'MainCamera' and then to any camera at all, but skipped the
// enabled check the C# twin made; the C# twin returned null unless the exact tag
// was present, and RenderSystem3D returns immediately on a null camera -- so the
// bundled 3D Scene template, whose camera is tagged 'MainCamera', rendered here
// and rendered nothing natively. World-space UI resolves its camera through the
// same property, which is how the disagreement was found.
//
// Twin of SexyBiscuit.Tests/CameraElectionTests.cs.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import { Scene } from '../src/core/Scene.js';
import { Actor } from '../src/core/Actor.js';
import { Transform3D } from '../src/core/Transform3D.js';
import { Camera3D } from '../src/rendering/Camera3D.js';
import { Vector3 } from '../src/math/Vector3.js';

function fresh() {
    // A camera left behind by another test would win this election, and which camera wins is
    // the whole point of these tests.
    Camera3D.clearAll();
    return new Scene('Election');
}

function add(scene, tag) {
    const actor = new Actor('Camera');
    actor.tag = tag;
    actor.addComponent(Transform3D);
    const camera = actor.addComponent(Camera3D);
    scene.addActor(actor);
    scene.flushPendingActors();
    return camera;
}

test('a camera tagged MainCamera wins when nothing carries the 3D tag', () => {
    const scene = fresh();
    const camera = add(scene, 'MainCamera');

    assert.equal(Camera3D.main, camera);
    scene.destroy();
});

test('the 3D tag still beats the plain one', () => {
    const scene = fresh();
    add(scene, 'MainCamera');
    const wanted = add(scene, 'MainCamera3D');

    assert.equal(Camera3D.main, wanted);
    scene.destroy();
});

test('an untagged camera is better than no camera at all', () => {
    const scene = fresh();
    const camera = add(scene, '');

    assert.equal(Camera3D.main, camera);
    scene.destroy();
});

test('a switched-off camera never wins', () => {
    // This engine used to skip the check entirely, so a scene that disabled its menu camera
    // rendered through it here and through the next one natively.
    const scene = fresh();
    const off = add(scene, 'MainCamera3D');
    off.enabled = false;

    const live = add(scene, 'MainCamera');

    assert.equal(Camera3D.main, live);
    scene.destroy();
});

test('a point behind the camera projects to nothing rather than the opposite corner', () => {
    const scene = fresh();
    const camera = add(scene, 'MainCamera3D');
    camera.getTransform3D().position = new Vector3(0, 0, 10);

    assert.ok(camera.worldToScreen(Vector3.zero, 1280, 720));
    assert.equal(camera.worldToScreen(new Vector3(0, 0, 40), 1280, 720), null);
    scene.destroy();
});

test('the point straight ahead projects to the middle of the screen', () => {
    const scene = fresh();
    const camera = add(scene, 'MainCamera3D');
    camera.getTransform3D().position = new Vector3(0, 0, 10);

    const screen = camera.worldToScreen(Vector3.zero, 1280, 720);

    assert.ok(Math.abs(screen.x - 640) < 1e-3);
    assert.ok(Math.abs(screen.y - 360) < 1e-3);
    scene.destroy();
});
