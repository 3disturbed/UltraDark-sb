// -----------------------------------------------------------------------------
// SpriteBatch — the 2D drawing surface components receive in `draw`.
//
// Stands in for MonoGame's SpriteBatch, which is the one MonoGame type that
// reaches into every component's public API in the C# engine. Keeping the name
// and the begin/draw/end shape means a ported 2D component reads the same.
//
// Draw calls are queued and sorted by layer depth in `end`, matching the C#
// renderer's default `SpriteSortMode.BackToFront`. Canvas2D would otherwise draw
// strictly in call order and ignore depth entirely.
// -----------------------------------------------------------------------------

import { Vector2, Color, Matrix4 } from '../math/index.js';

/** How sprites are ordered within a batch. */
export const SpriteSortMode = Object.freeze({
    Deferred: 'Deferred',        // call order
    BackToFront: 'BackToFront',  // high layerDepth first
    FrontToBack: 'FrontToBack',
});

/** Horizontal and vertical mirroring, matching XNA's `SpriteEffects`. */
export const SpriteEffects = Object.freeze({
    None: 'None',
    FlipHorizontally: 'FlipHorizontally',
    FlipVertically: 'FlipVertically',
    FlipBoth: 'FlipBoth',
});

/** Batches 2D drawing onto a Canvas2D context. */
export class SpriteBatch {
    /** @param {CanvasRenderingContext2D} ctx */
    constructor(ctx) {
        this.ctx = ctx;
        this.sortMode = SpriteSortMode.BackToFront;

        this._commands = [];
        this._began = false;
        this._transform = null;

        /** Draw calls issued since the last `end`. Read by the debug overlay. */
        this.drawCalls = 0;
    }

    /** The canvas this batch draws to. */
    get canvas() { return this.ctx.canvas; }

    /**
     * Opens a batch.
     * @param {object} [options]
     * @param {Matrix4} [options.transform] The camera's view matrix.
     * @param {string} [options.sortMode]
     */
    begin({ transform = null, sortMode = SpriteSortMode.BackToFront } = {}) {
        if (this._began) this.end();
        this._began = true;
        this._transform = transform;
        this.sortMode = sortMode;
        this._commands.length = 0;
        this.drawCalls = 0;
    }

    /**
     * Queues a texture.
     *
     * @param {import('../assets/Texture2D.js').Texture2D} texture
     * @param {object} options
     * @param {Vector2} options.position World position of the origin.
     * @param {object}  [options.sourceRect] `{ x, y, width, height }` sub-region of the texture.
     * @param {Color}   [options.tint]
     * @param {number}  [options.rotation=0] Radians.
     * @param {Vector2} [options.origin] Pivot in 0..1 of the sprite's size.
     * @param {Vector2} [options.scale]
     * @param {string}  [options.effects] A {@link SpriteEffects} value.
     * @param {number}  [options.layerDepth=0]
     */
    draw(texture, options) {
        if (!texture?.source) return;
        this._commands.push({ kind: 'sprite', texture, ...options });
    }

    /** Queues a filled rectangle, in world units. */
    fillRect(x, y, width, height, color, layerDepth = 0) {
        this._commands.push({ kind: 'rect', x, y, width, height, color, fill: true, layerDepth });
    }

    /** Queues a rectangle outline. */
    strokeRect(x, y, width, height, color, thickness = 1, layerDepth = 0) {
        this._commands.push({
            kind: 'rect', x, y, width, height, color, fill: false, thickness, layerDepth,
        });
    }

    /** Queues a line segment. */
    drawLine(from, to, color, thickness = 1, layerDepth = 0) {
        this._commands.push({ kind: 'line', from, to, color, thickness, layerDepth });
    }

    /** Queues a circle, filled or outlined. */
    drawCircle(center, radius, color, { fill = false, thickness = 1, layerDepth = 0 } = {}) {
        this._commands.push({ kind: 'circle', center, radius, color, fill, thickness, layerDepth });
    }

    /** Queues a convex or concave polygon from world-space points. */
    drawPolygon(points, color, { fill = false, thickness = 1, layerDepth = 0 } = {}) {
        this._commands.push({ kind: 'polygon', points, color, fill, thickness, layerDepth });
    }

    /**
     * Queues text.
     * @param {object} [options]
     * @param {string} [options.font] A CSS font shorthand, e.g. '16px sans-serif'.
     * @param {CanvasTextAlign} [options.align]
     */
    drawString(text, position, color, options = {}) {
        this._commands.push({ kind: 'text', text, position, color, ...options });
    }

    /** Sorts and flushes every queued command. */
    end() {
        if (!this._began) return;
        this._began = false;

        const ctx = this.ctx;
        const commands = this._commands;

        if (this.sortMode === SpriteSortMode.BackToFront) {
            // A stable sort, which JS guarantees, keeps equal depths in call order.
            commands.sort((a, b) => (a.layerDepth ?? 0) - (b.layerDepth ?? 0));
        } else if (this.sortMode === SpriteSortMode.FrontToBack) {
            commands.sort((a, b) => (b.layerDepth ?? 0) - (a.layerDepth ?? 0));
        }

        ctx.save();
        if (this._transform) this._applyTransform(ctx, this._transform);

        for (const command of commands) {
            this._execute(ctx, command);
            this.drawCalls++;
        }

        ctx.restore();
        commands.length = 0;
    }

    /**
     * Applies a camera matrix.
     *
     * The matrix is a full 4x4 in XNA row-major layout; a 2D camera only ever
     * uses the scale, rotation and translation of the XY plane, which is exactly
     * the six numbers `setTransform` takes.
     */
    _applyTransform(ctx, m) {
        const a = m.m;
        ctx.setTransform(a[0], a[1], a[4], a[5], a[12], a[13]);
    }

    _execute(ctx, c) {
        switch (c.kind) {
            case 'sprite': return this._drawSprite(ctx, c);
            case 'rect':   return this._drawRect(ctx, c);
            case 'line':   return this._drawLine(ctx, c);
            case 'circle': return this._drawCircle(ctx, c);
            case 'polygon':return this._drawPolygon(ctx, c);
            case 'text':   return this._drawText(ctx, c);
            default: return undefined;
        }
    }

    _drawSprite(ctx, c) {
        const texture = c.texture;
        const src = c.sourceRect ?? { x: 0, y: 0, width: texture.width, height: texture.height };
        const scale = c.scale ?? Vector2.one;
        const origin = c.origin ?? new Vector2(0.5, 0.5);
        const position = c.position ?? Vector2.zero;
        const tint = c.tint ?? Color.white;

        const width = src.width * scale.x;
        const height = src.height * scale.y;

        ctx.save();
        ctx.translate(position.x, position.y);
        if (c.rotation) ctx.rotate(c.rotation);

        const flipX = c.effects === SpriteEffects.FlipHorizontally || c.effects === SpriteEffects.FlipBoth;
        const flipY = c.effects === SpriteEffects.FlipVertically || c.effects === SpriteEffects.FlipBoth;
        if (flipX || flipY) ctx.scale(flipX ? -1 : 1, flipY ? -1 : 1);

        ctx.globalAlpha = tint.a / 255;

        // A non-white tint needs a second pass: Canvas2D has no per-draw colour
        // multiply, so the sprite is composited against the tint on a scratch
        // canvas. White is the overwhelmingly common case and skips all of it.
        const source = (tint.r === 255 && tint.g === 255 && tint.b === 255)
            ? texture.source
            : tintedSource(texture, src, tint);

        const sourceRect = source === texture.source ? src : { x: 0, y: 0, width: src.width, height: src.height };

        ctx.drawImage(
            source,
            sourceRect.x, sourceRect.y, sourceRect.width, sourceRect.height,
            -width * origin.x, -height * origin.y, width, height);

        ctx.restore();
    }

    _drawRect(ctx, c) {
        ctx.save();
        const style = toStyle(c.color);
        if (c.fill) {
            ctx.fillStyle = style;
            ctx.fillRect(c.x, c.y, c.width, c.height);
        } else {
            ctx.strokeStyle = style;
            ctx.lineWidth = c.thickness ?? 1;
            ctx.strokeRect(c.x, c.y, c.width, c.height);
        }
        ctx.restore();
    }

    _drawLine(ctx, c) {
        ctx.save();
        ctx.strokeStyle = toStyle(c.color);
        ctx.lineWidth = c.thickness ?? 1;
        ctx.beginPath();
        ctx.moveTo(c.from.x, c.from.y);
        ctx.lineTo(c.to.x, c.to.y);
        ctx.stroke();
        ctx.restore();
    }

    _drawCircle(ctx, c) {
        ctx.save();
        ctx.beginPath();
        ctx.arc(c.center.x, c.center.y, Math.abs(c.radius), 0, Math.PI * 2);
        if (c.fill) {
            ctx.fillStyle = toStyle(c.color);
            ctx.fill();
        } else {
            ctx.strokeStyle = toStyle(c.color);
            ctx.lineWidth = c.thickness ?? 1;
            ctx.stroke();
        }
        ctx.restore();
    }

    _drawPolygon(ctx, c) {
        if (!c.points || c.points.length < 2) return;
        ctx.save();
        ctx.beginPath();
        ctx.moveTo(c.points[0].x, c.points[0].y);
        for (let i = 1; i < c.points.length; i++) ctx.lineTo(c.points[i].x, c.points[i].y);
        ctx.closePath();
        if (c.fill) {
            ctx.fillStyle = toStyle(c.color);
            ctx.fill();
        } else {
            ctx.strokeStyle = toStyle(c.color);
            ctx.lineWidth = c.thickness ?? 1;
            ctx.stroke();
        }
        ctx.restore();
    }

    _drawText(ctx, c) {
        ctx.save();
        ctx.fillStyle = toStyle(c.color);
        ctx.font = c.font ?? '16px system-ui, sans-serif';
        ctx.textAlign = c.align ?? 'left';
        ctx.textBaseline = c.baseline ?? 'top';
        ctx.fillText(c.text, c.position.x, c.position.y);
        ctx.restore();
    }
}

function toStyle(color) {
    if (!color) return '#FFFFFF';
    return typeof color === 'string' ? color : color.toCss();
}

// One scratch canvas, reused: allocating per tinted sprite per frame is the
// difference between a smooth frame and a stuttering one on a phone.
let _tintCanvas = null;
let _tintCtx = null;

function tintedSource(texture, src, tint) {
    if (!_tintCanvas) {
        _tintCanvas = typeof OffscreenCanvas !== 'undefined'
            ? new OffscreenCanvas(src.width, src.height)
            : Object.assign(document.createElement('canvas'), { width: src.width, height: src.height });
        _tintCtx = _tintCanvas.getContext('2d');
    }

    if (_tintCanvas.width !== src.width || _tintCanvas.height !== src.height) {
        _tintCanvas.width = src.width;
        _tintCanvas.height = src.height;
    }

    _tintCtx.globalCompositeOperation = 'source-over';
    _tintCtx.clearRect(0, 0, src.width, src.height);
    _tintCtx.drawImage(texture.source, src.x, src.y, src.width, src.height, 0, 0, src.width, src.height);

    // Multiply lays the tint over the sprite; source-in then restores the alpha
    // the multiply pass would otherwise have filled in across the whole rect.
    _tintCtx.globalCompositeOperation = 'multiply';
    _tintCtx.fillStyle = `rgb(${tint.r}, ${tint.g}, ${tint.b})`;
    _tintCtx.fillRect(0, 0, src.width, src.height);

    _tintCtx.globalCompositeOperation = 'destination-in';
    _tintCtx.drawImage(texture.source, src.x, src.y, src.width, src.height, 0, 0, src.width, src.height);

    return _tintCanvas;
}
