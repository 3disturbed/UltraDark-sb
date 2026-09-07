// -----------------------------------------------------------------------------
// Scene — the root container for gameplay. Holds an ordered list of Layers.
// -----------------------------------------------------------------------------

import { Layer } from './Layer.js';

/** The four layers every new scene starts with, in draw order. */
const DEFAULT_LAYERS = [
    ['background', -100],
    ['default', 0],
    ['foreground', 100],
    ['ui', 200],
];

/** A world: layers of actors, updated and drawn together. */
export class Scene {
    /**
     * @param {string} [name='Scene']
     * @param {object} [options]
     * @param {boolean} [options.createDefaultLayers=true] Seed the four standard layers.
     */
    constructor(name = 'Scene', { createDefaultLayers = true } = {}) {
        this.name = name;
        this.isActive = true;

        // Set by the engine host when it adopts the scene. Kept as plain fields
        // rather than imports so `core` stays independent of the subsystems: a
        // headless test can build a Scene without pulling in physics or WebGL.
        /** @type {?import('../EngineHost.js').EngineHost} */
        this.engine = null;
        /** @type {?import('../physics/PhysicsSystem2D.js').PhysicsSystem2D} */
        this.physics2D = null;
        /** @type {?import('../physics/PhysicsSystem3D.js').PhysicsSystem3D} */
        this.physics3D = null;

        this._layers = [];
        this._markedForDestroy = new Set();

        if (createDefaultLayers) {
            for (const [layerName, order] of DEFAULT_LAYERS) this.addLayer(layerName, order);
        }
    }

    /** The layers, always sorted by `order`. */
    get layers() { return this._layers; }

    // -------------------------------------------------------------------------
    // Layers
    // -------------------------------------------------------------------------

    addLayer(name, order = 0) {
        const layer = new Layer(name, order);
        layer.scene = this;
        this._layers.push(layer);
        this._layers.sort((a, b) => a.order - b.order);
        return layer;
    }

    /** The layer with this exact name, or null. */
    getLayer(name) { return this._layers.find((l) => l.name === name) ?? null; }

    getOrCreateLayer(name, order = 0) { return this.getLayer(name) ?? this.addLayer(name, order); }

    /** Destroys a layer and everything in it. */
    removeLayer(name) {
        const layer = this.getLayer(name);
        if (!layer) return;
        layer.destroy();
        this._layers.splice(this._layers.indexOf(layer), 1);
    }

    // -------------------------------------------------------------------------
    // Actors
    // -------------------------------------------------------------------------

    /**
     * Adds an actor to a layer, creating the layer if needed.
     * @param {import('./Actor.js').Actor|Function} actorOrClass
     * @param {string} [layerName='default']
     */
    /**
     * Adds an actor, and everything attached to it, to a layer.
     *
     * A child is an actor in its own right: it needs a place in a layer to be started,
     * updated and drawn. Adding the subtree here rather than at each call site is what
     * stops the scene loader, prefabs and duplicate from each having to remember to walk
     * it — forgetting produced a turret that followed its tank perfectly and never drew.
     * A descendant that already belongs to a layer is left where it is, so adding a
     * parent twice cannot list a child twice.
     */
    addActor(actorOrClass, layerName = 'default') {
        const layer = this.getOrCreateLayer(layerName);
        const actor = layer.addActor(actorOrClass);

        for (const descendant of actor.descendants()) {
            if (!descendant.layerRef) layer.addActor(descendant);
        }
        return actor;
    }

    /** The first actor with this name, searching layers in draw order. */
    findByName(name) {
        for (const layer of this._layers) {
            const found = layer.findByName(name);
            if (found) return found;
        }
        return null;
    }

    /** Every actor with this tag, across every layer. */
    findByTag(tag) { return this._layers.flatMap((l) => l.findByTag(tag)); }

    /** Every actor that is an instance of `type`. */
    findActorsOfType(type) { return this._layers.flatMap((l) => l.findActorsOfType(type)); }

    /** Every live actor in the scene. */
    get allActors() { return this._layers.flatMap((l) => l.actors); }

    /** Finds an actor by its numeric id. */
    findById(id) {
        const wanted = Number(id);
        for (const layer of this._layers) {
            for (const actor of layer.actors) if (actor.id === wanted) return actor;
        }
        return null;
    }

    /** Queues an actor for destruction at the end of the frame. Idempotent. */
    markForDestroy(actor) { this._markedForDestroy.add(actor); }

    /**
     * Moves an actor to another layer without destroying it.
     *
     * The obvious sequence — remove then add — destroys the actor, because a
     * layer's removal queue is its destruction queue.
     */
    moveActor(actor, layerName, order = 0) {
        const target = this.getOrCreateLayer(layerName, order);
        if (actor.layerRef === target) return;

        actor.layerRef?.detachActor(actor);
        target.addActor(actor);

        // The subtree moves with it. Leaving a turret drawing in the layer its tank just
        // left is never what the drag meant, and the two would then sort against each other.
        for (const descendant of actor.descendants()) {
            if (descendant.layerRef === target) continue;
            descendant.layerRef?.detachActor(descendant);
            target.addActor(descendant);
        }
    }

    /**
     * Applies queued actor adds and removals immediately, without ticking anything.
     *
     * `addActor` queues rather than inserting, so spawning during a frame cannot
     * mutate the list being iterated. That queue normally drains at the start of
     * `update`, which is right for a running game and wrong for a tool that
     * builds a scene and then inspects it without simulating — the editor, the
     * serialiser, a test. Without this the scene draws correctly while appearing
     * completely empty to everything that walks `layer.actors`.
     */
    flushPendingActors() {
        for (const layer of this._layers) layer.flushPending();
        this._flushDestroyQueue();
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    update(dt) {
        if (!this.isActive) return;
        for (const layer of this._layers.slice()) layer.update(dt);
        this._flushDestroyQueue();
    }

    fixedUpdate(dt) {
        if (!this.isActive) return;
        for (const layer of this._layers.slice()) layer.fixedUpdate(dt);
    }

    lateUpdate(dt) {
        if (!this.isActive) return;
        for (const layer of this._layers.slice()) layer.lateUpdate(dt);
    }

    draw(batch) {
        if (!this.isActive) return;
        for (const layer of this._layers) layer.draw(batch);
    }

    /**
     * Destroys every actor in the scene and drops its layers.
     *
     * Not optional bookkeeping: components register themselves in static lists
     * (the mesh renderers, the lights, the player starts) and only leave them on
     * destroy, so a dropped scene stays visible to the renderer forever.
     */
    destroy() {
        for (const layer of this._layers) layer.destroy();
        this._layers.length = 0;
        this._markedForDestroy.clear();
    }

    /**
     * Applies every `markForDestroy` queued during this frame.
     *
     * `layer.removeActor` only queues, so each touched layer is flushed here too.
     * Without that the actor survived until the next frame's update — two frames
     * to destroy something, and one extra update on a destroyed actor.
     */
    _flushDestroyQueue() {
        if (this._markedForDestroy.size === 0) return;

        const touched = new Set();
        for (const actor of this._markedForDestroy) {
            const layer = actor.layerRef;
            if (!layer) continue;
            layer.removeActor(actor);
            touched.add(layer);
        }

        this._markedForDestroy.clear();
        for (const layer of touched) layer.flushPending();
    }
}
