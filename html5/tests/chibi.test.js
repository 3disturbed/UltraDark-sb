// -----------------------------------------------------------------------------
// MakeChibi — the recipe, the rig it builds, and the clips that drive it.
//
// The content gates matter more than the unit tests here: chibi-parts.json is a
// thousand lines of geometry naming joints, meshes and colour slots by string,
// and a typo in any of them produces a character with a missing arm rather than
// an error. Building every variant is what turns that into a failure.
//
// Mirrored in SexyBiscuit.Tests/ChibiTests.cs; both suites read the same data.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import {
    Scene, Actor, Transform3D, Vector3, MeshPrimitive,
    ChibiParts, JOINT_NAMES, SOCKET_NAMES, SLOTS, COLOUR_SLOTS, variantsFor, accessoryNames,
    defaultRecipe, normaliseRecipe, parseRecipe, stringifyRecipe, randomRecipe, seededRandom,
    buildChibi, findClip, clipNames, Pose, ChibiCharacter, ChibiAnimator,
    createScriptGlobals,
} from '../src/index.js';

// -----------------------------------------------------------------------------
// The rig contract
// -----------------------------------------------------------------------------

test('the rig lists every joint parents-first', () => {
    // The builder attaches in one pass, so a joint whose parent comes later in the
    // list would be attached to nothing and stand at the world origin.
    const seen = new Set();
    for (const joint of ChibiParts.joints) {
        for (const name of joint.name.includes('*') ? ['L', 'R'] : [null]) {
            const parent = joint.parent ? (name ? joint.parent.replace('*', name) : joint.parent) : '';
            if (parent) assert.ok(seen.has(parent), `${joint.name} comes before its parent ${parent}`);
        }
        for (const name of joint.name.includes('*') ? ['L', 'R'] : [null]) {
            seen.add(name ? joint.name.replace('*', name) : joint.name);
        }
    }
});

test('the rig has the joints a clip is allowed to name', () => {
    assert.deepEqual([...JOINT_NAMES], [
        'Hips', 'Torso', 'Neck', 'Head',
        'ArmL', 'ArmR', 'ForearmL', 'ForearmR', 'HandL', 'HandR',
        'ThighL', 'ThighR', 'ShinL', 'ShinR', 'FootL', 'FootR',
    ]);
    assert.deepEqual([...SOCKET_NAMES], [
        'Socket_Head', 'Socket_Face', 'Socket_Hand_L', 'Socket_Hand_R', 'Socket_Back',
    ]);
});

// -----------------------------------------------------------------------------
// The recipe
// -----------------------------------------------------------------------------

test('an empty recipe is a valid character', () => {
    // `{}` has to work: it is what a new file contains, and what the panel starts from.
    const recipe = normaliseRecipe({});
    assert.deepEqual(Object.keys(recipe.style).sort(), [...SLOTS].sort());
    assert.deepEqual(Object.keys(recipe.colours).sort(), [...COLOUR_SLOTS].sort());
    for (const value of Object.values(recipe.proportions)) assert.equal(value, 1);
});

test('a recipe round-trips through the text a .chibi file holds', () => {
    const before = randomRecipe(4242);
    const after = parseRecipe(stringifyRecipe(before));
    assert.deepEqual(after, before);
});

test('a style that no longer exists falls back instead of throwing', () => {
    // Renaming a hairstyle must not make every saved character unloadable.
    const warnings = [];
    const recipe = normaliseRecipe({ style: { hair: 'Beehive' } }, (m) => warnings.push(m));
    assert.equal(recipe.style.hair, 'Bob');
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /Beehive/);
});

test('proportions are clamped rather than trusted', () => {
    const recipe = normaliseRecipe({ proportions: { height: 40, headSize: -3, bodyWidth: 'wide' } });
    assert.equal(recipe.proportions.height, 2);
    assert.equal(recipe.proportions.headSize, 0.5);
    assert.equal(recipe.proportions.bodyWidth, 1);
});

test('a seed names the same character every time', () => {
    assert.deepEqual(randomRecipe(1001), randomRecipe(1001));
    assert.notDeepEqual(randomRecipe(1001), randomRecipe(1002));
});

test('the seeded generator produces the sequence the C# side reproduces', () => {
    // mulberry32 in 32-bit integer arithmetic. These four numbers are the pin:
    // ChibiRecipeTests asserts the same ones, so a change to either implementation
    // that would give the two engines different villages fails here.
    const next = seededRandom(7);
    const got = [next(), next(), next(), next()].map((v) => v.toFixed(9));
    assert.deepEqual(got, ['0.011704753', '0.061958258', '0.976907633', '0.699028706']);
});

test('a random character draws its colours from the curated palettes', () => {
    // Random bytes give forty characters the colour of mud; that is the whole
    // reason the palettes exist, so nothing may bypass them.
    for (let seed = 1; seed <= 60; seed++) {
        const recipe = randomRecipe(seed);
        assert.ok(ChibiParts.palettes.skin.includes(recipe.colours.skin));
        assert.ok(ChibiParts.palettes.hair.includes(recipe.colours.hair));
        assert.ok(ChibiParts.palettes.outfit.some(([t, b]) =>
            t === recipe.colours.top && b === recipe.colours.bottom));
    }
});

// -----------------------------------------------------------------------------
// The content
// -----------------------------------------------------------------------------

test('every part variant builds without a warning', () => {
    // chibi-parts.json names joints, meshes and colour slots as strings. A typo
    // gives a character with a missing arm, not an error -- unless this runs.
    for (const slot of SLOTS) {
        for (const variant of variantsFor(slot)) {
            const warnings = [];
            const recipe = defaultRecipe();
            recipe.style[slot] = variant;
            const built = buildChibi(recipe, (m) => warnings.push(m));
            assert.deepEqual(warnings, [], `${slot}/${variant}`);
            assert.ok(built.parts.length > 0, `${slot}/${variant} drew nothing`);
        }
    }
});

test('every accessory builds without a warning', () => {
    for (const part of accessoryNames()) {
        const warnings = [];
        const recipe = defaultRecipe();
        recipe.accessories = [{ part, colour: null }];
        buildChibi(recipe, (m) => warnings.push(m));
        assert.deepEqual(warnings, [], part);
    }
});

test('every piece names a mesh both engines can build', () => {
    // A shape only one engine has renders as a cube in the other, silently.
    const meshes = new Set();
    for (const slot of Object.values(ChibiParts.parts)) {
        for (const pieces of Object.values(slot)) for (const p of pieces) meshes.add(p.mesh);
    }
    for (const pieces of Object.values(ChibiParts.accessories)) {
        for (const p of pieces) meshes.add(p.mesh);
    }
    for (const mesh of meshes) {
        assert.ok(mesh in MeshPrimitive, `'${mesh}' is not a MeshPrimitive`);
    }
});

test('every piece paints itself from a declared colour slot', () => {
    const all = [
        ...Object.values(ChibiParts.parts).flatMap((s) => Object.values(s).flat()),
        ...Object.values(ChibiParts.accessories).flat(),
    ];
    for (const piece of all) {
        assert.ok(COLOUR_SLOTS.includes(piece.colour),
            `'${piece.colour}' is not a colour slot`);
    }
});

test('eyes sit on the face of every head shape, not inside it', () => {
    // A sphere's surface falls away towards the cheek and a cube's does not, so one
    // eye depth cannot serve both: eyes placed for a round head vanish inside a
    // square one, leaving a blank box for a face.
    for (const head of variantsFor('head')) {
        for (const style of variantsFor('eyes')) {
            const recipe = defaultRecipe();
            recipe.style.head = head;
            recipe.style.eyes = style;

            const actors = [...buildChibi(recipe).actor.descendants()];
            const skull = actors.find((a) => /^head_(Sphere|Cube|Capsule)$/.test(a.name));
            const skullT = skull.getComponent(Transform3D);
            const isCube = skull.name.endsWith('Cube');

            for (const eye of actors.filter((a) => a.name.startsWith('eyes_'))) {
                const t = eye.getComponent(Transform3D);
                const rx = skullT.scale.x / 2;
                const ry = (skull.name.endsWith('Capsule') ? skullT.scale.y : skullT.scale.y / 2);
                const rz = skullT.scale.z / 2;
                const dx = (t.position.x - skullT.position.x) / rx;
                const dy = (t.position.y - skullT.position.y) / ry;
                const dz = (t.position.z - skullT.position.z) / rz;

                // Reach: 1 is exactly on the surface. Below it the eye is buried.
                const reach = isCube ? Math.abs(dz) : Math.hypot(dx, dy, dz);
                assert.ok(reach > 0.95,
                    `${head}/${style}: an eye sits at ${reach.toFixed(2)} of the way to the surface`);
                assert.ok(reach < 1.35,
                    `${head}/${style}: an eye floats at ${reach.toFixed(2)} of the way to the surface`);
            }
        }
    }
});

test('no hairstyle swallows the head, on any of the eight', () => {
    // Hair is authored against the Round head and scaled onto the other seven by
    // headFit. Without that, the bob that fits a round skull is wider than a slim
    // one and hides it completely -- the character renders as a hair-coloured box
    // with two eyes floating on the front, which is exactly what it looked like.
    //
    // Checking the eyes alone does not catch it: the eyes sit low and stick out
    // even when everything above them is buried. So sample the front of the skull
    // and insist that most of it is still skin.
    const inside = (piece, point) => {
        const d = [0, 1, 2].map((i) => (point[i] - piece.position[i]) / (piece.size[i] / 2));
        return piece.mesh === 'Cube'
            ? d.every((v) => Math.abs(v) <= 1)
            : d[0] * d[0] + d[1] * d[1] + d[2] * d[2] <= 1;
    };

    const describe = (actor) => {
        const t = actor.getComponent(Transform3D);
        const mesh = actor.name.split('_')[1];
        return {
            position: [t.position.x, t.position.y, t.position.z],
            // A capsule is twice as tall as its y scale says.
            size: [t.scale.x, mesh === 'Capsule' ? t.scale.y * 2 : t.scale.y, t.scale.z],
            mesh,
            rotated: Math.abs(t.localEulerAngles.x) + Math.abs(t.localEulerAngles.z) > 1,
        };
    };

    for (const head of variantsFor('head')) {
        for (const style of variantsFor('hair')) {
            const recipe = defaultRecipe();
            recipe.style.head = head;
            recipe.style.hair = style;

            const actors = [...buildChibi(recipe).actor.descendants()];
            const skull = actors.filter((a) => a.name.startsWith('head_')).map(describe)
                .find((p) => p.mesh !== 'Cylinder');
            const hair = actors.filter((a) => a.name.startsWith('hair_')).map(describe);

            // Points across the front half of the skull, from the brow to the chin.
            let total = 0;
            let covered = 0;
            for (let ring = -0.75; ring <= 0.55; ring += 0.1) {
                for (let sweep = -0.7; sweep <= 0.7; sweep += 0.1) {
                    const r = Math.sqrt(Math.max(0, 1 - ring * ring));
                    const point = [
                        skull.position[0] + (skull.size[0] / 2) * r * Math.sin(sweep * Math.PI / 2) * 0.92,
                        skull.position[1] + (skull.size[1] / 2) * ring * 0.92,
                        skull.position[2] + (skull.size[2] / 2) * r * Math.cos(sweep * Math.PI / 2) * 0.92,
                    ];
                    total++;
                    if (hair.some((piece) => !piece.rotated && inside(piece, point))) covered++;
                }
            }

            const visible = 1 - covered / total;
            assert.ok(visible > 0.45,
                `${head}/${style}: only ${(visible * 100).toFixed(0)}% of the face is showing`);
        }
    }
});

// -----------------------------------------------------------------------------
// The build
// -----------------------------------------------------------------------------

test('a built chibi stands on the ground with its head on top', () => {
    // The numbers a designer actually notices. A chibi is about a unit tall, its
    // feet are on the floor rather than through it, and the head is above the hips.
    const built = buildChibi(defaultRecipe());
    const y = (joint) => built.joints.get(joint).position.y;

    assert.ok(y('FootL') > 0 && y('FootL') < 0.06, `feet at ${y('FootL')}`);
    assert.ok(y('Head') > y('Torso'));
    assert.ok(y('Torso') > y('Hips'));
    assert.equal(y('FootL').toFixed(6), y('FootR').toFixed(6));
});

test('the two sides are mirrored, not copied', () => {
    const built = buildChibi(defaultRecipe());
    const left = built.joints.get('ArmL').position;
    const right = built.joints.get('ArmR').position;

    assert.ok(left.x > 0 && right.x < 0, `${left.x} / ${right.x}`);
    assert.equal(left.x.toFixed(6), (-right.x).toFixed(6));
    assert.equal(left.y.toFixed(6), right.y.toFixed(6));
});

test('height scales the whole character and legLength only the legs', () => {
    const tall = defaultRecipe();
    tall.proportions.height = 2;
    assert.ok(buildChibi(tall).joints.get('Head').position.y >
              buildChibi(defaultRecipe()).joints.get('Head').position.y * 1.9);

    const leggy = defaultRecipe();
    leggy.proportions.legLength = 1.5;
    const built = buildChibi(leggy);
    // Longer legs raise the hips, so the feet stay on the floor rather than sinking.
    assert.ok(built.joints.get('Hips').position.y > 0.4);
    assert.ok(built.joints.get('FootL').position.y > 0);
});

test('recolouring a slot repaints every part that uses it', () => {
    const built = buildChibi(defaultRecipe());
    assert.ok(built.setColour('top', '#123456'));
    const tops = built.parts.filter((p) => p.colour === 'top');
    assert.ok(tops.length > 0);
    for (const part of tops) assert.equal(part.renderer.albedoColor.toHexRgb().toLowerCase(), '#123456');
});

test('an accessory with its own colour is not repainted by its slot', () => {
    // A red sword stays red when the accent colour changes; that is what asking
    // for a colour on the accessory means.
    const recipe = defaultRecipe();
    recipe.accessories = [{ part: 'Sword', colour: '#FF0000' }];
    const built = buildChibi(recipe);
    built.setColour('accent', '#00FF00');

    const overridden = built.parts.filter((p) => p.colour === null);
    assert.equal(overridden.length, 3);
    for (const part of overridden) assert.equal(part.renderer.albedoColor.toHexRgb().toLowerCase(), '#ff0000');
});

// -----------------------------------------------------------------------------
// The clips
// -----------------------------------------------------------------------------

test('every clip poses only joints the rig has', () => {
    // A clip naming a joint that does not exist is silently ignored by the
    // animator, so a limb simply never moves and nobody knows why.
    const pose = new Pose();
    for (const name of clipNames()) {
        const clip = findClip(name);
        for (const t of [0, 0.25, 0.5, 0.75, 1]) {
            pose.clear();
            clip.sample(t, pose, 1);
            for (const [joint, rot] of pose.joints) {
                assert.ok(JOINT_NAMES.includes(joint), `${name} poses '${joint}'`);
                for (const angle of rot) assert.ok(Number.isFinite(angle), `${name}/${joint}`);
            }
            for (const axis of pose.offset) assert.ok(Number.isFinite(axis), name);
        }
    }
});

test('walking swings the legs in opposition', () => {
    // The one thing a walk cycle has to get right. Both legs forward at once is
    // the failure this catches.
    const pose = new Pose();
    findClip('walk').sample(0.25, pose, 1);
    assert.ok(pose.get('ThighL')[0] * pose.get('ThighR')[0] < 0);
    assert.ok(pose.get('ArmL')[0] * pose.get('ThighL')[0] < 0, 'arms should oppose their own leg');
});

test('intensity scales a procedural clip rather than its speed', () => {
    const full = new Pose();
    const half = new Pose();
    findClip('walk').sample(0.25, full, 1);
    findClip('walk').sample(0.25, half, 0.5);
    assert.ok(Math.abs(half.get('ThighL')[0]) < Math.abs(full.get('ThighL')[0]));
});

test('a keyed clip interpolates between its keys', () => {
    const at = (t) => { const p = new Pose(); findClip('wave').sample(t, p); return p.get('ArmR')[2]; };
    assert.equal(at(0), -8);
    assert.equal(at(0.18).toFixed(3), (-132).toFixed(3));
    assert.ok(at(0.09) > -132 && at(0.09) < -8, 'half-way should be between the keys');
});

// -----------------------------------------------------------------------------
// The components
// -----------------------------------------------------------------------------

function sceneWithChibi(configure = null) {
    const scene = new Scene('chibi');
    const actor = new Actor('Villager');
    actor.addComponent(Transform3D);
    const character = actor.addComponent(ChibiCharacter);
    if (configure) configure(character);
    scene.addActor(actor);
    scene.flushPendingActors();
    return { scene, actor, character };
}

test('ChibiCharacter builds its body under its own actor', () => {
    const { scene, actor, character } = sceneWithChibi();
    try {
        character.rebuild();
        scene.flushPendingActors();

        assert.ok(character.chibi, 'nothing was built');
        assert.equal(character.chibi.actor.parent, actor);
        assert.equal(character.chibi.joints.size, JOINT_NAMES.length);
        assert.ok([...actor.descendants()].length > 30);
    } finally { scene.destroy(); }
});

test('rebuilding replaces the body rather than stacking a second one', () => {
    const { scene, actor, character } = sceneWithChibi();
    try {
        character.rebuild();
        scene.flushPendingActors();
        const first = [...actor.descendants()].length;

        character.rebuild();
        scene.flushPendingActors();
        assert.equal([...actor.descendants()].length, first);
    } finally { scene.destroy(); }
});

test('a seed on the component builds that seed character', () => {
    const { scene, character } = sceneWithChibi((c) => { c.seed = 1001; });
    try {
        character.rebuild();
        scene.flushPendingActors();
        assert.deepEqual(character.chibi.recipe.style, randomRecipe(1001).style);
    } finally { scene.destroy(); }
});

test('the animator poses the joints it resolved', () => {
    const { scene, actor, character } = sceneWithChibi();
    try {
        character.rebuild();
        scene.flushPendingActors();

        const animator = actor.addComponent(ChibiAnimator);
        animator.clip = 'walk';
        animator.start();
        animator.update(0.2);

        const thighL = character.chibi.joints.get('ThighL').localEulerAngles;
        const thighR = character.chibi.joints.get('ThighR').localEulerAngles;
        assert.ok(Math.abs(thighL.x) > 1, `ThighL did not move: ${thighL.x}`);
        assert.ok(thighL.x * thighR.x < 0, 'the legs should oppose');
    } finally { scene.destroy(); }
});

test('a clip that ends tells whoever asked, once', () => {
    const { scene, actor, character } = sceneWithChibi();
    try {
        character.rebuild();
        scene.flushPendingActors();

        const animator = actor.addComponent(ChibiAnimator);
        animator.start();

        const finished = [];
        animator.onClipFinished = (name) => finished.push(name);
        animator.play('hit', 0);

        for (let i = 0; i < 20; i++) animator.update(0.05);
        assert.deepEqual(finished, ['hit']);
    } finally { scene.destroy(); }
});

test('switching clips leaves no joint mid-stride', () => {
    // A wave poses one arm. Without resetting the rest, the legs keep whatever the
    // walk left them at and the character waves while frozen in a stride.
    const { scene, actor, character } = sceneWithChibi();
    try {
        character.rebuild();
        scene.flushPendingActors();

        const animator = actor.addComponent(ChibiAnimator);
        animator.start();
        animator.play('walk', 0);
        animator.update(0.2);
        assert.ok(Math.abs(character.chibi.joints.get('ThighL').localEulerAngles.x) > 1);

        animator.play('wave', 0);
        animator.update(0.05);
        assert.ok(Math.abs(character.chibi.joints.get('ThighL').localEulerAngles.x) < 0.001,
            'the legs should have gone back to rest');
    } finally { scene.destroy(); }
});

// -----------------------------------------------------------------------------
// The Chibi global
// -----------------------------------------------------------------------------

function scriptGlobals() {
    const scene = new Scene('chibi-script');
    const host = new Actor('Host');
    scene.addActor(host);
    scene.flushPendingActors();
    return { scene, globals: createScriptGlobals(host) };
}

test('a script can spawn a character and play a clip', () => {
    // bridge.test.js only pins the member names. This is what proves the members do
    // anything -- and it is the shape every game script would use. The C# suite runs
    // the same three cases through Jint.
    const { scene, globals } = scriptGlobals();
    try {
        const villager = globals.Chibi.random(1001, 2, 0, -3);
        scene.flushPendingActors();

        const spawned = scene.findByName('Chibi 1001');
        assert.ok(spawned, 'nothing was spawned');
        assert.equal(spawned.transform3D.localPosition.x, 2);

        spawned.getComponent(ChibiCharacter).rebuild();
        scene.flushPendingActors();

        assert.equal(globals.Chibi.play(villager, 'walk'), true);
        assert.equal(spawned.getComponent(ChibiAnimator).clip, 'walk');
        assert.equal(globals.Chibi.play(villager, 'moonwalk'), false);
    } finally { scene.destroy(); }
});

test('a script can recolour and redress a character', () => {
    const { scene, globals } = scriptGlobals();
    try {
        const v = globals.Chibi.random(7, 0, 0, 0);
        scene.flushPendingActors();
        const spawned = scene.findByName('Chibi 7');
        spawned.getComponent(ChibiCharacter).rebuild();
        scene.flushPendingActors();

        assert.equal(globals.Chibi.setColour(v, 'top', '#123456'), true);
        assert.equal(spawned.getComponent(ChibiCharacter).chibi.recipe.colours.top, '#123456');
        assert.equal(globals.Chibi.setColour(v, 'eyebrows', '#000'), false);

        assert.equal(globals.Chibi.setStyle(v, 'hair', 'Mohawk'), true);
        scene.flushPendingActors();
        assert.equal(spawned.getComponent(ChibiCharacter).chibi.recipe.style.hair, 'Mohawk');
    } finally { scene.destroy(); }
});

test('a script can hang something off a socket', () => {
    // The reason the sockets exist, and the first real use of last week's attachment
    // API from a script: an actor a script created has no attachTo of its own.
    const { scene, globals } = scriptGlobals();
    try {
        const v = globals.Chibi.random(3, 0, 0, 0);
        scene.flushPendingActors();
        scene.findByName('Chibi 3').getComponent(ChibiCharacter).rebuild();
        scene.flushPendingActors();

        globals.Scene.createActor('Torch', 0, 0);
        scene.flushPendingActors();
        const torch = globals.Scene.find('Torch');

        assert.equal(globals.Chibi.attach(v, 'Hand_R', torch), true);
        assert.equal(scene.findByName('Torch').parent.name, 'Socket_Hand_R');

        assert.equal(globals.Chibi.attach(v, 'Tail', torch), false);
        assert.ok(globals.Chibi.socket(v, 'Head'));
        assert.equal(globals.Chibi.socket(v, 'Tail'), null);
    } finally { scene.destroy(); }
});
