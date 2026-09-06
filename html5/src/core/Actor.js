// -----------------------------------------------------------------------------
// Actor — everything in the world is one. Actors hold Components and drive them.
//
// The lifecycle mirrors the C# Actor exactly, including two details that are
// easy to get wrong and that scenes depend on:
//
//   * the actor's own per-frame hook is `onStart`, while a component's is
//     `start` — the names differ in C# too, deliberately;
//   * a component that is disabled when its actor starts never receives `start`,
//     and does not get it retroactively when re-enabled.
// -----------------------------------------------------------------------------

import { Transform } from './Transform.js';
import { Transform3D } from './Transform3D.js';
import { isolate } from './ExceptionIsolation.js';
import { resolveComponent, componentNameOf } from './TypeRegistry.js';
import { CoroutineRunner } from './CoroutineRunner.js';

let _nextId = 1;

/** Base class for every object in the world. */
export class Actor {
    /**
     * Saveable state declared by an Actor *subclass*. The base actor's own
     * name/tag/layer/active live in the actor block of a scene file, not here.
     */
    static schema = {};

    /**
     * @param {string} [name] Display name, shown in the hierarchy.
     */
    constructor(name = 'Actor') {
        // ---- Identity ----
        this.name = name;
        this.tag = 'Untagged';

        /** Numeric layer index, used for physics and raycast masks. */
        this.layer = 0;

        /** When false the actor and its components are skipped every frame. */
        this.isActive = true;

        /** Process-unique id. Stable for the actor's lifetime; not saved. */
        this.id = _nextId++;

        /**
         * Seconds until the actor destroys itself. Zero or less means it lives
         * until something calls `destroy()`. Counts down on scaled time.
         */
        this.lifeSpan = 0;

        // ---- Scene graph ----
        /** @type {?import('./Scene.js').Scene} */
        this.scene = null;
        /** @type {?import('./Layer.js').Layer} The Layer object, distinct from `layer`. */
        this.layerRef = null;

        // ---- Internals ----
        this._components = [];
        this._started = false;
        this._destroyed = false;

        /** The 2D transform. Always present, always the first component. */
        this.transform = new Transform();
        this._attachComponent(this.transform);
    }

    /** True once the actor has been destroyed and is no longer usable. */
    get isDestroyed() { return this._destroyed; }

    /** The 3D transform, if this actor has one. Null for a purely 2D actor. */
    get transform3D() { return this.getComponent(Transform3D); }

    // -------------------------------------------------------------------------
    // Component management
    // -------------------------------------------------------------------------

    /**
     * Adds a component, first adding anything its `static requires` list names
     * that is not already present.
     *
     * @param {Function|string} type A component class, or a registered name.
     * @returns {import('./Component.js').Component}
     */
    addComponent(type) {
        const ctor = this._resolveComponentCtor(type);
        if (!ctor) throw new Error(`Unknown component type: ${type}`);

        for (const required of ctor.requires ?? []) {
            if (!this.hasComponent(required)) this.addComponent(required);
        }

        return this._attachComponent(new ctor());
    }

    /**
     * Adds a component without walking its `requires` list.
     *
     * The scene loader takes this path: it restores components in file order and
     * must not invent extra ones. A tool adding a single component by name wants
     * the opposite, which is what `addComponent` is for.
     */
    addComponentByType(type) {
        const ctor = this._resolveComponentCtor(type);
        if (!ctor) throw new Error(`Unknown component type: ${type}`);
        return this._attachComponent(new ctor());
    }

    /**
     * The first component of a type, or null.
     * @param {Function|string} type A component class, or a registered name.
     */
    getComponent(type) {
        const ctor = this._resolveComponentCtor(type);
        if (!ctor) return null;
        for (const c of this._components) if (c instanceof ctor) return c;
        return null;
    }

    /** Every component of a type, in attachment order. */
    getComponents(type) {
        const ctor = this._resolveComponentCtor(type);
        if (!ctor) return [];
        return this._components.filter((c) => c instanceof ctor);
    }

    /** True when the actor carries at least one component of the type. */
    hasComponent(type) { return this.getComponent(type) !== null; }

    /**
     * Removes a component — either an instance, or the first of a type.
     * The actor's own Transform is refused: every actor has exactly one.
     * @returns {boolean} False when there was nothing to remove.
     */
    removeComponent(typeOrInstance) {
        const target = typeof typeOrInstance === 'object' && typeOrInstance !== null
            ? typeOrInstance
            : this.getComponent(typeOrInstance);

        if (!target || target === this.transform) return false;

        const index = this._components.indexOf(target);
        if (index < 0) return false;

        isolate(() => target.onDestroy(), target, 'onDestroy');
        this._components.splice(index, 1);
        return true;
    }

    /** Every component on this actor, transform included. */
    getAllComponents() { return this._components; }

    _attachComponent(component) {
        component.actor = this;
        this._components.push(component);

        isolate(() => component.awake(), component, 'awake');

        // A component added to an actor that has already started gets its start
        // immediately, so a component attached at runtime is not left uninitialised.
        if (this._started && component.enabled) {
            isolate(() => component.start(), component, 'start');
        }
        return component;
    }

    _resolveComponentCtor(type) {
        if (typeof type === 'function') return type;
        if (typeof type !== 'string') return null;
        return resolveComponent(type);
    }

    // -------------------------------------------------------------------------
    // Lifecycle — called by the Layer
    // -------------------------------------------------------------------------

    internalStart() {
        if (this._started || this._destroyed) return;
        this._started = true;

        isolate(() => this.onStart(), this, 'onStart');

        for (const c of this._components.slice()) {
            if (!c.enabled) continue;
            isolate(() => c.start(), c, 'start');
        }
    }

    internalUpdate(dt) {
        if (!this.isActive || this._destroyed) return;

        if (this.lifeSpan > 0) {
            this.lifeSpan -= dt;
            if (this.lifeSpan <= 0) { this.destroy(); return; }
        }

        isolate(() => this.update(dt), this, 'update');

        // Snapshot: a component may add or remove components mid-iteration.
        for (const c of this._components.slice()) {
            if (!c.enabled) continue;
            isolate(() => c.update(dt), c, 'update');
        }
    }

    internalFixedUpdate(dt) {
        if (!this.isActive || this._destroyed) return;

        isolate(() => this.fixedUpdate(dt), this, 'fixedUpdate');
        for (const c of this._components.slice()) {
            if (!c.enabled) continue;
            isolate(() => c.fixedUpdate(dt), c, 'fixedUpdate');
        }
    }

    internalLateUpdate(dt) {
        if (!this.isActive || this._destroyed) return;

        isolate(() => this.lateUpdate(dt), this, 'lateUpdate');
        for (const c of this._components.slice()) {
            if (!c.enabled) continue;
            isolate(() => c.lateUpdate(dt), c, 'lateUpdate');
        }
    }

    internalDraw(batch) {
        if (!this.isActive || this._destroyed) return;

        isolate(() => this.draw(batch), this, 'draw');
        for (const c of this._components.slice()) {
            if (!c.enabled) continue;
            isolate(() => c.draw(batch), c, 'draw');
        }
    }

    internalDestroy() {
        if (this._destroyed) return;
        this._destroyed = true;

        // Cancel anything this actor started, so a coroutine cannot outlive its target.
        CoroutineRunner.instance.stopAllFor(this);

        isolate(() => this.onDestroy(), this, 'onDestroy');

        // Disabled components are destroyed too: they still registered themselves
        // in the renderer's and physics' static lists when they woke.
        for (const c of this._components.slice()) {
            isolate(() => c.onDestroy(), c, 'onDestroy');
        }
        this._components.length = 0;
    }

    // -------------------------------------------------------------------------
    // Overridable hooks
    // -------------------------------------------------------------------------

    /** Runs once when the actor joins a running scene. */
    onStart() {}

    /** Runs every frame. @param {number} dt Scaled seconds since the last frame. */
    update(dt) {}

    /** Runs at the fixed timestep. */
    fixedUpdate(dt) {}

    /** Runs every frame, after every `update` in the scene. */
    lateUpdate(dt) {}

    /** Draws through the 2D renderer. */
    draw(batch) {}

    /** Runs when the actor is destroyed. */
    onDestroy() {}

    // -------------------------------------------------------------------------
    // Collision and trigger — forwarded from the physics system
    // -------------------------------------------------------------------------

    onCollisionEnter(data) { this._forward('onCollisionEnter', data); }
    onCollisionStay(data)  { this._forward('onCollisionStay', data); }
    onCollisionExit(data)  { this._forward('onCollisionExit', data); }
    onTriggerEnter(other)  { this._forward('onTriggerEnter', other); }
    onTriggerStay(other)   { this._forward('onTriggerStay', other); }
    onTriggerExit(other)   { this._forward('onTriggerExit', other); }

    _forward(hook, payload) {
        for (const c of this._components.slice()) {
            if (!c.enabled) continue;
            isolate(() => c[hook](payload), c, hook);
        }
    }

    // -------------------------------------------------------------------------
    // Coroutines
    // -------------------------------------------------------------------------

    /**
     * Starts a coroutine owned by this actor, cancelled automatically when the
     * actor is destroyed. Pass a generator, the JavaScript equivalent of the
     * C# `IEnumerator`.
     *
     * @example
     * this.startCoroutine(function* () {
     *     while (true) {
     *         this.isActive = !this.isActive;
     *         yield new WaitForSeconds(0.2);
     *     }
     * }.call(this));
     */
    startCoroutine(routine) { return CoroutineRunner.instance.start(routine, this); }

    stopCoroutine(coroutine) { CoroutineRunner.instance.stop(coroutine); }

    stopAllCoroutines() { CoroutineRunner.instance.stopAllFor(this); }

    // -------------------------------------------------------------------------
    // Destruction
    // -------------------------------------------------------------------------

    /**
     * Queues the actor for destruction at the end of the current frame, so
     * iteration already in progress over the scene stays valid.
     *
     * @param {number} [delaySeconds] When positive, sets `lifeSpan` instead.
     */
    destroy(delaySeconds = 0) {
        if (delaySeconds > 0) { this.lifeSpan = delaySeconds; return; }
        if (this._destroyed) return;
        this.scene?.markForDestroy(this);
    }

    toString() { return `Actor[${this.id}:${this.name}]`; }
}

export { componentNameOf };
