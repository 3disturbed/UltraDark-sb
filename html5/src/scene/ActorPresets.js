// -----------------------------------------------------------------------------
// ActorPresets — the ready-made actors the editor's Place Actors palette offers.
//
// Code, not data, exactly as in Scene/ActorPresets.cs: each preset is a factory
// so an added actor is fully wired rather than a bag of default components.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector2, Vector3, Color } from '../math/index.js';
import { Camera2D } from '../rendering/Camera2D.js';
import { Camera3D } from '../rendering/Camera3D.js';
import { SpriteRenderer } from '../rendering/SpriteRenderer.js';
import { MeshRenderer } from '../rendering/MeshRenderer.js';
import { Light3D, LightType, SkyLight } from '../rendering/Light3D.js';
import { Skybox } from '../rendering/Skybox.js';
import { MeshPrimitive } from '../rendering/PrimitiveMesh.js';
import { Rigidbody2D, BodyType } from '../physics/Rigidbody2D.js';
import { BoxCollider2D, CircleCollider2D } from '../physics/Collider2D.js';
import { BoxCollider3D, SphereCollider3D } from '../physics/Collider3D.js';
import { Rigidbody3D } from '../physics/Rigidbody3D.js';
import { PlayerStart } from '../gameplay/PlayerStart.js';
import { ChibiCharacter } from '../chibi/ChibiCharacter.js';
import { ChibiAnimator } from '../chibi/ChibiAnimator.js';
import { GameMode } from '../gameplay/GameMode.js';
import { UiCanvas } from '../ui/UiCanvas.js';
import { UiSpace } from '../ui/UiEnums.js';

/** One entry in the Place Actors palette. */
export class ActorPreset {
    constructor(category, name, description, build) {
        this.category = category;
        this.name = name;
        this.description = description;
        this.build = build;
    }
}

/** Builds a 3D actor with a mesh of the given primitive and colour. */
function primitive(name, meshType, colour, { position = [0, 0, 0], scale = [1, 1, 1], roughness = 0.6 } = {}) {
    const actor = new Actor(name);
    const t = actor.addComponent(Transform3D);
    t.localPosition = Vector3.from(position);
    t.localScale = Vector3.from(scale);

    const renderer = actor.addComponent(MeshRenderer);
    renderer.setPrimitive(meshType);
    renderer.albedoColor = Color.from(colour);
    renderer.roughness = roughness;

    return actor;
}

/** Every preset, grouped by category. */
export const ActorPresets = [
    // ---- Basic --------------------------------------------------------------
    new ActorPreset('Basic', 'Empty Actor', 'An actor with nothing but a transform.',
        () => new Actor('Actor')),

    new ActorPreset('Basic', 'Empty 3D Actor', 'An actor with a 3D transform, ready to parent things to.',
        () => { const a = new Actor('Actor'); a.addComponent(Transform3D); return a; }),

    // ---- Geometry -----------------------------------------------------------
    new ActorPreset('Geometry', 'Cube', 'A one-metre cube.',
        () => primitive('Cube', MeshPrimitive.Cube, '#B4B8C0', { position: [0, 0.5, 0] })),

    new ActorPreset('Geometry', 'Sphere', 'A one-metre sphere.',
        () => primitive('Sphere', MeshPrimitive.Sphere, '#B4B8C0', { position: [0, 0.5, 0] })),

    new ActorPreset('Geometry', 'Cylinder', 'A one-metre cylinder.',
        () => primitive('Cylinder', MeshPrimitive.Cylinder, '#B4B8C0', { position: [0, 0.5, 0] })),

    new ActorPreset('Geometry', 'Capsule', 'A capsule, the shape a character occupies.',
        () => primitive('Capsule', MeshPrimitive.Capsule, '#B4B8C0', { position: [0, 1, 0] })),

    new ActorPreset('Geometry', 'Cone', 'A cone.',
        () => primitive('Cone', MeshPrimitive.Cone, '#B4B8C0', { position: [0, 0.5, 0] })),

    new ActorPreset('Geometry', 'Torus', 'A torus.',
        () => primitive('Torus', MeshPrimitive.Torus, '#B4B8C0', { position: [0, 0.5, 0] })),

    new ActorPreset('Geometry', 'Floor', 'A thirty-metre ground plane, tagged Ground.',
        () => {
            const actor = primitive('Floor', MeshPrimitive.Plane, '#767A82',
                { scale: [30, 1, 30], roughness: 0.9 });
            actor.tag = 'Ground';

            const collider = actor.addComponent(BoxCollider3D);
            collider.size = new Vector3(1, 0.1, 1);
            return actor;
        }),

    // ---- Lights -------------------------------------------------------------
    new ActorPreset('Characters', 'Chibi', 'A chibi character built from primitives, already playing an idle.',
        () => {
            const a = new Actor('Chibi');
            a.addComponent(Transform3D);
            a.addComponent(ChibiCharacter);
            a.addComponent(ChibiAnimator);
            return a;
        }),

    new ActorPreset('Characters', 'Chibi (random)', 'A different coordinated character every time you place one.',
        () => {
            const a = new Actor('Chibi');
            a.addComponent(Transform3D);
            // A fresh seed per placement: a crowd wants forty villagers, not forty of
            // the same villager, and the seed is what the scene file remembers.
            a.addComponent(ChibiCharacter).seed = 1 + Math.floor(Math.random() * 1000000);
            a.addComponent(ChibiAnimator);
            return a;
        }),

    new ActorPreset('Lights', 'Directional Light', 'A sun, angled to cast readable shadows.',
        () => {
            const actor = new Actor('Directional Light');
            actor.tag = 'Light';
            const t = actor.addComponent(Transform3D);
            t.localPosition = new Vector3(0, 10, 0);
            t.localEulerAngles = new Vector3(-50, 35, 0);

            const light = actor.addComponent(Light3D);
            light.type = LightType.Directional;
            light.color = Color.from('#FFF8E8');
            light.intensity = 1.1;
            light.castsShadows = true;
            return actor;
        }),

    new ActorPreset('Lights', 'Point Light', 'An omnidirectional light.',
        () => {
            const actor = new Actor('Point Light');
            actor.tag = 'Light';
            actor.addComponent(Transform3D).localPosition = new Vector3(0, 2, 0);

            const light = actor.addComponent(Light3D);
            light.type = LightType.Point;
            light.range = 10;
            light.intensity = 8;
            return actor;
        }),

    new ActorPreset('Lights', 'Spot Light', 'A cone of light.',
        () => {
            const actor = new Actor('Spot Light');
            actor.tag = 'Light';
            const t = actor.addComponent(Transform3D);
            t.localPosition = new Vector3(0, 4, 0);
            t.localEulerAngles = new Vector3(-90, 0, 0);

            const light = actor.addComponent(Light3D);
            light.type = LightType.Spot;
            light.range = 15;
            light.intensity = 20;
            light.spotAngle = 35;
            return actor;
        }),

    new ActorPreset('Lights', 'Sky Light', 'Ambient light from the sky and the ground.',
        () => { const a = new Actor('Sky Light'); a.addComponent(SkyLight); return a; }),

    new ActorPreset('Lights', 'Skybox', 'A gradient sky. Put it on the background layer.',
        () => { const a = new Actor('Sky'); a.addComponent(Skybox); return a; }),

    // ---- Cameras ------------------------------------------------------------
    new ActorPreset('Cameras', 'Camera', 'A 3D camera, tagged MainCamera3D.',
        () => {
            const actor = new Actor('Main Camera');
            actor.tag = 'MainCamera3D';
            const t = actor.addComponent(Transform3D);
            t.localPosition = new Vector3(0, 2, 8);
            t.localEulerAngles = new Vector3(-10, 0, 0);
            actor.addComponent(Camera3D);
            return actor;
        }),

    new ActorPreset('Cameras', 'Camera 2D', 'A 2D camera, tagged MainCamera.',
        () => {
            const actor = new Actor('Main Camera');
            actor.tag = 'MainCamera';
            actor.addComponent(Camera2D);
            return actor;
        }),

    // ---- Gameplay -----------------------------------------------------------
    new ActorPreset('Gameplay', 'Player Start', 'Where the game mode spawns a player.',
        () => {
            const actor = new Actor('Player Start');
            actor.tag = 'PlayerStart';
            actor.addComponent(Transform3D).localPosition = new Vector3(0, 0, 6);
            actor.addComponent(PlayerStart);
            return actor;
        }),

    new ActorPreset('Gameplay', 'Game Mode', 'The match rules. A scene needs one to play.',
        () => new GameMode('Game Mode')),

    new ActorPreset('Gameplay', 'Physics Cube', 'A cube that falls.',
        () => {
            const actor = primitive('Physics Cube', MeshPrimitive.Cube, '#C46A4A', { position: [0, 3, 0] });
            actor.addComponent(Rigidbody3D);
            actor.addComponent(BoxCollider3D);
            return actor;
        }),

    new ActorPreset('Gameplay', 'Trigger Volume', 'An invisible box that reports overlaps.',
        () => {
            const actor = new Actor('Trigger');
            actor.addComponent(Transform3D);
            const collider = actor.addComponent(BoxCollider3D);
            collider.isTrigger = true;
            collider.size = new Vector3(2, 2, 2);
            return actor;
        }),

    // ---- 2D -----------------------------------------------------------------
    new ActorPreset('2D', 'Sprite', 'A tinted quad, ready for a texture.',
        () => {
            const actor = new Actor('Sprite');
            const renderer = actor.addComponent(SpriteRenderer);
            renderer.tint = Color.from('#FFFFFF');
            return actor;
        }),

    new ActorPreset('2D', 'Physics Sprite', 'A sprite that falls and collides.',
        () => {
            const actor = new Actor('Physics Sprite');
            actor.addComponent(SpriteRenderer).tint = Color.from('#3296FF');
            actor.addComponent(Rigidbody2D).freezeRotation = true;
            actor.addComponent(BoxCollider2D).size = new Vector2(32, 32);
            return actor;
        }),

    new ActorPreset('2D', 'Static Platform', 'An immovable 2D platform, tagged Ground.',
        () => {
            const actor = new Actor('Platform');
            actor.tag = 'Ground';
            actor.addComponent(SpriteRenderer).tint = Color.from('#50783C');
            actor.addComponent(Rigidbody2D).bodyType = BodyType.Static;
            actor.addComponent(BoxCollider2D).size = new Vector2(200, 32);
            return actor;
        }),

    new ActorPreset('2D', 'Ball', 'A bouncing circle.',
        () => {
            const actor = new Actor('Ball');
            actor.addComponent(SpriteRenderer).tint = Color.from('#FFC83C');
            actor.addComponent(Rigidbody2D);
            const collider = actor.addComponent(CircleCollider2D);
            collider.radius = 16;
            collider.restitution = 0.6;
            return actor;
        }),

    // ---- UI -----------------------------------------------------------------
    new ActorPreset('UI', 'Canvas', 'Screen-space UI root: a tree of nodes, laid out and navigable.',
        () => { const a = new Actor('Canvas'); a.addComponent(UiCanvas); return a; }),

    new ActorPreset('UI', 'World Canvas',
        'The same UI tree, standing on a plane in the scene: nameplates, terminals, signs.',
        () => {
            const a = new Actor('World Canvas');
            a.addComponent(Transform3D);
            const canvas = a.addComponent(UiCanvas);
            canvas.space = UiSpace.World;
            canvas.referenceResolution = { x: 400, y: 200 };
            return a;
        }),
];

/** Finds a preset by name — exactly first, then case-insensitively. */
export function findPreset(name) {
    return ActorPresets.find((p) => p.name === name)
        ?? ActorPresets.find((p) => p.name.toLowerCase() === String(name).toLowerCase())
        ?? null;
}

/** The preset categories, in palette order. */
export function presetCategories() {
    return [...new Set(ActorPresets.map((p) => p.category))];
}

export { SphereCollider3D };
