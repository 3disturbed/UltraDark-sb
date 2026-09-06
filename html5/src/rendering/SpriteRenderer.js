// -----------------------------------------------------------------------------
// SpriteRenderer — draws a texture at its actor's transform.
//
// When no texture is set it draws a tinted rectangle instead of nothing. The
// bundled templates rely on that: every actor in them has a SpriteRenderer with
// only a `Tint`, and the scenes are meant to be visible before any art exists.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Vector2, Color } from '../math/index.js';
import { SpriteEffects } from './SpriteBatch.js';

/** Draws a 2D sprite. */
export class SpriteRenderer extends Component {
    static schema = {
        texturePath: { type: P.Asset, assetKind: 'texture', default: '' },
        tint:        { type: P.Color, default: '#FFFFFFFF' },
        effects:     { type: P.Enum, values: ['None', 'FlipHorizontally', 'FlipVertically', 'FlipBoth'], default: 'None' },
        layerDepth:  { type: P.Number, default: 0 },
        pivot:       { type: P.Vector2, default: [0.5, 0.5] },
        frameWidth:  { type: P.Int, default: 0 },
        frameHeight: { type: P.Int, default: 0 },
        frameIndex:  { type: P.Int, default: 0 },
        /** Size used when there is no texture, before the actor's scale. */
        size:        { type: P.Vector2, default: [32, 32] },
    };

    /** Every live sprite renderer, for picking and for batching decisions. */
    static all = [];

    constructor() {
        super();
        /** @type {?import('../assets/Texture2D.js').Texture2D} */
        this.texture = null;
        this.texturePath = '';
        this.tint = Color.white;
        this.effects = SpriteEffects.None;
        this.layerDepth = 0;
        this.pivot = new Vector2(0.5, 0.5);

        // Atlas slicing: a non-zero frame size treats the texture as a grid.
        this.frameWidth = 0;
        this.frameHeight = 0;
        this.frameIndex = 0;

        this.size = new Vector2(32, 32);

        /** Explicit sub-rectangle, overriding the atlas grid when set. */
        this.sourceRect = null;

        this._resolved = false;
    }

    awake() { SpriteRenderer.all.push(this); }

    onDestroy() {
        const i = SpriteRenderer.all.indexOf(this);
        if (i >= 0) SpriteRenderer.all.splice(i, 1);
    }

    start() { this._resolveTexture(); }

    /** Loads `texturePath` through the asset manager, once. */
    _resolveTexture() {
        if (this._resolved || !this.texturePath) return;
        this._resolved = true;

        const assets = this.actor?.scene?.engine?.assets;
        if (!assets) return;

        assets.loadTexture(this.texturePath)
            .then((texture) => { this.texture = texture; })
            .catch((err) => console.warn(`[SpriteRenderer] ${this.texturePath}: ${err.message}`));
    }

    /** The sub-rectangle this frame draws, or null for the whole texture. */
    getSourceRect() {
        if (this.sourceRect) return this.sourceRect;
        if (!this.texture || this.frameWidth <= 0 || this.frameHeight <= 0) return null;

        const columns = Math.max(1, Math.floor(this.texture.width / this.frameWidth));
        const column = this.frameIndex % columns;
        const row = Math.floor(this.frameIndex / columns);

        return {
            x: column * this.frameWidth,
            y: row * this.frameHeight,
            width: this.frameWidth,
            height: this.frameHeight,
        };
    }

    draw(batch) {
        if (!batch) return;
        this._resolveTexture();

        const transform = this.transform;
        const position = transform.position;
        const scale = transform.scale;
        const rotation = transform.rotation;

        if (!this.texture) {
            // No art yet: a tinted box, sized by `size` times the actor's scale,
            // pivoted the same way a sprite would be.
            const width = this.size.x * scale.x;
            const height = this.size.y * scale.y;
            const x = position.x - width * this.pivot.x;
            const y = position.y - height * this.pivot.y;

            if (rotation !== 0) {
                batch.drawPolygon(
                    rotatedCorners(position, x, y, width, height, rotation),
                    this.tint, { fill: true, layerDepth: this.layerDepth });
            } else {
                batch.fillRect(x, y, width, height, this.tint, this.layerDepth);
            }
            return;
        }

        batch.draw(this.texture, {
            position,
            sourceRect: this.getSourceRect(),
            tint: this.tint,
            rotation,
            origin: this.pivot,
            scale,
            effects: this.effects,
            layerDepth: this.layerDepth,
        });
    }
}
registerComponent(SpriteRenderer, { category: 'Rendering', summary: 'Draws a 2D sprite.' });

function rotatedCorners(origin, x, y, width, height, rotation) {
    const cos = Math.cos(rotation), sin = Math.sin(rotation);
    return [
        [x, y], [x + width, y], [x + width, y + height], [x, y + height],
    ].map(([px, py]) => {
        const dx = px - origin.x, dy = py - origin.y;
        return new Vector2(origin.x + dx * cos - dy * sin, origin.y + dx * sin + dy * cos);
    });
}
