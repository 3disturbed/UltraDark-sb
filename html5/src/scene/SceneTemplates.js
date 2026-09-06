// -----------------------------------------------------------------------------
// SceneTemplates — the scenes a new project starts from.
// -----------------------------------------------------------------------------

import { Scene } from '../core/Scene.js';
import { findPreset } from './ActorPresets.js';
import { Vector3 } from '../math/index.js';

/**
 * A lit 3D scene with a floor, some shapes, a camera, a spawn point and a game
 * mode — everything Play needs, so a new project runs before anything is added.
 */
export function createDefault3D(name = 'Untitled') {
    const scene = new Scene(name);

    scene.addActor(findPreset('Skybox').build(), 'background');
    scene.addActor(findPreset('Sky Light').build(), 'background');
    scene.addActor(findPreset('Directional Light').build());

    const fill = findPreset('Point Light').build();
    fill.name = 'Fill Light';
    fill.transform3D.localPosition = new Vector3(-4, 3, 4);
    fill.getComponent('Light3D').intensity = 4;
    scene.addActor(fill);

    scene.addActor(findPreset('Floor').build());

    const cube = findPreset('Cube').build();
    scene.addActor(cube);

    const sphere = findPreset('Sphere').build();
    sphere.transform3D.localPosition = new Vector3(-2.2, 0.5, 0);
    scene.addActor(sphere);

    const cylinder = findPreset('Cylinder').build();
    cylinder.transform3D.localPosition = new Vector3(2.2, 0.5, 0);
    scene.addActor(cylinder);

    scene.addActor(findPreset('Camera').build());
    scene.addActor(findPreset('Player Start').build());
    scene.addActor(findPreset('Game Mode').build());

    scene.flushPendingActors();
    return scene;
}

/** A 2D scene with a camera, a platform and a falling sprite. */
export function createDefault2D(name = 'Untitled') {
    const scene = new Scene(name);

    scene.addActor(findPreset('Camera 2D').build());

    const platform = findPreset('Static Platform').build();
    platform.transform.localPosition = { x: 640, y: 600 };
    platform.transform.localScale = { x: 6, y: 1 };
    scene.addActor(platform);

    const sprite = findPreset('Physics Sprite').build();
    sprite.transform.localPosition = { x: 640, y: 200 };
    scene.addActor(sprite);

    scene.flushPendingActors();
    return scene;
}

/** A scene with nothing but the four standard layers. */
export function createEmpty(name = 'Untitled') {
    return new Scene(name);
}
