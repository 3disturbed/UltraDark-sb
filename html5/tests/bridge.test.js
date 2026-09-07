// -----------------------------------------------------------------------------
// The scripting contract — `src/scripting/bridge-api.json` lists every global,
// member and hook a script may use under either engine. This suite holds the
// browser bridge to that file in both directions: a member the file names must
// exist, and a member the bridge grows must be added to the file (and so to the
// C# bridge, whose own test reads the same file).
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
    Scene, Actor, Transform3D, Rigidbody2D, BoxCollider2D, SpriteRenderer, ScriptComponent,
    createScriptGlobals, wrapActor, unwrapActor, wrapCollisionData, SCRIPT_HOOKS, MouseButton, CollisionData,
} from '../src/index.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const contract = JSON.parse(fs.readFileSync(path.join(here, '../src/scripting/bridge-api.json'), 'utf8'));

/** Own property names of a proxy, getters included. */
function membersOf(object) {
    return Object.getOwnPropertyNames(object).sort();
}

function contractMembers(section) {
    return Object.keys(section).sort();
}

function scriptedActor() {
    const scene = new Scene('Contract');
    const actor = scene.addActor(new Actor('Scripted'));
    actor.addComponent(Transform3D);
    actor.addComponent(Rigidbody2D);
    scene.flushPendingActors();
    return { scene, actor };
}

test('every global the contract names exists with exactly the members it lists', () => {
    const { actor } = scriptedActor();
    const globals = createScriptGlobals(actor);

    for (const [name, members] of Object.entries(contract.globals)) {
        const value = globals[name];
        assert.ok(value && typeof value === 'object', `the '${name}' global is missing`);
        assert.deepEqual(membersOf(value), contractMembers(members),
            `'${name}' does not match bridge-api.json`);

        for (const [member, info] of Object.entries(members)) {
            if (info.kind === 'fn') {
                assert.equal(typeof value[member], 'function', `${name}.${member} should be a function`);
            }
        }
    }

    for (const name of Object.keys(contract.bare)) {
        assert.equal(typeof globals[name], 'function', `bare '${name}()' is missing`);
    }

    // Nothing the bridge installs may be absent from the contract.
    const expectedGlobals = [...Object.keys(contract.globals), ...Object.keys(contract.bare)].sort();
    assert.deepEqual(Object.keys(globals).sort(), expectedGlobals);
});

test('a found actor and a collision carry the shapes the contract lists', () => {
    const { scene, actor } = scriptedActor();
    const other = scene.addActor(new Actor('Other'));
    scene.flushPendingActors();

    const proxy = wrapActor(other);
    assert.deepEqual(membersOf(proxy), contractMembers(contract.actorProxy));
    assert.deepEqual(membersOf(proxy.transform), contractMembers(contract.actorProxyTransform));
    assert.equal(unwrapActor(proxy), other, 'a proxy unwraps to its actor');
    assert.equal(unwrapActor(createScriptGlobals(actor).actor), actor, 'the actor global unwraps too');

    const data = wrapCollisionData(new CollisionData({ other, relativeVelocity: 3 }));
    assert.deepEqual(membersOf(data), contractMembers(contract.collisionData));
    assert.equal(data.tag, 'Untagged');
    assert.equal(data.name, 'Other');

    other.destroy();
    scene.flushPendingActors();
    assert.equal(wrapActor(other), null, 'a destroyed actor wraps to null');
});

test('a flat script receives wrapped actors in its collision and trigger hooks', () => {
    // The C# bridge hands a script proxies; a raw engine Actor here would make
    // `other.transform.x` and `other.getComponent("ScriptComponent")` behave differently.
    const { scene, actor } = scriptedActor();
    const script = actor.addComponent(ScriptComponent);
    const enemy = scene.addActor(new Actor('Enemy'));
    enemy.tag = 'Enemy';
    const enemyScript = enemy.addComponent(ScriptComponent);
    scene.flushPendingActors();

    enemyScript.setSource('var hits = 0; function takeDamage(n) { hits += n; return hits; }');
    script.setSource(`
        var seen = [];
        function onCollisionEnter(other) {
            if (other.tag === 'Enemy') seen.push('hit:' + other.getComponent('ScriptComponent').invoke('takeDamage', 2));
        }
        function onTriggerEnter(other) { seen.push('trigger:' + other.name + ':' + typeof other.getComponent); }
    `);

    script.onCollisionEnter(new CollisionData({ other: enemy }));
    script.onTriggerEnter(enemy);
    assert.deepEqual(script.invoke('onCollisionEnter', wrapCollisionData(new CollisionData({ other: enemy }))), undefined);
    assert.equal(enemyScript.invoke('takeDamage', 0), 4);
});

test('the hook list is the contract hook list', () => {
    assert.deepEqual([...SCRIPT_HOOKS].sort(), contractMembers(contract.hooks));
});

test('mouse buttons agree with the browser and with C#', () => {
    // MouseEvent.button and Input/MouseButton.cs both put the middle button at 1 and the
    // right button at 2. This table once had them swapped.
    assert.equal(MouseButton.Left, 0);
    assert.equal(MouseButton.Middle, 1);
    assert.equal(MouseButton.Right, 2);
});

test('a script can create, extend and destroy actors and call into another script', () => {
    const { scene, actor } = scriptedActor();
    const script = actor.addComponent(ScriptComponent);
    scene.flushPendingActors();

    const messages = [];
    const globals = createScriptGlobals(actor, { log: (level, message) => messages.push(`${level}: ${message}`) });
    script.setSource('');   // the component itself is exercised below through the globals

    const spawned = globals.Scene.createActor('Crate', 3, 4);
    assert.ok(spawned, 'createActor returned nothing');
    scene.flushPendingActors();
    assert.equal(scene.findByName('Crate').transform.position.x, 3);

    const collider = globals.Scene.addComponent(spawned, 'BoxCollider2D', { Size: [10, 20], IsTrigger: true });
    assert.ok(collider, 'addComponent returned nothing');
    const raw = scene.findByName('Crate').getComponent(BoxCollider2D);
    assert.deepEqual(raw.size.toArray(), [10, 20], 'PascalCase file spellings apply');
    assert.equal(raw.isTrigger, true);

    // Both spellings reach one property, and a file-form value is coerced.
    const rb = globals.actor.getComponent('Rigidbody2D');
    rb.GravityScale = 2;
    assert.equal(rb.gravityScale, 2);
    rb.velocityX = 5;
    assert.equal(actor.getComponent(Rigidbody2D).linearVelocity.x, 5);

    const sprite = globals.Scene.addComponent(spawned, 'SpriteRenderer');
    sprite.tint = '#FF0000';
    assert.equal(scene.findByName('Crate').getComponent(SpriteRenderer).tint.r, 255, 'a hex string became a colour');

    assert.equal(globals.Scene.findAll('Crate').length, 1);
    assert.equal(globals.Scene.findFirstByTag('Untagged').name, 'Scripted');

    globals.Scene.addComponent('not an actor', 'SpriteRenderer');
    assert.ok(messages.some((m) => m.startsWith('warn:')), 'a bad argument warns rather than throws');

    globals.Scene.destroy(spawned);
    scene.flushPendingActors();
    assert.equal(scene.findByName('Crate'), null);

    // Cross-script calls: invoke() reaches any top-level function, not only the hooks.
    const target = scene.addActor(new Actor('Target'));
    const targetScript = target.addComponent(ScriptComponent);
    scene.flushPendingActors();
    targetScript.setSource(`
        var health = 10;
        function takeDamage(amount) { health -= amount; return health; }
        function helper() { function nested() {} return 1; }
    `);
    const found = globals.Scene.find('Target').getComponent('ScriptComponent');
    assert.equal(found.invoke('takeDamage', 4), 6);
    assert.equal(found.call('takeDamage', 1), 5);
    assert.equal(found.invoke('nested'), undefined, 'a nested function is not reachable');
    assert.equal(found.invoke('missing'), undefined);

    // The network stub keeps a multiplayer script alive as a solo game.
    assert.equal(globals.Network.isLocalPlayer(0), true);
    assert.equal(globals.Network.startServer(), false);
    assert.ok(messages.some((m) => m.includes('running solo')));
});

test('an invoke made before the script has loaded runs once it has, before onStart', () => {
    // Scripts are fetched in the browser, so the spawner that attaches one and configures it
    // in the same breath is early. Jint loads synchronously and runs the call at once; here it
    // waits, and both engines then run onStart with the configured values.
    const scene = new Scene('Pending');
    const actor = scene.addActor(new Actor('Tower'));
    const script = actor.addComponent(ScriptComponent);
    scene.flushPendingActors();

    assert.equal(script.invoke('configure', 7), undefined, 'nothing to run yet');

    script.setSource(`
        var range = 0, seenAtStart = -1, awakeSaw = -1;
        function configure(r) { range = r; }
        function onAwake() { awakeSaw = range; }
        function onStart() { seenAtStart = range; }
        function get() { return [awakeSaw, range, seenAtStart]; }
    `);
    script.start();
    assert.deepEqual(script.invoke('get'), [0, 7, 7], 'onAwake ran before configure, onStart after');
    assert.equal(script.invoke('configure', 9), undefined);
    assert.equal(script.invoke('get')[1], 9, 'once loaded, invoke is immediate');
});

test('a destroyed actor is not active', () => {
    // `active` is the ONLY liveness signal a script has: the contract has no
    // `isDestroyed`. So it has to be honest about both halves of what the engine
    // itself checks, which is `isActive && !destroyed` everywhere internally.
    //
    // It was not. `isActive` is a plain field and destroy() never cleared it, so
    // a script holding a reference to something long gone read `active === true`
    // forever. The shape that breaks is the common one:
    //
    //     if (!thing || thing.active !== true) { thing = Scene.findFirstByTag(...); }
    //
    // The guard never fires and the script goes on talking to a corpse. In
    // UltraDark-sb that made every boss after the first immune to the player's
    // gun, because the projectile pool's cached boss script belonged to the
    // previous one. It read exactly like a balance problem.
    const scene = new Scene('liveness');
    const actor = scene.addActor(new Actor('Boss'));
    scene.flushPendingActors();

    try {
        const proxy = wrapActor(actor);
        assert.equal(proxy.active, true, 'a live actor is active');

        actor.destroy();
        scene.flushPendingActors();

        assert.equal(proxy.active, false,
            'a reference held across a destroy must report inactive, not stale truth');

        // Switched off is a different thing from gone, and both read false.
        const other = scene.addActor(new Actor('Switched'));
        scene.flushPendingActors();
        const off = wrapActor(other);
        other.isActive = false;
        assert.equal(off.active, false, 'a deactivated actor is inactive');
        other.isActive = true;
        assert.equal(off.active, true, 'and can be switched back on');
    } finally {
        scene.destroy();
    }
});
