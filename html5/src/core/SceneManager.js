// -----------------------------------------------------------------------------
// SceneManager — loads scenes and drives the active one.
//
// An instance, not a static, matching the C# class: the editor needs an
// edit-time scene and a play-time copy alive at once, which a global would not
// allow.
// -----------------------------------------------------------------------------

import { Scene } from './Scene.js';
import { SBEvent } from './SBEvent.js';

/** Owns the active scene and any additive ones. */
export class SceneManager {
    /**
     * @param {object} [services]
     * @param {import('../assets/AssetManager.js').AssetManager} [services.assets]
     * @param {import('../EngineHost.js').EngineHost} [services.engine]
     */
    constructor({ assets = null, engine = null } = {}) {
        this.assets = assets;
        this.engine = engine;

        /** @type {?Scene} */
        this._activeScene = null;
        /** @type {Scene[]} */
        this._additiveScenes = [];

        this._pendingLoad = null;

        this.sceneLoaded = new SBEvent();
        this.sceneUnloaded = new SBEvent();

        /** Actors carried across a scene change. */
        this._persistent = new Set();
    }

    get activeScene() { return this._activeScene; }
    get additiveScenes() { return this._additiveScenes; }

    /**
     * Queues a scene load. The swap happens at the start of the next update, so
     * a script that calls this mid-frame does not have the world change under it.
     */
    loadScene(scenePath, { additive = false } = {}) {
        this._pendingLoad = { scenePath, additive };
    }

    /** Loads a scene immediately, awaiting the file. */
    async loadSceneAsync(scenePath, { additive = false, onProgress = null } = {}) {
        onProgress?.(0);
        const scene = await this._readScene(scenePath);
        onProgress?.(1);

        if (additive) this._addAdditive(scene);
        else this.adoptScene(scene);

        return scene;
    }

    /**
     * Installs an already-built scene as the active one, destroying the old.
     * This is the path the editor uses on every open, play and stop.
     */
    adoptScene(scene) {
        const previous = this._activeScene;

        if (previous) {
            // Persistent actors move across before the old scene is destroyed,
            // or they would be destroyed along with it.
            for (const actor of this._persistent) {
                if (actor.isDestroyed || actor.scene !== previous) continue;
                actor.layerRef?.detachActor(actor);
                scene.addActor(actor, actor.layerRef?.name ?? 'default');
            }

            this.sceneUnloaded.broadcast(previous);
            previous.destroy();
        }

        this._activeScene = scene;
        this._attach(scene);
        scene.flushPendingActors();
        this.sceneLoaded.broadcast(scene);
        return scene;
    }

    /** A new, empty scene with the four standard layers. */
    createScene(name = 'Scene') {
        const scene = new Scene(name);
        this._attach(scene);
        return scene;
    }

    /** Unloads one additive scene by name. */
    unloadScene(sceneName) {
        const index = this._additiveScenes.findIndex((s) => s.name === sceneName);
        if (index < 0) return false;

        const [scene] = this._additiveScenes.splice(index, 1);
        this.sceneUnloaded.broadcast(scene);
        scene.destroy();
        return true;
    }

    /** Keeps an actor alive across scene loads. */
    dontDestroyOnLoad(actor) { this._persistent.add(actor); }

    /**
     * Resolves a scene path the way the C# loader does: the path itself, then
     * with `.scene`, then with `.json`, each also tried under `Assets/`.
     */
    static candidatePaths(path) {
        const bases = [path, `${path}.scene`, `${path}.json`];
        return bases.flatMap((candidate) =>
            candidate.startsWith('Assets/') ? [candidate] : [candidate, `Assets/${candidate}`]);
    }

    async _readScene(scenePath) {
        const { deserialize } = await import('../scene/SceneSerializer.js');

        if (!this.assets) return new Scene(baseName(scenePath));

        let lastError = null;
        for (const candidate of SceneManager.candidatePaths(scenePath)) {
            try {
                const text = await this.assets.loadText(candidate);
                return deserialize(text);
            } catch (err) {
                lastError = err;
            }
        }

        console.warn(
            `[SceneManager] Could not load scene '${scenePath}': ${lastError?.message ?? 'not found'}. `
            + 'Starting an empty scene instead.');
        return new Scene(baseName(scenePath));
    }

    _addAdditive(scene) {
        this._attach(scene);
        scene.flushPendingActors();
        this._additiveScenes.push(scene);
        this.sceneLoaded.broadcast(scene);
    }

    /** Wires a scene to the engine's services. */
    _attach(scene) {
        scene.engine = this.engine;
        if (this.engine) {
            scene.physics2D = this.engine.createPhysics2D(scene);
            scene.physics3D = this.engine.createPhysics3D(scene);
        }
    }

    async _processPendingLoad() {
        if (!this._pendingLoad) return;
        const { scenePath, additive } = this._pendingLoad;
        this._pendingLoad = null;

        const scene = await this._readScene(scenePath);
        if (additive) this._addAdditive(scene);
        else this.adoptScene(scene);
    }

    // ---- Frame ---------------------------------------------------------------

    update(dt) {
        // Fire and forget: the swap lands on whichever frame the file arrives.
        if (this._pendingLoad) this._processPendingLoad();

        this._activeScene?.update(dt);
        for (const scene of this._additiveScenes) scene.update(dt);
    }

    fixedUpdate(dt) {
        this._activeScene?.fixedUpdate(dt);
        for (const scene of this._additiveScenes) scene.fixedUpdate(dt);
    }

    lateUpdate(dt) {
        this._activeScene?.lateUpdate(dt);
        for (const scene of this._additiveScenes) scene.lateUpdate(dt);
    }

    draw(batch) {
        this._activeScene?.draw(batch);
        for (const scene of this._additiveScenes) scene.draw(batch);
    }

    /** Destroys every scene. */
    dispose() {
        this._activeScene?.destroy();
        for (const scene of this._additiveScenes) scene.destroy();
        this._activeScene = null;
        this._additiveScenes.length = 0;
    }
}

function baseName(path) {
    return path.split('/').pop().replace(/\.(scene|json)$/i, '') || 'Scene';
}
