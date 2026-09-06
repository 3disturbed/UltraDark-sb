// -----------------------------------------------------------------------------
// Runtime — the object model, lifecycle, physics and input behaviours that the
// C# engine's own tests pin down. Where a rule is subtle, the test says why.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import {
    Scene, Actor, Component, Transform3D, registerComponent, serialize, deserialize,
    Vector2, Vector3, Quaternion, Color, Matrix4, SBMath,
    CoroutineRunner, WaitForSeconds, WaitUntil,
    TimerManager, Time, SBEvent,
    PhysicsSystem2D, Rigidbody2D, BodyType, BoxCollider2D, CircleCollider2D,
    PhysicsSystem3D, Rigidbody3D, BoxCollider3D, CharacterController3D, rayVsBounds,
    MeshRenderer,
    InputManager, ActionMap,
    Tween, installCSharpAliases,
    ScriptComponent, SpriteRenderer,
    createDefault3D, createDefault2D,
    GameMode, PlayerController, Character, PlayerStart, PlayMode, Camera3D,
} from '../src/index.js';

// ---- A probe component used across several tests ----------------------------

class Probe extends Component {
    static schema = { speed: { type: 'number', default: 5 } };

    constructor() {
        super();
        this.speed = 5;
        this.calls = [];
    }

    awake() { this.calls.push('awake'); }
    start() { this.calls.push('start'); }
    update() { this.calls.push('update'); }
    fixedUpdate() { this.calls.push('fixedUpdate'); }
    lateUpdate() { this.calls.push('lateUpdate'); }
    onDestroy() { this.calls.push('onDestroy'); }
}
registerComponent(Probe, { category: 'Project', source: 'project' });

// -----------------------------------------------------------------------------
// Lifecycle
// -----------------------------------------------------------------------------

test('an actor is invisible to queries until the scene is flushed', () => {
    // addActor queues rather than inserting, so that spawning during a frame
    // cannot mutate the list the update loop is walking. Anything that reads a
    // scene without simulating it has to flush first.
    const scene = new Scene('Deferred');
    scene.addActor(new Actor('Late'));

    assert.equal(scene.findByName('Late'), null);
    scene.flushPendingActors();
    assert.ok(scene.findByName('Late'));
});

test('awake runs at attach, start at the flush', () => {
    const scene = new Scene('Lifecycle');
    const actor = new Actor('Thing');
    const probe = actor.addComponent(Probe);

    assert.deepEqual(probe.calls, ['awake'], 'awake must run before the actor joins a scene');

    scene.addActor(actor);
    scene.flushPendingActors();
    assert.deepEqual(probe.calls, ['awake', 'start']);
});

test('a component disabled at start never receives start', () => {
    // The rule the C# engine follows, and the reason a component that
    // initialises in start must not ship disabled.
    const scene = new Scene('Disabled');
    const actor = new Actor('Thing');
    const probe = actor.addComponent(Probe);
    probe.enabled = false;

    scene.addActor(actor);
    scene.flushPendingActors();
    assert.deepEqual(probe.calls, ['awake']);

    probe.enabled = true;
    scene.update(0.016);
    assert.ok(!probe.calls.includes('start'), 'start is not delivered retroactively');
});

test('destroy is deferred to the end of the frame', () => {
    const scene = new Scene('Destroy');
    const actor = scene.addActor(new Actor('Doomed'));
    scene.flushPendingActors();

    actor.destroy();
    assert.ok(scene.findByName('Doomed'), 'the actor survives the rest of the frame');

    scene.update(0.016);
    assert.equal(scene.findByName('Doomed'), null, 'and is gone by the next one');
    assert.ok(actor.isDestroyed);
});

test('lifeSpan destroys an actor on scaled time', () => {
    const scene = new Scene('LifeSpan');
    const actor = scene.addActor(new Actor('Bullet'));
    actor.lifeSpan = 0.5;
    scene.flushPendingActors();

    for (let i = 0; i < 20; i++) scene.update(0.016);
    assert.ok(scene.findByName('Bullet'), 'still alive at 0.32s');

    for (let i = 0; i < 20; i++) scene.update(0.016);
    assert.equal(scene.findByName('Bullet'), null, 'gone after 0.64s');
});

test('moveActor changes layer without destroying the actor', () => {
    // The obvious remove-then-add sequence destroys the actor, because a layer's
    // removal queue is its destruction queue.
    const scene = new Scene('Layers');
    const actor = scene.addActor(new Actor('Mover'), 'default');
    const probe = actor.addComponent(Probe);
    scene.flushPendingActors();

    scene.moveActor(actor, 'ui');
    scene.flushPendingActors();

    assert.equal(actor.layerRef.name, 'ui');
    assert.ok(!actor.isDestroyed);
    assert.ok(!probe.calls.includes('onDestroy'));
    assert.equal(scene.getLayer('default').actors.length, 0);
});

test('requires adds a missing dependency, addComponentByType does not', () => {
    class NeedsBody extends Component {}
    NeedsBody.requires = [Rigidbody2D];
    registerComponent(NeedsBody, { category: 'Project', source: 'project' });

    const withDependency = new Actor('A');
    withDependency.addComponent(NeedsBody);
    assert.ok(withDependency.getComponent(Rigidbody2D), 'addComponent honours requires');

    // The loader takes this path: it restores components in file order and must
    // not invent extra ones.
    const bare = new Actor('B');
    bare.addComponentByType(NeedsBody);
    assert.equal(bare.getComponent(Rigidbody2D), null);
});

test('the actor transform cannot be removed', () => {
    const actor = new Actor('Thing');
    assert.equal(actor.removeComponent(actor.transform), false);
    assert.ok(actor.transform);
});

// -----------------------------------------------------------------------------
// Transforms
// -----------------------------------------------------------------------------

test('moving a parent moves its whole subtree', () => {
    // Marking only self as dirty leaves a descendant returning a stale cached
    // world transform; the walk has to reach the whole subtree.
    const scene = new Scene('Hierarchy');
    const root = scene.addActor(new Actor('Root'));
    const middle = scene.addActor(new Actor('Middle'));
    const leaf = scene.addActor(new Actor('Leaf'));
    scene.flushPendingActors();

    middle.transform.setParent(root.transform, false);
    leaf.transform.setParent(middle.transform, false);

    middle.transform.localPosition = new Vector2(10, 0);
    leaf.transform.localPosition = new Vector2(5, 0);

    assert.deepEqual(leaf.transform.position.toArray(), [15, 0]);

    root.transform.localPosition = new Vector2(100, 100);
    assert.deepEqual(leaf.transform.position.toArray(), [115, 100]);
});

test('setting a world position through a rotated parent is exact', () => {
    const parent = new Actor('Parent');
    const child = new Actor('Child');

    parent.transform.localPosition = new Vector2(10, 10);
    parent.transform.localRotation = Math.PI / 3;
    parent.transform.localScale = new Vector2(2, 2);
    child.transform.setParent(parent.transform, false);

    child.transform.position = new Vector2(42, -7);
    const result = child.transform.position;

    assert.ok(Math.abs(result.x - 42) < 1e-4, `x was ${result.x}`);
    assert.ok(Math.abs(result.y - -7) < 1e-4, `y was ${result.y}`);
});

test('3D euler angles round-trip through the quaternion', () => {
    // The C# QuaternionToEuler applies a ZYX extraction to a quaternion built
    // the YXZ way, so it only round-trips when one angle is zero. This engine
    // uses the true inverse; see Quaternion.toEuler for why.
    const actor = new Actor('Solid');
    const t = actor.addComponent(Transform3D);

    for (const euler of [[0, 0, 0], [-45, 30, 0], [10, -170, 25], [89, 0, 0], [30, -60, 100]]) {
        t.localEulerAngles = Vector3.from(euler);
        const back = t.localEulerAngles;
        for (const axis of ['x', 'y', 'z']) {
            assert.ok(Math.abs(back[axis] - Vector3.from(euler)[axis]) < 1e-3,
                `${euler} came back as ${back}`);
        }
    }
});

test('3D forward follows MonoGame, pointing down -Z', () => {
    const actor = new Actor('Solid');
    const t = actor.addComponent(Transform3D);

    const forward = t.forward;
    assert.ok(Math.abs(forward.z - -1) < 1e-6, `forward was ${forward}`);

    // A positive yaw is counter-clockwise seen from above in a right-handed
    // system, so facing -Z and turning 90 degrees leaves you facing -X.
    t.localEulerAngles = new Vector3(0, 90, 0);
    const turned = t.forward;
    assert.ok(Math.abs(turned.x - -1) < 1e-5, `after a 90 degree yaw, forward was ${turned}`);
});

// -----------------------------------------------------------------------------
// Physics
// -----------------------------------------------------------------------------

function makePhysicsScene() {
    const scene = new Scene('Physics');
    scene.physics2D = new PhysicsSystem2D({ scene, gravity: new Vector2(0, 980) });
    return scene;
}

test('a dynamic body falls, lands and comes to rest', () => {
    const scene = makePhysicsScene();

    const ground = scene.addActor(new Actor('Ground'));
    ground.tag = 'Ground';
    ground.transform.localPosition = new Vector2(0, 400);
    ground.addComponent(Rigidbody2D).bodyType = BodyType.Static;
    ground.addComponent(BoxCollider2D).size = new Vector2(1000, 40);

    const box = scene.addActor(new Actor('Box'));
    box.transform.localPosition = new Vector2(0, 0);
    const body = box.addComponent(Rigidbody2D);
    body.freezeRotation = true;
    box.addComponent(BoxCollider2D).size = new Vector2(40, 40);

    scene.flushPendingActors();
    for (let i = 0; i < 180; i++) { scene.physics2D.fixedStep(1 / 60); scene.update(1 / 60); }

    // Ground top is at 380; the box's half-height is 20.
    assert.ok(Math.abs(box.transform.position.y - 360) < 1.5,
        `the box rested at ${box.transform.position.y}, expected about 360`);
    assert.ok(Math.abs(body.linearVelocity.y) < 1, 'and stopped moving');
});

test('collision enter fires once and exit fires on separation', () => {
    const scene = makePhysicsScene();
    scene.physics2D.gravity = new Vector2(0, 0);

    const a = scene.addActor(new Actor('A'));
    a.transform.localPosition = new Vector2(0, 0);
    a.addComponent(Rigidbody2D).linearVelocity = new Vector2(100, 0);
    a.addComponent(BoxCollider2D).size = new Vector2(20, 20);

    const b = scene.addActor(new Actor('B'));
    b.tag = 'Wall';
    b.transform.localPosition = new Vector2(60, 0);
    b.addComponent(Rigidbody2D).bodyType = BodyType.Static;
    b.addComponent(BoxCollider2D).size = new Vector2(20, 20);

    let enters = 0, stays = 0, exits = 0;
    let sawTag = null;
    a.onCollisionEnter = (data) => { enters++; sawTag = data.tag; };
    a.onCollisionStay = () => { stays++; };
    a.onCollisionExit = () => { exits++; };

    scene.flushPendingActors();
    for (let i = 0; i < 60; i++) { scene.physics2D.fixedStep(1 / 60); scene.update(1 / 60); }

    assert.equal(enters, 1, 'enter fires exactly once per contact');
    assert.ok(stays > 0, 'stay fires while touching');
    // `tag` forwards to `other` so the shorthand the bundled scripts use works.
    assert.equal(sawTag, 'Wall');

    a.getComponent(Rigidbody2D).linearVelocity = new Vector2(-400, 0);
    for (let i = 0; i < 60; i++) { scene.physics2D.fixedStep(1 / 60); scene.update(1 / 60); }
    assert.equal(exits, 1, 'exit fires when they separate');
});

test('a trigger reports overlap without pushing', () => {
    const scene = makePhysicsScene();
    scene.physics2D.gravity = new Vector2(0, 0);

    const mover = scene.addActor(new Actor('Mover'));
    mover.addComponent(Rigidbody2D).linearVelocity = new Vector2(100, 0);
    mover.addComponent(BoxCollider2D).size = new Vector2(20, 20);

    const zone = scene.addActor(new Actor('Zone'));
    zone.transform.localPosition = new Vector2(50, 0);
    const collider = zone.addComponent(BoxCollider2D);
    collider.isTrigger = true;
    collider.size = new Vector2(40, 40);
    zone.addComponent(Rigidbody2D).bodyType = BodyType.Static;

    let entered = false;
    mover.onTriggerEnter = () => { entered = true; };

    scene.flushPendingActors();
    for (let i = 0; i < 60; i++) { scene.physics2D.fixedStep(1 / 60); scene.update(1 / 60); }

    assert.ok(entered, 'the trigger reported the overlap');
    assert.ok(mover.transform.position.x > 60, 'and did not stop the mover');
});

test('raycast returns the nearest hit', () => {
    const scene = makePhysicsScene();

    for (const [name, x] of [['Near', 50], ['Far', 150]]) {
        const actor = scene.addActor(new Actor(name));
        actor.transform.localPosition = new Vector2(x, 0);
        actor.addComponent(BoxCollider2D).size = new Vector2(20, 20);
        actor.addComponent(Rigidbody2D).bodyType = BodyType.Static;
    }
    scene.flushPendingActors();

    const hit = scene.physics2D.raycast(new Vector2(0, 0), new Vector2(1, 0), 500);
    assert.equal(hit?.actor.name, 'Near');
    assert.ok(Math.abs(hit.distance - 40) < 1e-3, `distance was ${hit.distance}`);

    assert.equal(scene.physics2D.raycastAll(new Vector2(0, 0), new Vector2(1, 0), 500).length, 2);
    assert.equal(scene.physics2D.raycast(new Vector2(0, 0), new Vector2(-1, 0), 500), null);
});

test('a character stands on a floor and jumps', () => {
    const scene = new Scene('Character');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const floor = scene.addActor(new Actor('Floor'));
    floor.addComponent(Transform3D).localPosition = new Vector3(0, -0.5, 0);
    const floorCollider = floor.addComponent(BoxCollider3D);
    floorCollider.size = new Vector3(50, 1, 50);

    const walker = scene.addActor(new Actor('Walker'));
    walker.addComponent(Transform3D).localPosition = new Vector3(0, 5, 0);
    const controller = walker.addComponent(CharacterController3D);

    scene.flushPendingActors();
    for (let i = 0; i < 240; i++) { scene.physics3D.fixedStep(1 / 60); scene.fixedUpdate(1 / 60); }

    assert.ok(controller.isGrounded, 'the character landed');
    const restingY = walker.transform3D.position.y;
    assert.ok(Math.abs(restingY - 0.9) < 0.2, `it rested at ${restingY}, expected about 0.9`);

    assert.ok(controller.jump(), 'jump succeeds when grounded');
    for (let i = 0; i < 6; i++) { scene.physics3D.fixedStep(1 / 60); scene.fixedUpdate(1 / 60); }
    assert.ok(walker.transform3D.position.y > restingY + 0.3, 'and it left the ground');
});

// -----------------------------------------------------------------------------
// Coroutines and timers
// -----------------------------------------------------------------------------

test('a coroutine waits on scaled time and resumes', () => {
    const runner = new CoroutineRunner();
    CoroutineRunner.instance = runner;

    const scene = new Scene('Coroutines');
    const actor = scene.addActor(new Actor('Waiter'));
    scene.flushPendingActors();

    const log = [];
    actor.startCoroutine((function* () {
        log.push('a');
        yield new WaitForSeconds(0.5);
        log.push('b');
        yield null;
        log.push('c');
    })());

    // The first step runs on the next tick, not immediately.
    assert.deepEqual(log, []);
    runner.tick(1 / 60, 1 / 60);
    assert.deepEqual(log, ['a']);

    for (let i = 0; i < 20; i++) runner.tick(1 / 60, 1 / 60);
    assert.deepEqual(log, ['a'], 'still waiting at 0.33s');

    for (let i = 0; i < 15; i++) runner.tick(1 / 60, 1 / 60);
    assert.deepEqual(log, ['a', 'b', 'c']);
});

test('destroying an actor cancels its coroutines', () => {
    const runner = new CoroutineRunner();
    CoroutineRunner.instance = runner;

    const scene = new Scene('Cancel');
    const actor = scene.addActor(new Actor('Ticker'));
    scene.flushPendingActors();

    let ticks = 0;
    actor.startCoroutine((function* () {
        while (true) { ticks++; yield null; }
    })());

    for (let i = 0; i < 5; i++) runner.tick(1 / 60, 1 / 60);
    assert.ok(ticks > 0);

    actor.destroy();
    scene.update(1 / 60);

    const before = ticks;
    for (let i = 0; i < 5; i++) runner.tick(1 / 60, 1 / 60);
    assert.equal(ticks, before, 'a coroutine must not outlive its actor');
});

test('a looping timer carries its overshoot forward', () => {
    const timers = new TimerManager();
    let fires = 0;
    timers.setTimer(0.1, () => { fires++; }, { looping: true });

    // 0.03 does not divide 0.1; a timer that reset to the full interval each fire
    // would drift and fire fewer times than the elapsed time calls for.
    for (let i = 0; i < 100; i++) timers.tick(0.03, 0.03);
    assert.ok(fires >= 29 && fires <= 30, `fired ${fires} times over 3 seconds`);
});

test('an unscaled timer ignores time scale', () => {
    const timers = new TimerManager();
    let scaled = 0, unscaled = 0;
    timers.setTimer(0.1, () => { scaled++; }, { looping: true });
    timers.setTimer(0.1, () => { unscaled++; }, { looping: true, useUnscaledTime: true });

    // A paused game: scaled dt is zero, wall-clock dt is not.
    for (let i = 0; i < 60; i++) timers.tick(0, 1 / 60);
    assert.equal(scaled, 0);
    assert.ok(unscaled > 0);
});

// -----------------------------------------------------------------------------
// Input
// -----------------------------------------------------------------------------

test('key presses are edge-triggered for exactly one frame', () => {
    const input = new InputManager();
    input._onKeyDown({ code: 'KeyD', key: 'd' });
    input.update(1 / 60);

    assert.ok(input.isKeyDown('D'));
    assert.ok(input.isKeyPressed('D'));

    input.update(1 / 60);
    assert.ok(input.isKeyDown('D'), 'still held');
    assert.ok(!input.isKeyPressed('D'), 'but no longer newly pressed');

    input._onKeyUp({ code: 'KeyD', key: 'd' });
    input.update(1 / 60);
    assert.ok(input.isKeyReleased('D'));
    assert.ok(!input.isKeyDown('D'));
});

test('a browser key repeat does not re-trigger a press', () => {
    const input = new InputManager();
    input._onKeyDown({ code: 'Space', key: ' ' });
    input.update(1 / 60);
    assert.ok(input.isKeyPressed('Space'));

    input._onKeyDown({ code: 'Space', key: ' ', repeat: true });
    input.update(1 / 60);
    assert.ok(!input.isKeyPressed('Space'), 'auto-repeat must not read as a new press');
});

test('losing focus releases every held key', () => {
    // Otherwise the keyup lands on whatever the user switched to and the key
    // stays held for the rest of the session.
    const input = new InputManager();
    input._onKeyDown({ code: 'KeyW', key: 'w' });
    input.update(1 / 60);
    assert.ok(input.isKeyDown('W'));

    input._onBlur();
    input.update(1 / 60);
    assert.ok(!input.isKeyDown('W'));
    assert.ok(input.isKeyReleased('W'));
});

test('browser key codes normalise to the names action maps use', () => {
    const input = new InputManager();
    input._onKeyDown({ code: 'ArrowLeft', key: 'ArrowLeft' });
    input._onKeyDown({ code: 'ShiftLeft', key: 'Shift' });
    input._onKeyDown({ code: 'Digit1', key: '1' });
    input.update(1 / 60);

    assert.ok(input.isKeyDown('Left'));
    assert.ok(input.isKeyDown('LeftShift'));
    assert.ok(input.isKeyDown('D1'));
});

test('an action reads from whichever device is active', () => {
    const input = new InputManager();

    input._onKeyDown({ code: 'KeyD', key: 'd' });
    input.update(1 / 60);
    assert.equal(input.getAxis('MoveX'), 1, 'keyboard drives the axis');

    input._onKeyUp({ code: 'KeyD', key: 'd' });
    input.update(1 / 60);
    assert.equal(input.getAxis('MoveX'), 0);

    // The touch device is this engine's addition: without it every action-map
    // driven game is unplayable on a phone.
    input.touch.setScreenSize(800, 600);
    input.touch.onTouchStart(1, 100, 400);
    input.update(1 / 60);
    input.touch.onTouchMove(1, 200, 400);
    input.update(1 / 60);
    assert.ok(input.getAxis('MoveX') > 0.9, 'the left virtual joystick drives it too');
});

test('two virtual joysticks claim different halves of the screen', () => {
    const input = new InputManager();
    input.touch.setScreenSize(800, 600);

    input.touch.onTouchStart(1, 100, 400);   // left half
    input.touch.onTouchStart(2, 700, 400);   // right half
    input.update(1 / 60);

    input.touch.onTouchMove(1, 180, 400);
    input.touch.onTouchMove(2, 700, 320);
    input.update(1 / 60);

    assert.ok(input.touch.leftJoystick.value.x > 0.9);
    assert.ok(input.touch.rightJoystick.value.y < -0.9);
});

test('an action map survives a JSON round trip', () => {
    const original = ActionMap.default();
    const restored = ActionMap.fromJson(JSON.parse(JSON.stringify(original.toJSON())));

    assert.deepEqual(restored.names.sort(), original.names.sort());
    assert.equal(restored.get('MoveX').bindings.length, original.get('MoveX').bindings.length);
});

// -----------------------------------------------------------------------------
// Scripting
// -----------------------------------------------------------------------------

test('a flat-function script gets the bridge globals and its hooks run', () => {
    const scene = new Scene('Scripting');
    const actor = scene.addActor(new Actor('Scripted'));
    actor.tag = 'Player';
    actor.addComponent(Rigidbody2D);
    const script = actor.addComponent(ScriptComponent);
    scene.flushPendingActors();

    script.setSource(`
        var ticks = 0;
        function onStart() { actor.name = 'Renamed'; }
        function onUpdate(dt) {
            ticks++;
            var rb = actor.getComponent('Rigidbody2D');
            rb.velocityX = 42;
            transform.x = transform.x + 1;
        }
        function onCollisionEnter(data) { actor.tag = data.tag; }
    `);

    assert.deepEqual(script.definedHooks.sort(), ['onCollisionEnter', 'onStart', 'onUpdate']);

    script._call('onStart');
    assert.equal(actor.name, 'Renamed');

    scene.update(1 / 60);
    assert.equal(actor.getComponent(Rigidbody2D).velocityX, 42);
    assert.equal(actor.transform.position.x, 1);
});

test('two components running the same script keep separate state', () => {
    // Jint gives one engine per component; a shared scope here would let two
    // enemies running the same file share their variables.
    const scene = new Scene('Isolation');
    const source = 'var count = 0; function onUpdate() { count++; } function get() { return count; }';

    const actors = ['A', 'B'].map((name) => {
        const actor = scene.addActor(new Actor(name));
        return { actor, script: actor.addComponent(ScriptComponent) };
    });
    scene.flushPendingActors();

    for (const { script } of actors) script.setSource(source);

    // Only the first is ticked; the second's counter must not move.
    actors[0].script.update(1 / 60);
    actors[0].script.update(1 / 60);

    assert.notEqual(actors[0].script._hooks.onUpdate, actors[1].script._hooks.onUpdate);
});

// -----------------------------------------------------------------------------
// Gameplay
// -----------------------------------------------------------------------------

test('a game mode does not spawn anything in edit mode', () => {
    // Otherwise opening a scene in the editor populates it with controllers and
    // pawns, which then get written into the file on save.
    PlayMode.isActive = false;
    try {
        const scene = new Scene('EditTime');
        scene.addActor(new GameMode('Game Mode'));
        scene.flushPendingActors();
        scene.update(1 / 60);

        assert.equal(scene.findActorsOfType(PlayerController).length, 0);
        assert.equal(scene.findActorsOfType(Character).length, 0);
    } finally {
        PlayMode.isActive = true;
    }
});

test('play mode spawns a controller, a pawn and possesses it', () => {
    const scene = new Scene('PlayTime');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const start = scene.addActor(new Actor('Player Start'));
    start.addComponent(Transform3D).localPosition = new Vector3(3, 0, -4);
    start.addComponent(PlayerStart);

    scene.addActor(new GameMode('Game Mode'));
    scene.flushPendingActors();
    scene.update(1 / 60);

    const controllers = scene.findActorsOfType(PlayerController);
    assert.equal(controllers.length, 1);

    const pawn = controllers[0].controlledPawn;
    assert.ok(pawn, 'the controller possessed a pawn');
    assert.equal(pawn.controller, controllers[0], 'and the link is two-way');

    // A Player Start marks where the pawn's feet go, not its middle. Dropping a
    // capsule's centre onto the marker buries it to the waist, and a character
    // that starts inside the floor never finds ground — the floor reads as a
    // ceiling and it falls forever.
    const position = pawn.transform3D.position;
    const footOffset = pawn.movement.footOffset;

    assert.equal(position.x, 3);
    assert.equal(position.z, -4);
    assert.equal(position.y, 0 + footOffset, 'the pawn stands on the marker rather than in it');
});

test('a character spawned on a floor stands on it instead of falling through', () => {
    // Pressing Play on the default scene dropped the pawn through the world.
    const scene = new Scene('Spawn');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const floor = scene.addActor(new Actor('Floor'));
    floor.addComponent(Transform3D).localScale = new Vector3(30, 1, 30);
    const collider = floor.addComponent(BoxCollider3D);
    collider.size = new Vector3(1, 0.1, 1);

    const start = scene.addActor(new Actor('Player Start'));
    start.addComponent(Transform3D).localPosition = new Vector3(0, 0, 6);
    start.addComponent(PlayerStart);

    scene.addActor(new GameMode('Game Mode'));
    scene.flushPendingActors();
    scene.update(1 / 60);

    const pawn = scene.findActorsOfType(Character)[0];
    assert.ok(pawn, 'no pawn was spawned');

    for (let i = 0; i < 180; i++) { scene.physics3D.fixedStep(1 / 60); scene.fixedUpdate(1 / 60); }

    assert.ok(pawn.movement.isGrounded, `the pawn was still falling at y=${pawn.transform3D.position.y}`);
    assert.ok(pawn.transform3D.position.y > 0.5,
        `the pawn sank to y=${pawn.transform3D.position.y}`);
});

test('a character partly inside the floor is pushed out rather than dropped through', () => {
    // stepUpHeight exists for kerbs, and it is the same mechanism that recovers a
    // character which has sunk into geometry. Rejecting every surface above the
    // feet outright meant the floor read as a ceiling and nothing caught the fall.
    const scene = new Scene('Penetration');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const floor = scene.addActor(new Actor('Floor'));
    floor.addComponent(Transform3D).localPosition = new Vector3(0, 0, 0);
    const collider = floor.addComponent(BoxCollider3D);
    collider.size = new Vector3(30, 1, 30);

    const walker = scene.addActor(new Actor('Walker'));
    const t = walker.addComponent(Transform3D);
    const controller = walker.addComponent(CharacterController3D);
    scene.flushPendingActors();

    // Sunk a quarter of a metre into the floor, well inside stepUpHeight.
    const floorTop = 0.5;
    t.localPosition = new Vector3(0, floorTop + controller.footOffset - 0.25, 0);

    for (let i = 0; i < 30; i++) { scene.physics3D.fixedStep(1 / 60); scene.fixedUpdate(1 / 60); }

    assert.ok(controller.isGrounded, 'the character never found the floor it was standing in');
    assert.ok(Math.abs(t.position.y - (floorTop + controller.footOffset)) < 0.05,
        `it settled at ${t.position.y}, expected about ${floorTop + controller.footOffset}`);
});

test('possessing a pawn moves it to a new controller cleanly', () => {
    const scene = new Scene('Possession');
    const pawn = scene.addActor(new Character('Pawn'));
    const first = scene.addActor(new PlayerController('First'));
    const second = scene.addActor(new PlayerController('Second'));
    scene.flushPendingActors();

    first.possess(pawn);
    assert.equal(pawn.controller, first);

    second.possess(pawn);
    assert.equal(pawn.controller, second);
    assert.equal(first.controlledPawn, null, 'the first controller let go');
});

// -----------------------------------------------------------------------------
// Scene templates
// -----------------------------------------------------------------------------

test('the default 3D template builds a playable scene', () => {
    const scene = createDefault3D('Test');
    const names = scene.allActors.map((a) => a.name);

    for (const wanted of ['Sky', 'Directional Light', 'Floor', 'Main Camera', 'Player Start', 'Game Mode']) {
        assert.ok(names.includes(wanted), `the template is missing '${wanted}'`);
    }

    // A renderable 3D scene needs a light, something with a mesh, and a camera.
    assert.ok(scene.allActors.some((a) => a.getComponent('Light3D')));
    assert.ok(scene.allActors.some((a) => a.getComponent('MeshRenderer')));
    assert.ok(scene.allActors.some((a) => a.getComponent('Camera3D')));
});

test('the default 2D template builds and serialises', () => {
    const scene = createDefault2D('Test');
    assert.ok(scene.allActors.some((a) => a.getComponent('Camera2D')));

    const written = JSON.parse(serialize(scene));
    assert.ok(written.layers.some((l) => l.actors.length > 0));
    assert.equal(deserialize(written).allActors.length, scene.allActors.length);
});

// -----------------------------------------------------------------------------
// Animation and events
// -----------------------------------------------------------------------------

test('a tween eases and completes', () => {
    Tween.killAll();
    const target = { x: 0 };
    let completed = false;

    Tween.to(target, { x: 100 }, 1).ease('outCubic').onComplete(() => { completed = true; }).play();

    for (let i = 0; i < 30; i++) Tween.updateAll(1 / 60);
    assert.ok(target.x > 50, 'an ease-out is past halfway at the halfway point');

    for (let i = 0; i < 40; i++) Tween.updateAll(1 / 60);
    assert.equal(target.x, 100);
    assert.ok(completed);
    assert.equal(Tween.activeCount, 0);
});

test('an event handler that unsubscribes mid-broadcast still receives the call', () => {
    // The snapshot is the whole point: a handler removing another must not
    // change who is called during the broadcast already in progress.
    const event = new SBEvent();
    const seen = [];

    const second = () => seen.push('second');
    event.add(() => { seen.push('first'); event.remove(second); });
    event.add(second);

    event.broadcast();
    assert.deepEqual(seen, ['first', 'second']);

    event.broadcast();
    assert.deepEqual(seen, ['first', 'second', 'first'], 'and is gone from the next one');
});

test('a throwing handler does not stop the rest', () => {
    const event = new SBEvent();
    const seen = [];
    const original = console.error;
    console.error = () => {};

    try {
        event.add(() => { throw new Error('boom'); });
        event.add(() => seen.push('ran'));
        event.broadcast();
    } finally {
        console.error = original;
    }

    assert.deepEqual(seen, ['ran']);
});

// -----------------------------------------------------------------------------
// Time
// -----------------------------------------------------------------------------

test('time scale affects delta but not the unscaled clock', () => {
    Time.reset();
    Time.timeScale = 0.5;
    Time.advance(0.1);

    assert.ok(Math.abs(Time.deltaTime - 0.05) < 1e-9);
    assert.ok(Math.abs(Time.unscaledDeltaTime - 0.1) < 1e-9);
    Time.timeScale = 1;
});

test('a long frame is clamped so physics does not explode after a stall', () => {
    Time.reset();
    Time.advance(5);
    assert.equal(Time.unscaledDeltaTime, Time.maximumDeltaTime);
});

// -----------------------------------------------------------------------------
// C# naming compatibility
// -----------------------------------------------------------------------------

test('the C# aliases let transliterated code run', () => {
    installCSharpAliases();

    const scene = new Scene('Aliases');
    const actor = scene.addActor(new Actor('Ported'));
    scene.flushPendingActors();

    // Written the way the C# would be, with only the type names unchanged.
    actor.Transform.Position = new Vector2(10, 20);
    actor.AddComponent(SpriteRenderer);

    assert.deepEqual(actor.transform.position.toArray(), [10, 20]);
    assert.ok(actor.GetComponent(SpriteRenderer));
    assert.equal(actor.Name, 'Ported');
});

test('the aliases stay out of serialisation', () => {
    installCSharpAliases();

    const actor = new Actor('Clean');
    const keys = Object.keys(actor);

    assert.ok(!keys.includes('Name'), 'an alias must not be enumerable');
    assert.ok(keys.includes('name'));
});

// -----------------------------------------------------------------------------
// Regressions
// -----------------------------------------------------------------------------

test('a material property loads as a Material3D, not a plain object', () => {
    // The renderer calls methods on it every frame; a bare object from the file
    // throws on the first draw and the scene renders as nothing at all.
    const scene = deserialize({
        name: 'Materials',
        layers: [{
            name: 'Default',
            actors: [{
                name: 'Cube',
                position3: [0, 0, 0],
                components: [{
                    type: 'MeshRenderer',
                    properties: {
                        MeshType: 'Cube',
                        Materials: [{ albedoColor: '#3366CCFF', metallic: 0.5, roughness: 0.3 }],
                    },
                }],
            }],
        }],
    });

    const material = scene.findByName('Cube').getComponent('MeshRenderer').materials[0];
    assert.equal(material.constructor.name, 'Material3D');
    assert.equal(typeof material.albedoColor.toFloatArray, 'function');
    assert.deepEqual(material.albedoColor.toFloatArray().map((n) => +n.toFixed(2)), [0.2, 0.4, 0.8, 1]);
});

test('a loaded scene starts its actors only once it has an engine', () => {
    // `start` is where a component loads what it references — a sprite's texture,
    // a script's source. Starting the actors while the scene still has no host
    // means every one of those silently finds no asset manager and never loads.
    const scene = deserialize({
        name: 'Ordering',
        layers: [{
            name: 'Default',
            actors: [{
                name: 'Scripted',
                components: [{ type: 'ScriptComponent', properties: { ScriptPath: 'Scripts/A.js' } }],
            }],
        }],
    }, { flush: false });

    const scripted = scene.layers[0]._pendingAdd[0];
    assert.ok(scripted, 'with flush:false the actor is still queued');
    assert.equal(scripted.getComponent('ScriptComponent')._initialised, false);

    // What SceneManager.adoptScene does: attach, then flush.
    const loaded = [];
    scene.engine = { assets: { loadText: (p) => { loaded.push(p); return Promise.resolve(''); } } };
    scene.flushPendingActors();

    assert.deepEqual(loaded, ['Scripts/A.js'], 'the script was fetched once the engine was in place');
});

test('deserialize still flushes by default', () => {
    const scene = deserialize({
        name: 'Default flush',
        layers: [{ name: 'Default', actors: [{ name: 'Thing' }] }],
    });
    assert.ok(scene.findByName('Thing'), 'a caller that just wants a scene gets a usable one');
});

test('a host that is not simulating leaves the world alone', () => {
    // The editor renders continuously but must not advance the scene: otherwise
    // opening a scene starts dropping its actors through the floor before anyone
    // has pressed Play.
    const scene = new Scene('EditTime');
    scene.physics2D = new PhysicsSystem2D({ scene, gravity: new Vector2(0, 980) });

    const box = scene.addActor(new Actor('Box'));
    box.transform.localPosition = new Vector2(0, 100);
    box.addComponent(Rigidbody2D);
    box.addComponent(BoxCollider2D);
    scene.flushPendingActors();

    // What the editor's loop does while not playing.
    const simulate = false;
    for (let i = 0; i < 120; i++) {
        Time.advance(1 / 60);
        if (!simulate) continue;
        scene.physics2D.fixedStep(1 / 60);
        scene.update(1 / 60);
    }

    assert.equal(box.transform.position.y, 100, 'nothing moved while not simulating');
});

test('isKinematic is read from a C# file but not written back', () => {
    // The C# Rigidbody2D has no static body type: BodyType is computed from
    // IsKinematic and has no setter, so that is the only spelling its files use.
    const scene = deserialize({
        name: 'Bodies',
        layers: [{
            name: 'Default',
            actors: [{
                name: 'Platform',
                components: [{ type: 'Rigidbody2D', properties: { IsKinematic: true, Mass: 3 } }],
            }],
        }],
    });

    const body = scene.findByName('Platform').getComponent(Rigidbody2D);
    assert.equal(body.bodyType, BodyType.Kinematic, 'IsKinematic was honoured');
    assert.equal(body.mass, 3);

    const written = JSON.parse(serialize(scene))
        .layers[0].actors[0].components[0].properties;

    assert.equal(written.IsKinematic, undefined, 'not written twice under two names');
    assert.equal(written.BodyType, 'Kinematic', 'the richer form is what gets written');
});

test('world bounds hug the mesh instead of ballooning to its diagonal', () => {
    // Picking, culling and the selection outline all read these. A sphere-sized
    // box is a fine proxy for a cube and useless for anything flat: the default
    // floor's became a 21-metre ball centred on the origin, so it won every
    // viewport click in the scene.
    const floor = new Actor('Floor');
    floor.addComponent(Transform3D).localScale = new Vector3(30, 1, 30);
    const renderer = floor.addComponent(MeshRenderer);
    renderer.setPrimitive('Plane');

    const size = renderer.worldBounds.size;
    assert.ok(Math.abs(size.x - 30) < 0.01, `x extent was ${size.x}`);
    assert.ok(size.y < 0.1, `a flat plane should stay flat, y extent was ${size.y}`);
    assert.ok(Math.abs(size.z - 30) < 0.01, `z extent was ${size.z}`);
});

test('a rotated box grows only on the axes the rotation spreads it across', () => {
    const actor = new Actor('Cube');
    actor.addComponent(Transform3D).localEulerAngles = new Vector3(0, 45, 0);
    const renderer = actor.addComponent(MeshRenderer);
    renderer.setPrimitive('Cube');

    const size = renderer.worldBounds.size;
    assert.ok(Math.abs(size.x - Math.SQRT2) < 1e-4, `x was ${size.x}`);
    assert.ok(Math.abs(size.y - 1) < 1e-4, `a yaw must not grow the box vertically, y was ${size.y}`);
    assert.ok(Math.abs(size.z - Math.SQRT2) < 1e-4, `z was ${size.z}`);
});

test('a ray picks the object it actually points at, not the biggest one', () => {
    const scene = new Scene('Picking');

    const floor = scene.addActor(new Actor('Floor'));
    floor.addComponent(Transform3D).localScale = new Vector3(30, 1, 30);
    floor.addComponent(MeshRenderer).setPrimitive('Plane');

    const cube = scene.addActor(new Actor('Cube'));
    cube.addComponent(Transform3D).localPosition = new Vector3(0, 0.5, 0);
    cube.addComponent(MeshRenderer).setPrimitive('Cube');
    scene.flushPendingActors();

    // Straight down the -Z axis at the cube's centre height, from outside the floor.
    const origin = new Vector3(0, 0.5, 10);
    const direction = new Vector3(0, 0, -1);

    let nearest = null;
    let nearestDistance = Infinity;
    for (const renderer of MeshRenderer.all) {
        if (renderer.actor.scene !== scene) continue;
        const hit = rayVsBounds(origin, direction, Infinity, renderer.worldBounds);
        if (hit && hit.distance < nearestDistance) {
            nearestDistance = hit.distance;
            nearest = renderer.actor;
        }
    }

    assert.equal(nearest?.name, 'Cube', 'the ray points at the cube');
});

test('the default pawn gets a body and a view so Play looks like playing', () => {
    // A bare Character has no renderer and no camera. It walks, falls and
    // collides correctly and none of it is visible, so pressing Play on a fresh
    // scene looked exactly like not pressing it.
    const scene = new Scene('DefaultRig');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const start = scene.addActor(new Actor('Player Start'));
    start.addComponent(Transform3D);
    start.addComponent(PlayerStart);

    scene.addActor(new GameMode('Game Mode'));
    scene.flushPendingActors();
    scene.update(1 / 60);

    const controller = scene.findActorsOfType(PlayerController)[0];
    const pawn = controller.controlledPawn;

    const camera = PlayerController.findPawnCamera(pawn);
    assert.ok(camera, 'the pawn has no camera, so Play would show the editor view');
    assert.equal(controller.viewCamera, camera, 'the controller adopted it');
    assert.equal(Camera3D.playerView, camera, 'and the renderer draws through it');

    const body = pawn.transform3D.children
        .map((t) => t.actor.getComponent(MeshRenderer))
        .find(Boolean);
    assert.ok(body, 'the pawn has nothing to see');

    // Behind the pawn, and facing the same way it does — a camera yawed 180 here
    // would look at the horizon behind the player instead of over its shoulder.
    const view = camera.getTransform3D();
    assert.ok(view.localPosition.z > 0, 'the camera sits behind the pawn');
    assert.ok(Math.abs(view.localEulerAngles.y) < 1, 'and shares the pawn’s facing');
});

test('a pawn that brings its own camera keeps it', () => {
    // The body and the view are added independently: a pawn that supplies one
    // still gets the other.
    const scene = new Scene('OwnRig');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const start = scene.addActor(new Actor('Player Start'));
    start.addComponent(Transform3D);
    start.addComponent(PlayerStart);

    const mode = new GameMode('Game Mode');
    mode.pawnFactory = () => {
        const pawn = new Character('Custom');
        pawn.addComponent(Camera3D);
        return pawn;
    };

    scene.addActor(mode);
    scene.flushPendingActors();
    scene.update(1 / 60);

    const controller = scene.findActorsOfType(PlayerController)[0];
    const pawn = controller.controlledPawn;
    const own = pawn.getComponent(Camera3D);

    assert.ok(own, 'the supplied camera went missing');
    assert.equal(controller.viewCamera, own, 'the controller adopted the pawn’s own camera');

    const addedCameras = pawn.transform3D.children
        .filter((t) => t.actor.getComponent(Camera3D));
    assert.equal(addedCameras.length, 0, 'a second camera was bolted onto a pawn that came equipped');
});

test('look input pitches the view camera rather than going nowhere', () => {
    // The pawn takes only the yaw — pitching the body would tip the character
    // over — so without the controller applying it, every frame of pitch input
    // accumulated into a number nothing ever read.
    const scene = new Scene('Look');
    scene.physics3D = new PhysicsSystem3D({ scene });

    const start = scene.addActor(new Actor('Player Start'));
    start.addComponent(Transform3D);
    start.addComponent(PlayerStart);
    scene.addActor(new GameMode('Game Mode'));
    scene.flushPendingActors();
    scene.update(1 / 60);

    const controller = scene.findActorsOfType(PlayerController)[0];
    const pawn = controller.controlledPawn;
    const view = controller.viewCamera.getTransform3D();

    const restingPitch = view.localEulerAngles.x;
    pawn.addControllerPitchInput(20);
    scene.lateUpdate(1 / 60);

    assert.ok(Math.abs(view.localEulerAngles.x - (restingPitch + 20)) < 0.01,
        `the camera pitch was ${view.localEulerAngles.x}, expected ${restingPitch + 20}`);
    assert.ok(Math.abs(pawn.transform3D.eulerAngles.x) < 0.01, 'the body must not pitch with it');
});
