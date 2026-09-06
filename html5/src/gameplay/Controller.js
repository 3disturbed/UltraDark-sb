// -----------------------------------------------------------------------------
// Controller — the thing that drives a Pawn. Base for player and AI controllers.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { registerActor } from '../core/TypeRegistry.js';
import { SBEvent } from '../core/SBEvent.js';

/** Drives a Pawn. */
export class Controller extends Actor {
    constructor(name = 'Controller') {
        super(name);

        /** @type {?import('./Pawn.js').Pawn} */
        this.controlledPawn = null;

        this.possessed = new SBEvent();
        this.unPossessed = new SBEvent();
    }

    /** Overridden by PlayerController. */
    get isPlayerController() { return false; }

    /** Takes control of a pawn, releasing whatever it held before. */
    possess(pawn) {
        if (!pawn || pawn === this.controlledPawn) return;

        this.unPossess();

        // A pawn can only have one controller, so break the other link first.
        pawn.controller?.unPossess();

        this.controlledPawn = pawn;
        pawn.controller = this;

        this.onPossess(pawn);
        pawn.onPossessed(this);
        this.possessed.broadcast(pawn);
    }

    /** Releases the current pawn. */
    unPossess() {
        const pawn = this.controlledPawn;
        if (!pawn) return;

        this.controlledPawn = null;
        pawn.controller = null;

        this.onUnPossess(pawn);
        pawn.onUnPossessed(this);
        this.unPossessed.broadcast(pawn);
    }

    onDestroy() { this.unPossess(); }

    /** Hook for subclasses; runs before the pawn is told. */
    onPossess(pawn) {}

    /** Hook for subclasses; runs before the pawn is told. */
    onUnPossess(pawn) {}
}
registerActor(Controller, { category: 'Gameplay', summary: 'Drives a pawn.' });
