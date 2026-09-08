// -----------------------------------------------------------------------------
// ChibiCharacter — one component that stands in for forty actors.
//
// A scene could store a built chibi as the subtree it is, but then every scene
// file carries forty nested actors per character and every read of it costs
// forty actors' worth of tokens. Storing the recipe path instead means a scene
// says "a villager stands here" in three lines, and the wardrobe is edited in
// one place rather than in every scene that used it.
//
// Mirrored in SexyBiscuit.Engine/Chibi/ChibiCharacter.cs.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { build } from './ChibiBuilder.js';
import { defaultRecipe, normalise, random } from './ChibiRecipe.js';

/** Builds a chibi under its actor, from a recipe file or from a seed. */
export class ChibiCharacter extends Component {
    static schema = {
        recipePath: { type: P.Asset, assetKind: 'chibi', default: '' },
        seed: { type: P.Int, default: 0 },
        buildOnStart: { type: P.Bool, default: true },
    };

    /** Every live character, so a panel can rebuild them all after a wardrobe edit. */
    static all = [];

    constructor() {
        super();

        /** A `.chibi` under Assets/. Empty means "use the seed", or the default. */
        this.recipePath = '';
        /** Non-zero picks a coordinated random character, reproducibly. */
        this.seed = 0;
        this.buildOnStart = true;

        /** @type {?import('./ChibiBuilder.js').ChibiBuild} */
        this.chibi = null;
        this._loading = false;
    }

    awake() {
        ChibiCharacter.all.push(this);
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    onDestroy() {
        const i = ChibiCharacter.all.indexOf(this);
        if (i >= 0) ChibiCharacter.all.splice(i, 1);
        this.chibi = null;
    }

    start() {
        if (this.buildOnStart && !this.chibi) this.rebuild();
    }

    /** The recipe this component's fields describe, before any file is read. */
    localRecipe() {
        return this.seed !== 0 ? random(this.seed) : defaultRecipe();
    }

    /**
     * Rebuilds the character, destroying whatever was there.
     *
     * With a `recipePath` the file is read first, so this returns before the new
     * body exists; without one it is synchronous. Callers that need the result
     * should use the returned promise rather than reading `chibi` straight after.
     */
    rebuild(recipe = null) {
        this._destroySubtree();

        if (recipe) {
            this._apply(recipe);
            return Promise.resolve(this.chibi);
        }

        if (!this.recipePath) {
            this._apply(this.localRecipe());
            return Promise.resolve(this.chibi);
        }

        const assets = this.actor?.scene?.engine?.assets;
        if (!assets) {
            this._apply(this.localRecipe());
            return Promise.resolve(this.chibi);
        }

        this._loading = true;
        return assets.loadJson(this.recipePath)
            .then((raw) => {
                // A seed alongside a file means "this character, varied": the file
                // supplies the wardrobe and the seed is kept for the game to use.
                this._apply(normalise(raw, (m) => this._warn(m)));
                return this.chibi;
            })
            .catch((error) => {
                this._warn(`could not read '${this.recipePath}': ${error.message}`);
                this._apply(this.localRecipe());
                return this.chibi;
            })
            .finally(() => { this._loading = false; });
    }

    _apply(recipe) {
        this.chibi = build(recipe, (m) => this._warn(m));
        // false: the parts were placed in the rig's own frame, not the world's.
        this.chibi.actor.attachTo(this.actor, false);
        this.actor.scene?.addActor(this.chibi.actor);
    }

    _destroySubtree() {
        if (!this.chibi) return;
        this.chibi.actor.destroy();
        this.chibi = null;
    }

    _warn(message) {
        // eslint-disable-next-line no-console
        console.warn(`[ChibiCharacter] ${this.actor?.name ?? '?'}: ${message}`);
    }
}
registerComponent(ChibiCharacter, {
    category: 'Chibi',
    summary: 'Builds a chibi character from a recipe or a seed.',
});
