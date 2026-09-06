// -----------------------------------------------------------------------------
// Camera2D — the 2D view, mirroring Rendering/Camera2D.cs.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Vector2, Matrix4, SBMath } from '../math/index.js';

/** Smooth follow settings for a Camera2D. */
export class CameraFollow {
    constructor() {
        /** @type {?import('../core/Actor.js').Actor} */
        this.target = null;
        this.lerpSpeed = 5;
        this.offset = new Vector2(0, 0);
        /** Half-size of the box the target may move in before the camera reacts. */
        this.deadzone = new Vector2(0, 0);
    }
}

/** The 2D view transform. Tag its actor `MainCamera` to have the renderer find it. */
export class Camera2D extends Component {
    static schema = {
        zoom:          { type: P.Number, default: 1, min: 0.1, max: 10 },
        minZoom:       { type: P.Number, default: 0.1 },
        maxZoom:       { type: P.Number, default: 10 },
        shakeMaxOffset:{ type: P.Number, default: 20 },
        shakeMaxAngle: { type: P.Number, default: 0.05 },
    };

    /** Every live 2D camera, so the renderer can pick one without walking the scene. */
    static all = [];

    /** The camera the 2D renderer uses when none is named explicitly. */
    static get main() {
        return Camera2D.all.find((c) => c.actor?.tag === 'MainCamera')
            ?? Camera2D.all[0]
            ?? null;
    }

    constructor() {
        super();
        this._zoom = 1;
        this.minZoom = 0.1;
        this.maxZoom = 10;

        /** Optional world-space rectangle the view is confined to. */
        this.bounds = null;

        this.follow = new CameraFollow();

        this.shakeMaxOffset = 20;
        this.shakeMaxAngle = 0.05;
        this._shakeTrauma = 0;
        this._shakeDecay = 1;
        this._shakeOffset = new Vector2(0, 0);
        this._shakeAngle = 0;

        /** Viewport size in pixels. The host keeps this in step with the canvas. */
        this.viewportWidth = 1280;
        this.viewportHeight = 720;
    }

    get zoom() { return this._zoom; }
    set zoom(value) { this._zoom = SBMath.clamp(value, this.minZoom, this.maxZoom); }

    awake() { Camera2D.all.push(this); }

    onDestroy() {
        const i = Camera2D.all.indexOf(this);
        if (i >= 0) Camera2D.all.splice(i, 1);
    }

    /** Tells the camera how big the surface it is drawing to is. */
    setViewport(width, height) {
        this.viewportWidth = width;
        this.viewportHeight = height;
    }

    /**
     * The matrix the sprite batch draws through: world space into screen pixels,
     * with the camera's position at the centre of the viewport.
     */
    getViewMatrix() {
        const position = this.transform.position;
        const rotation = this.transform.rotation + this._shakeAngle;
        const halfWidth = this.viewportWidth / 2;
        const halfHeight = this.viewportHeight / 2;

        return Matrix4.multiply(
            Matrix4.multiply(
                Matrix4.multiply(
                    Matrix4.translation(
                        -position.x - this._shakeOffset.x,
                        -position.y - this._shakeOffset.y, 0),
                    Matrix4.rotationZ(-rotation)),
                Matrix4.scale(this._zoom, this._zoom, 1)),
            Matrix4.translation(halfWidth, halfHeight, 0));
    }

    /** Converts a point in canvas pixels to world space. */
    screenToWorld(screenPoint) {
        const inverse = Matrix4.invert(this.getViewMatrix());
        const p = Matrix4.transformPoint(inverse, { x: screenPoint.x, y: screenPoint.y, z: 0 });
        return new Vector2(p.x, p.y);
    }

    /** Converts a world point to canvas pixels. */
    worldToScreen(worldPoint) {
        const p = Matrix4.transformPoint(this.getViewMatrix(), { x: worldPoint.x, y: worldPoint.y, z: 0 });
        return new Vector2(p.x, p.y);
    }

    /** Adds camera shake. Intensity is 0..1; it decays over `duration` seconds. */
    shake(intensity, duration = 0.5) {
        this._shakeTrauma = Math.min(1, this._shakeTrauma + intensity);
        this._shakeDecay = duration > 0 ? 1 / duration : 1;
    }

    /**
     * Follow and shake run in lateUpdate so the camera reacts to where things
     * ended up this frame rather than where they were at the start of it.
     */
    lateUpdate(dt) {
        this._updateFollow(dt);
        this._updateShake(dt);
        this._applyBounds();
    }

    _updateFollow(dt) {
        const target = this.follow.target;
        if (!target || target.isDestroyed) return;

        const wanted = Vector2.add(target.transform.position, this.follow.offset);
        const current = this.transform.position;
        const delta = Vector2.subtract(wanted, current);

        // Inside the deadzone the camera holds still, so small idle movements do
        // not make the whole screen drift.
        if (Math.abs(delta.x) <= this.follow.deadzone.x) delta.x = 0;
        if (Math.abs(delta.y) <= this.follow.deadzone.y) delta.y = 0;

        const t = 1 - Math.exp(-this.follow.lerpSpeed * dt);
        this.transform.position = new Vector2(current.x + delta.x * t, current.y + delta.y * t);
    }

    _updateShake(dt) {
        if (this._shakeTrauma <= 0) {
            this._shakeOffset.set(0, 0);
            this._shakeAngle = 0;
            return;
        }

        this._shakeTrauma = Math.max(0, this._shakeTrauma - this._shakeDecay * dt);

        // Squaring the trauma makes a small shake feel gentle and a large one
        // violent, rather than everything reading the same.
        const magnitude = this._shakeTrauma * this._shakeTrauma;
        this._shakeOffset.set(
            (Math.random() * 2 - 1) * this.shakeMaxOffset * magnitude,
            (Math.random() * 2 - 1) * this.shakeMaxOffset * magnitude);
        this._shakeAngle = (Math.random() * 2 - 1) * this.shakeMaxAngle * magnitude;
    }

    _applyBounds() {
        if (!this.bounds) return;

        const halfWidth = this.viewportWidth / (2 * this._zoom);
        const halfHeight = this.viewportHeight / (2 * this._zoom);
        const p = this.transform.position;

        // When the view is wider than the bounds, centring beats clamping: an
        // unclampable axis would otherwise stick to one edge.
        const x = this.bounds.width <= halfWidth * 2
            ? this.bounds.x + this.bounds.width / 2
            : SBMath.clamp(p.x, this.bounds.x + halfWidth, this.bounds.right - halfWidth);
        const y = this.bounds.height <= halfHeight * 2
            ? this.bounds.y + this.bounds.height / 2
            : SBMath.clamp(p.y, this.bounds.y + halfHeight, this.bounds.bottom - halfHeight);

        this.transform.position = new Vector2(x, y);
    }
}
registerComponent(Camera2D, { category: 'Rendering', summary: 'The 2D view transform.' });
