// -----------------------------------------------------------------------------
// Layer — an ordered set of Actors inside a Scene, and the unit of draw order.
//
// Adds and removes are queued rather than applied immediately, so spawning or
// destroying during a frame cannot mutate the list the update loop is walking.
// The queues drain at the start of `update` — or on demand through
// `flushPending`, which is what a tool or the editor needs when it builds a
// scene and then reads it back without simulating.
// -----------------------------------------------------------------------------

import { Actor } from './Actor.js';

/** A named, ordered collection of Actors. Lower `order` draws first, behind. */
export class Layer {
    /**
     * @param {string} name
     * @param {number} [order=0] Draw order. Lower draws first.
     */
    constructor(name, order = 0) {
        this.name = name;
        this.order = order;

        /** Gates drawing only. */
        this.visible = true;

        /** Gates update, fixedUpdate and lateUpdate. */
        this.active = true;

        /** @type {?import('./Scene.js').Scene} */
        this.scene = null;

        this._actors = [];
        this._pendingAdd = [];
        this._pendingRemove = [];
    }

    /** The live actors. Queued ones are not here until the next flush. */
    get actors() { return this._actors; }

    // -------------------------------------------------------------------------
    // Actor management
    // -------------------------------------------------------------------------

    /**
     * Queues an actor to join this layer. It becomes visible to `actors`,
     * `findByName` and the serialiser at the next flush.
     *
     * @param {Actor|Function} actorOrClass An actor, or an Actor subclass to construct.
     */
    addActor(actorOrClass) {
        const actor = typeof actorOrClass === 'function' ? new actorOrClass() : actorOrClass;
        actor.scene = this.scene;
        actor.layerRef = this;
        this._pendingAdd.push(actor);
        return actor;
    }

    /** Queues an actor for removal. This destroys it; use `detachActor` to move one. */
    removeActor(actor) { this._pendingRemove.push(actor); }

    /**
     * Takes an actor out of this layer immediately and intact, so it can join
     * another one. Nothing is destroyed: components stay awake and registered.
     *
     * Call it between frames, not from a component callback — it edits the actor
     * list the update loop may be walking.
     *
     * @returns {boolean} True when the actor was present, live or still queued.
     */
    detachActor(actor) {
        const liveIndex = this._actors.indexOf(actor);
        if (liveIndex >= 0) this._actors.splice(liveIndex, 1);

        const queuedIndex = this._pendingAdd.indexOf(actor);
        if (queuedIndex >= 0) this._pendingAdd.splice(queuedIndex, 1);

        const removeIndex = this._pendingRemove.indexOf(actor);
        if (removeIndex >= 0) this._pendingRemove.splice(removeIndex, 1);

        const present = liveIndex >= 0 || queuedIndex >= 0;
        if (present && actor.layerRef === this) actor.layerRef = null;
        return present;
    }

    /** The first live actor with this name, or null. */
    findByName(name) { return this._actors.find((a) => a.name === name) ?? null; }

    /** Every live actor with this tag. */
    findByTag(tag) { return this._actors.filter((a) => a.tag === tag); }

    /** Every live actor that is an instance of `type`. */
    findActorsOfType(type) { return this._actors.filter((a) => a instanceof type); }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /**
     * Applies queued adds and removes.
     *
     * Both queues are drained into a local buffer before being walked, because
     * `start` and `onDestroy` routinely spawn or destroy actors — a game mode
     * spawning its controller and pawn is the obvious case. Anything queued
     * during the flush lands on the next one.
     */
    flushPending() {
        if (this._pendingAdd.length > 0) {
            const batch = this._pendingAdd.slice();
            this._pendingAdd.length = 0;

            for (const actor of batch) {
                this._actors.push(actor);
                actor.internalStart();
            }
        }

        if (this._pendingRemove.length > 0) {
            const batch = this._pendingRemove.slice();
            this._pendingRemove.length = 0;

            for (const actor of batch) {
                actor.internalDestroy();
                const i = this._actors.indexOf(actor);
                if (i >= 0) this._actors.splice(i, 1);
            }
        }
    }

    update(dt) {
        if (!this.active) return;
        this.flushPending();
        for (const actor of this._actors.slice()) actor.internalUpdate(dt);
    }

    fixedUpdate(dt) {
        if (!this.active) return;
        for (const actor of this._actors.slice()) actor.internalFixedUpdate(dt);
    }

    lateUpdate(dt) {
        if (!this.active) return;
        for (const actor of this._actors.slice()) actor.internalLateUpdate(dt);
    }

    /**
     * Draws every visible actor, back to front.
     *
     * The sort key is the actor's *local* Y, matching the C# engine's painter's
     * algorithm. Parented actors therefore sort by their offset from the parent,
     * which is worth knowing before attaching sprites to a moving rig.
     */
    draw(batch) {
        if (!this.visible) return;

        const sorted = this._actors
            .filter((a) => a.isActive)
            .sort((a, b) => a.transform.localPosition.y - b.transform.localPosition.y);

        for (const actor of sorted) actor.internalDraw(batch);
    }

    /** Destroys every actor in the layer, queued ones included, and clears its queues. */
    destroy() {
        for (const actor of this._actors.slice()) actor.internalDestroy();

        // Actors still queued have already run `awake`, which is where components
        // register themselves in the renderer's and physics' lists. Dropping them
        // without `onDestroy` leaves phantom meshes drawing for the session.
        for (const actor of this._pendingAdd.slice()) actor.internalDestroy();

        this._actors.length = 0;
        this._pendingAdd.length = 0;
        this._pendingRemove.length = 0;
    }
}
