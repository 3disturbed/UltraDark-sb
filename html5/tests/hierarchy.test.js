// -----------------------------------------------------------------------------
// Actor-level attachment — the parent/child relationship a game reasons about, as
// opposed to the transform parenting it is built on. Mirrors
// SexyBiscuit.Tests/ActorHierarchyTests.cs case for case; the two engines have to
// agree, because a scene file written by one is loaded by the other.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import { Scene, Actor, Transform3D, Vector2, Vector3, serialize, deserialize } from '../src/index.js';

function add(scene, name, x = 0, y = 0) {
    const actor = new Actor(name);
    actor.transform.position = new Vector2(x, y);
    scene.addActor(actor);
    return actor;
}

test('attaching keeps the child where it stands', () => {
    // The default has to be "do not move me". A pickup snapped onto a moving player
    // should stay in the player's hand, not teleport to the player's origin the
    // instant it is attached — which is what keepWorldTransform:false would do.
    const scene = new Scene('attach');
    try {
        const parent = add(scene, 'Player', 100, 50);
        const child = add(scene, 'Pickup', 110, 50);
        scene.flushPendingActors();

        child.attachTo(parent);

        assert.equal(Math.round(child.transform.position.x), 110);
        assert.equal(Math.round(child.transform.localPosition.x), 10);
    } finally { scene.destroy(); }
});

test('attaching without keeping the world transform treats the offset as local', () => {
    // The socket case, and the one the scene loader takes: the file already says
    // "the muzzle sits 20 units along the barrel", so that number is the local
    // offset and must be applied as written rather than rebased.
    const scene = new Scene('socket');
    try {
        const barrel = add(scene, 'Barrel', 100, 0);
        const muzzle = add(scene, 'Muzzle', 20, 0);
        scene.flushPendingActors();

        muzzle.attachTo(barrel, false);

        assert.equal(Math.round(muzzle.transform.localPosition.x), 20);
        assert.equal(Math.round(muzzle.transform.position.x), 120);
    } finally { scene.destroy(); }
});

test('both transforms follow the attachment', () => {
    // The reason attachment lives on the Actor at all. A 3D actor parented through
    // the 2D transform alone inherits nothing, because no 3D renderer ever reads
    // that transform — the child would sit at the world origin however far the
    // parent moved.
    const scene = new Scene('both');
    try {
        const parent = add(scene, 'Ship');
        const child = add(scene, 'Turret');
        const parentSpatial = parent.addComponent(Transform3D);
        const childSpatial = child.addComponent(Transform3D);
        scene.flushPendingActors();

        child.attachTo(parent, false);
        parentSpatial.localPosition = new Vector3(0, 10, 0);
        childSpatial.localPosition = new Vector3(0, 2, 0);

        assert.equal(child.transform.parent, parent.transform);
        assert.equal(childSpatial.parent, parentSpatial);
        assert.equal(Math.round(childSpatial.position.y), 12);
    } finally { scene.destroy(); }
});

test('a 3D child of a 2D parent is not given an invented transform', () => {
    // Attaching must never add a Transform3D to the parent behind the game's back:
    // that would change what the parent serialises as, and turn a 2D actor into a
    // 3D one.
    const scene = new Scene('mixed');
    try {
        const parent = add(scene, 'Flat');
        const child = add(scene, 'Solid');
        const childSpatial = child.addComponent(Transform3D);
        scene.flushPendingActors();

        child.attachTo(parent);

        assert.equal(parent.getComponent(Transform3D), null);
        assert.equal(childSpatial.parent, null);
        assert.equal(child.parent, parent);
    } finally { scene.destroy(); }
});

test('a cycle is refused rather than hanging', () => {
    // Every walk in the engine — root, isActiveInHierarchy, descendants,
    // hierarchyPath — is an unguarded loop up or down the tree. A cycle is not a
    // wrong answer, it is a frozen tab, so it has to be impossible to create.
    const scene = new Scene('cycle');
    try {
        const a = add(scene, 'A');
        const b = add(scene, 'B');
        const c = add(scene, 'C');
        scene.flushPendingActors();

        b.attachTo(a);
        c.attachTo(b);

        assert.throws(() => a.attachTo(c), /cycle/);
        assert.throws(() => a.attachTo(a), /itself/);
    } finally { scene.destroy(); }
});

test('destroying a parent destroys its whole subtree', () => {
    // A tank's turret must not be left hanging in the air when the tank dies. The
    // alternative — every caller walking the subtree first — is the rule everybody
    // forgets exactly once.
    const scene = new Scene('destroy');
    try {
        const tank = add(scene, 'Tank');
        const turret = add(scene, 'Turret');
        const muzzle = add(scene, 'Muzzle');
        scene.flushPendingActors();

        turret.attachTo(tank);
        muzzle.attachTo(turret);

        tank.destroy();
        scene.flushPendingActors();

        assert.equal(tank.isDestroyed, true);
        assert.equal(turret.isDestroyed, true);
        assert.equal(muzzle.isDestroyed, true);
        assert.equal(scene.findByName('Muzzle'), null);
    } finally { scene.destroy(); }
});

test('detached children survive their parent', () => {
    // The documented escape hatch from the rule above. Debris that outlives the
    // thing it fell off has to be expressible.
    const scene = new Scene('detach');
    try {
        const ship = add(scene, 'Ship');
        const debris = add(scene, 'Debris');
        scene.flushPendingActors();

        debris.attachTo(ship);
        ship.detachChildren();
        ship.destroy();
        scene.flushPendingActors();

        assert.equal(ship.isDestroyed, true);
        assert.equal(debris.isDestroyed, false);
        assert.equal(debris.parent, null);
    } finally { scene.destroy(); }
});

test('a destroyed child leaves its parent’s child list', () => {
    // Otherwise a surviving parent holds a destroyed shell forever, and every
    // `for (const c of actor.children)` in a game has to null-check it.
    const scene = new Scene('orphan');
    try {
        const parent = add(scene, 'Parent');
        const child = add(scene, 'Child');
        scene.flushPendingActors();

        child.attachTo(parent);
        child.destroy();
        scene.flushPendingActors();

        assert.equal(parent.children.length, 0);
        assert.equal(parent.isDestroyed, false);
    } finally { scene.destroy(); }
});

test('isActiveInHierarchy follows every ancestor', () => {
    // isActive answers "was this actor switched off", which is not the question
    // gameplay code asks. A child of a disabled parent is not running either.
    const scene = new Scene('active');
    try {
        const root = add(scene, 'Root');
        const mid = add(scene, 'Mid');
        const leaf = add(scene, 'Leaf');
        scene.flushPendingActors();

        mid.attachTo(root);
        leaf.attachTo(mid);

        assert.equal(leaf.isActiveInHierarchy, true);
        root.isActive = false;
        assert.equal(leaf.isActive, true);
        assert.equal(leaf.isActiveInHierarchy, false);
    } finally { scene.destroy(); }
});

test('findChild and paths agree with each other', () => {
    const scene = new Scene('find');
    try {
        const tank = add(scene, 'Tank');
        const turret = add(scene, 'Turret');
        const muzzle = add(scene, 'Muzzle');
        scene.flushPendingActors();

        turret.attachTo(tank);
        muzzle.attachTo(turret);

        assert.equal(tank.findChild('Turret'), turret);
        assert.equal(tank.findChild('Muzzle'), null);
        assert.equal(tank.findChild('Muzzle', true), muzzle);
        assert.equal(tank.findChildByPath('Turret/Muzzle'), muzzle);
        assert.equal(muzzle.hierarchyPath, 'Tank/Turret/Muzzle');
        assert.equal(muzzle.root, tank);
        assert.deepEqual([...tank.descendants()].map((a) => a.name), ['Turret', 'Muzzle']);
    } finally { scene.destroy(); }
});

test('a round trip preserves the hierarchy and its local offsets', () => {
    // The point of nesting children in the scene file rather than writing a parent
    // id: a saved subtree reloads with the same shape and the same local offsets,
    // and a child is never loaded twice because it appeared both in the layer and
    // in its parent.
    const scene = new Scene('roundtrip');
    try {
        const tank = add(scene, 'Tank', 100, 0);
        const turret = add(scene, 'Turret', 100, 10);
        const muzzle = add(scene, 'Muzzle', 100, 25);
        scene.flushPendingActors();

        turret.attachTo(tank);
        muzzle.attachTo(turret);

        const reloaded = deserialize(serialize(scene));
        try {
            const loadedTank = reloaded.findByName('Tank');
            assert.equal(loadedTank.children.length, 1);

            const loadedTurret = loadedTank.findChild('Turret');
            const loadedMuzzle = loadedTank.findChild('Muzzle', true);

            assert.equal(Math.round(loadedTurret.transform.localPosition.y), 10);
            assert.equal(Math.round(loadedMuzzle.transform.localPosition.y), 15);
            assert.equal(Math.round(loadedMuzzle.transform.position.y), 25);
            assert.equal(Math.round(loadedMuzzle.transform.position.x), 100);

            // Each actor appears exactly once in the scene, not once per level it
            // was written at.
            assert.equal(reloaded.layers.reduce((n, l) => n + l.actors.length, 0), 3);
        } finally { reloaded.destroy(); }
    } finally { scene.destroy(); }
});

test('the two engines write the same nested shape', () => {
    // The C# writer puts `children` last, after `components`. A file written by one
    // engine and re-saved by the other should not reorder keys, because that turns
    // every save into a whole-file diff.
    const scene = new Scene('shape');
    try {
        const parent = add(scene, 'Parent', 5, 0);
        const child = add(scene, 'Child', 7, 0);
        scene.flushPendingActors();
        child.attachTo(parent);

        const dto = JSON.parse(serialize(scene));
        // A new Scene comes with the standard layers, most of them empty.
        const actors = dto.layers.flatMap((l) => l.actors);
        assert.equal(actors.length, 1);
        assert.deepEqual(Object.keys(actors[0]).slice(-2), ['components', 'children']);
        assert.equal(actors[0].children[0].name, 'Child');
        assert.equal(actors[0].children[0].position[0], 2);
    } finally { scene.destroy(); }
});
