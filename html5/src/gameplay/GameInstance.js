// -----------------------------------------------------------------------------
// GameInstance — state that outlives any one scene.
// -----------------------------------------------------------------------------

import { SBEvent } from '../core/SBEvent.js';

/** Base for something that lives alongside the game instance. */
export class Subsystem {
    constructor() {
        this.isInitialized = false;
    }

    /** Return false to skip creating this subsystem. */
    shouldCreate() { return true; }

    initialize() {}
    deinitialize() {}
    tick(dt) {}
}

/** State that survives scene changes: save data, settings, the session. */
export class GameInstance {
    /** The instance the engine host created. */
    static current = null;

    constructor() {
        /** @type {Map<Function, Subsystem>} */
        this.subsystems = new Map();

        this.started = new SBEvent();
        this.shuttingDown = new SBEvent();
        this.worldChanged = new SBEvent();
    }

    /** Adds a subsystem, or returns the existing one. */
    getSubsystem(Ctor) {
        const existing = this.subsystems.get(Ctor);
        if (existing) return existing;

        const subsystem = new Ctor();
        if (!subsystem.shouldCreate()) return null;

        subsystem.initialize();
        subsystem.isInitialized = true;
        this.subsystems.set(Ctor, subsystem);
        return subsystem;
    }

    internalInit() { GameInstance.current = this; }

    internalStart() {
        this.onStart();
        this.started.broadcast();
    }

    internalTick(dt) {
        this.tick(dt);
        for (const subsystem of this.subsystems.values()) {
            if (subsystem.isInitialized) subsystem.tick(dt);
        }
    }

    internalShutdown() {
        this.shuttingDown.broadcast();
        this.onShutdown();
        for (const subsystem of this.subsystems.values()) subsystem.deinitialize();
        this.subsystems.clear();
        if (GameInstance.current === this) GameInstance.current = null;
    }

    // ---- Hooks ---------------------------------------------------------------

    onStart() {}
    onShutdown() {}
    tick(dt) {}
}
