// -----------------------------------------------------------------------------
// Interoperability — the HTML5 runtime must read and write the same project
// files the C# engine does. These tests run against the real scenes and scripts
// under Templates/, not against fixtures, so a change to either engine's format
// shows up here.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
    deserialize, serialize, Scene, Actor, Transform3D, MissingComponent,
    SpriteRenderer, Camera2D, Rigidbody2D, BoxCollider2D, ScriptComponent,
    Light3D, MeshRenderer, Color, Vector2, Vector3, Quaternion,
    EngineConfig, resolveComponent,
} from '../src/index.js';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const templatesDir = path.join(repoRoot, 'Templates');

/** Every `.scene` the repository ships, so no template is left untested. */
function templateScenes() {
    if (!fs.existsSync(templatesDir)) return [];
    const found = [];

    for (const template of fs.readdirSync(templatesDir)) {
        const scenesDir = path.join(templatesDir, template, 'Scenes');
        if (!fs.existsSync(scenesDir)) continue;
        for (const file of fs.readdirSync(scenesDir)) {
            if (file.endsWith('.scene')) found.push(path.join(scenesDir, file));
        }
    }
    return found;
}

test('every bundled C# template scene loads', () => {
    const scenes = templateScenes();
    assert.ok(scenes.length > 0, 'no template scenes found — is the repo complete?');

    for (const file of scenes) {
        const scene = deserialize(fs.readFileSync(file, 'utf8'), { onWarning: () => {} });

        assert.ok(scene.name, `${path.basename(file)} produced a nameless scene`);
        assert.ok(scene.layers.length > 0, `${path.basename(file)} produced no layers`);

        const actors = scene.allActors;
        assert.ok(actors.length > 0, `${path.basename(file)} produced no actors`);

        // Every actor must have kept its identity and its components.
        for (const actor of actors) {
            assert.ok(actor.name, `an actor in ${path.basename(file)} lost its name`);
            assert.ok(actor.transform, 'an actor lost its transform');
        }
    }
});

test('a scene round-trips through save and load unchanged', () => {
    const file = path.join(templatesDir, '2D Platformer', 'Scenes', 'Level1.scene');
    const original = deserialize(fs.readFileSync(file, 'utf8'), { onWarning: () => {} });

    const reloaded = deserialize(serialize(original), { onWarning: () => {} });

    assert.equal(reloaded.name, original.name);
    assert.equal(reloaded.allActors.length, original.allActors.length);

    for (const before of original.allActors) {
        const after = reloaded.findByName(before.name);
        assert.ok(after, `'${before.name}' did not survive the round trip`);
        assert.equal(after.tag, before.tag);
        assert.deepEqual(after.transform.localPosition.toArray(), before.transform.localPosition.toArray());
        assert.deepEqual(after.transform.localScale.toArray(), before.transform.localScale.toArray());
        assert.equal(after.getAllComponents().length, before.getAllComponents().length);
    }
});

test('registered component types resolve and their properties apply', () => {
    const file = path.join(templatesDir, '2D Platformer', 'Scenes', 'Level1.scene');
    const scene = deserialize(fs.readFileSync(file, 'utf8'), { onWarning: () => {} });

    const player = scene.findByName('Player');
    assert.ok(player);

    // The scene names these by their bare C# type name and nothing else.
    const sprite = player.getComponent(SpriteRenderer);
    assert.ok(sprite, 'SpriteRenderer did not resolve');
    assert.deepEqual(
        [sprite.tint.r, sprite.tint.g, sprite.tint.b],
        [50, 150, 255],
        'the { R, G, B, A } colour form did not load');

    const body = player.getComponent(Rigidbody2D);
    assert.ok(body);
    assert.equal(body.mass, 1);
    assert.equal(body.freezeRotation, true);

    const collider = player.getComponent(BoxCollider2D);
    assert.ok(collider);
    assert.deepEqual(collider.size.toArray(), [32, 48], 'the [x, y] vector form did not load');

    const script = player.getComponent(ScriptComponent);
    assert.ok(script);
    assert.equal(script.scriptPath, 'Scripts/PlayerController.js');
});

test('the hand-authored transform and transform3d forms load', () => {
    const scene = deserialize({
        name: 'Forms',
        layers: [{
            name: 'Default',
            actors: [
                { name: 'Flat', transform: { x: 12, y: 34, rotation: 1.5, scaleX: 2, scaleY: 3 } },
                {
                    name: 'Solid',
                    transform3d: { x: 1, y: 2, z: 3, rotX: -45, rotY: 30, rotZ: 0, scaleX: 1, scaleY: 1, scaleZ: 1 },
                },
            ],
        }],
    });

    const flat = scene.findByName('Flat');
    assert.deepEqual(flat.transform.localPosition.toArray(), [12, 34]);
    assert.equal(flat.transform.localRotation, 1.5);
    assert.deepEqual(flat.transform.localScale.toArray(), [2, 3]);

    const solid = scene.findByName('Solid');
    const t = solid.getComponent(Transform3D);
    assert.ok(t, 'transform3d did not add a Transform3D');
    assert.deepEqual(t.localPosition.toArray(), [1, 2, 3]);

    const euler = t.localEulerAngles;
    assert.ok(Math.abs(euler.x - -45) < 1e-3, `pitch was ${euler.x}`);
    assert.ok(Math.abs(euler.y - 30) < 1e-3, `yaw was ${euler.y}`);
});

test('a 2D actor does not gain a 3D transform on load', () => {
    const scene = deserialize({
        name: 'Flat',
        layers: [{ name: 'Default', actors: [{ name: 'Sprite', position: [1, 2] }] }],
    });

    assert.equal(scene.findByName('Sprite').getComponent(Transform3D), null);
});

test('an unresolvable component round-trips byte for byte', () => {
    const source = {
        name: 'Unknown',
        layers: [{
            name: 'Default',
            actors: [{
                name: 'Turret',
                components: [
                    { type: 'ProjectSpecificWeapon', properties: { Damage: 42, Barrels: ['a', 'b'] } },
                ],
            }],
        }],
    };

    const scene = deserialize(source, { onWarning: () => {} });
    const turret = scene.findByName('Turret');

    const placeholder = turret.getComponent(MissingComponent);
    assert.ok(placeholder, 'an unknown component was dropped rather than preserved');
    assert.equal(placeholder.typeName, 'ProjectSpecificWeapon');

    const written = JSON.parse(serialize(scene));
    const component = written.layers[0].actors[0].components[0];

    assert.equal(component.type, 'ProjectSpecificWeapon');
    assert.deepEqual(component.properties, source.layers[0].actors[0].components[0].properties);
});

test('an unresolvable actor class keeps its name', () => {
    const scene = deserialize({
        name: 'Classes',
        layers: [{
            name: 'Default',
            actors: [{ name: 'Boss', class: 'ProjectBossActor', properties: { Health: 500 } }],
        }],
    }, { onWarning: () => {} });

    const written = JSON.parse(serialize(scene));
    assert.equal(written.layers[0].actors[0].class, 'ProjectBossActor');
    assert.deepEqual(written.layers[0].actors[0].properties, { Health: 500 });
});

test('component names resolve by short, full and assembly-qualified name', () => {
    assert.equal(resolveComponent('SpriteRenderer'), SpriteRenderer);
    assert.equal(resolveComponent('SexyBiscuit.Engine.Rendering.SpriteRenderer'), SpriteRenderer);
    assert.equal(
        resolveComponent('SexyBiscuit.Engine.Rendering.SpriteRenderer, SexyBiscuit.Engine, Version=1.0.0.0'),
        SpriteRenderer);
    assert.equal(resolveComponent('spriterenderer'), SpriteRenderer);
    assert.equal(resolveComponent('NoSuchComponent'), null);
});

test('the writer emits the flat array form the C# reader expects', () => {
    const scene = new Scene('Written');
    const actor = new Actor('Thing');
    actor.transform.localPosition = new Vector2(10, 20);
    actor.transform.localScale = new Vector2(2, 2);

    const t3d = actor.addComponent(Transform3D);
    t3d.localPosition = new Vector3(1, 2, 3);
    t3d.localRotation = Quaternion.fromEuler([0, 90, 0]);

    const light = actor.addComponent(Light3D);
    light.color = Color.from('#FF8040');
    light.intensity = 2.5;

    scene.addActor(actor);
    scene.flushPendingActors();

    const written = JSON.parse(serialize(scene));
    const dto = written.layers.flatMap((l) => l.actors).find((a) => a.name === 'Thing');

    assert.deepEqual(dto.position, [10, 20], 'position must be a two-element array');
    assert.deepEqual(dto.scale, [2, 2]);
    assert.deepEqual(dto.position3, [1, 2, 3], 'position3 must be a three-element array');
    assert.equal(dto.rotation3.length, 4, 'rotation3 must be a quaternion, not euler angles');

    const lightDto = dto.components.find((c) => c.type === 'Light3D');
    assert.ok(lightDto, 'the component was written under its bare type name');
    assert.equal(lightDto.properties.Color, '#FF8040FF', 'colours are written as #RRGGBBAA');
    assert.equal(lightDto.properties.Intensity, 2.5, 'property keys are PascalCase');
    assert.equal(lightDto.properties.Type, 'Directional', 'enums are written by name');
});

test('a MeshRenderer writes its material once, not twice', () => {
    const scene = new Scene('Materials');
    const actor = new Actor('Cube');
    actor.addComponent(Transform3D);

    const renderer = actor.addComponent(MeshRenderer);
    renderer.albedoColor = Color.from('#3366CC');
    renderer.roughness = 0.25;

    scene.addActor(actor);
    scene.flushPendingActors();

    const dto = JSON.parse(serialize(scene)).layers.flatMap((l) => l.actors)[0];
    const properties = dto.components.find((c) => c.type === 'MeshRenderer').properties;

    // The convenience passthroughs proxy the first material; writing both would
    // leave the file disagreeing with itself as soon as either is edited.
    assert.equal(properties.AlbedoColor, undefined);
    assert.equal(properties.Roughness, undefined);
    assert.equal(properties.Materials[0].albedoColor, '#3366CCFF');
    assert.equal(properties.Materials[0].roughness, 0.25);
});

test('ProjectSettings.json is read in both casings the repo uses', () => {
    const pascal = EngineConfig.fromProjectSettings({
        WindowTitle: '2D Platformer', WindowWidth: 1280, WindowHeight: 720,
        VSync: true, StartScene: 'Scenes/Level1',
    });
    assert.equal(pascal.windowTitle, '2D Platformer');
    assert.equal(pascal.windowWidth, 1280);
    assert.equal(pascal.startScene, 'Scenes/Level1');

    const camel = EngineConfig.fromProjectSettings({
        appName: 'Biscuit Chronicles', startScene: 'Scenes/MainMenu',
        windowWidth: 1920, steamAppId: 480, version: '0.1.0',
    });
    assert.equal(camel.windowTitle, 'Biscuit Chronicles', 'appName aliases WindowTitle');
    assert.equal(camel.startScene, 'Scenes/MainMenu');
    assert.equal(camel.windowWidth, 1920);
});

test('every real ProjectSettings.json in the repo parses', () => {
    if (!fs.existsSync(templatesDir)) return;

    for (const template of fs.readdirSync(templatesDir)) {
        const file = path.join(templatesDir, template, 'ProjectSettings.json');
        if (!fs.existsSync(file)) continue;

        const config = EngineConfig.fromProjectSettings(fs.readFileSync(file, 'utf8'));
        assert.ok(config.windowTitle, `${template} produced no window title`);
        assert.ok(config.windowWidth > 0);
    }
});

test('a byte order mark does not stop a scene loading', () => {
    // .NET's Encoding.UTF8 emits one by default, so every JSON file the C#
    // export pipeline writes starts with U+FEFF — which JSON.parse rejects.
    const file = path.join(templatesDir, '2D Platformer', 'Scenes', 'Level1.scene');
    const withBom = `﻿${fs.readFileSync(file, 'utf8')}`;

    const scene = deserialize(withBom, { onWarning: () => {} });
    assert.equal(scene.name, 'Level1');
    assert.ok(scene.findByName('Player'));
});
