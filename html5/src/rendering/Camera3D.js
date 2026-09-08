// -----------------------------------------------------------------------------
// Camera3D — the 3D view.
//
// `Camera3D.main` prefers the camera the player is actually looking through:
// a possessed pawn's camera wins over a scene camera tagged MainCamera3D, which
// is what makes Play show the game's view rather than the editor's.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Vector3, Matrix4, DEG2RAD } from '../math/index.js';
import { Transform3D } from '../core/Transform3D.js';

/** The 3D view transform. */
export class Camera3D extends Component {
    static schema = {
        fieldOfView:    { type: P.Number, default: 60, min: 1, max: 179 },
        nearClip:       { type: P.Number, default: 0.1, min: 0.001 },
        farClip:        { type: P.Number, default: 1000 },
        isOrthographic: { type: P.Bool, default: false },
        orthoSize:      { type: P.Number, default: 5, min: 0.01 },
    };

    /** Every live 3D camera. */
    static all = [];

    /** The camera a possessed pawn owns. Set by the player controller. */
    static playerView = null;

    /** Drops every registered camera. A test that leaks one poisons the next. */
    static clearAll() {
        Camera3D.all.length = 0;
        Camera3D.playerView = null;
    }

    /**
     * The view the renderer draws through: the possessed pawn's camera, else one tagged
     * 'MainCamera3D', else one tagged 'MainCamera', else whichever was added first.
     *
     * The liveness check is not decoration -- a switched-off camera used to win this
     * election here while the C# twin skipped it, so a scene that disabled its menu camera
     * rendered through it in the browser and through the next one natively. CameraParityTests
     * and cameraMain.test.js pin the order and the check together.
     */
    static get main() {
        if (Camera3D.playerView && live(Camera3D.playerView)) return Camera3D.playerView;

        return Camera3D.all.find((c) => live(c) && c.actor.tag === 'MainCamera3D')
            ?? Camera3D.all.find((c) => live(c) && c.actor.tag === 'MainCamera')
            ?? Camera3D.all.find(live)
            ?? null;
    }

    constructor() {
        super();
        /** Vertical field of view, in degrees. */
        this.fieldOfView = 60;
        this.nearClip = 0.1;
        this.farClip = 1000;
        this.isOrthographic = false;
        /** Half the vertical extent of the view when orthographic. */
        this.orthoSize = 5;
    }

    awake() {
        Camera3D.all.push(this);
        // A camera needs a 3D transform; a scene file may only carry the 2D one.
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    onDestroy() {
        const i = Camera3D.all.indexOf(this);
        if (i >= 0) Camera3D.all.splice(i, 1);
        if (Camera3D.playerView === this) Camera3D.playerView = null;
    }

    /** This camera's 3D transform, added on awake if the actor lacked one. */
    getTransform3D() {
        return this.actor.getComponent(Transform3D) ?? this.actor.addComponent(Transform3D);
    }

    /** World space into view space. */
    getViewMatrix() {
        const t = this.getTransform3D();
        const eye = t.position;
        const target = Vector3.add(eye, t.forward);
        return Matrix4.lookAt(eye, target, t.up);
    }

    /** View space into clip space. */
    getProjectionMatrix(aspectRatio) {
        if (this.isOrthographic) {
            const halfHeight = this.orthoSize;
            const halfWidth = halfHeight * aspectRatio;
            return Matrix4.orthographic(
                -halfWidth, halfWidth, -halfHeight, halfHeight, this.nearClip, this.farClip);
        }
        return Matrix4.perspective(
            this.fieldOfView * DEG2RAD, aspectRatio, this.nearClip, this.farClip);
    }

    /**
     * A ray from the camera through a point on screen, for picking.
     *
     * @param {{x: number, y: number}} screenPoint Canvas pixels.
     * @param {number} viewportWidth
     * @param {number} viewportHeight
     * @returns {{origin: Vector3, direction: Vector3}}
     */
    screenToWorldRay(screenPoint, viewportWidth, viewportHeight) {
        // Screen pixels into normalised device coordinates, where Y is flipped
        // because canvas Y grows downwards and clip space Y grows upwards.
        const ndcX = (screenPoint.x / viewportWidth) * 2 - 1;
        const ndcY = 1 - (screenPoint.y / viewportHeight) * 2;

        const t = this.getTransform3D();
        const origin = t.position;

        if (this.isOrthographic) {
            const halfHeight = this.orthoSize;
            const halfWidth = halfHeight * (viewportWidth / viewportHeight);
            const offset = Vector3.add(
                Vector3.scale(t.right, ndcX * halfWidth),
                Vector3.scale(t.up, ndcY * halfHeight));
            return { origin: Vector3.add(origin, offset), direction: t.forward.normalize() };
        }

        const tanHalfFov = Math.tan(this.fieldOfView * DEG2RAD / 2);
        const aspect = viewportWidth / viewportHeight;

        const direction = Vector3.add(
            Vector3.add(
                t.forward,
                Vector3.scale(t.right, ndcX * tanHalfFov * aspect)),
            Vector3.scale(t.up, ndcY * tanHalfFov)).normalize();

        return { origin, direction };
    }

    /** Projects a world point to canvas pixels, or null when it is behind the camera. */
    worldToScreen(worldPoint, viewportWidth, viewportHeight) {
        const viewProjection = Matrix4.multiply(
            this.getViewMatrix(), this.getProjectionMatrix(viewportWidth / viewportHeight));

        const p = Vector3.from(worldPoint);
        const m = viewProjection.m;
        const w = m[3] * p.x + m[7] * p.y + m[11] * p.z + m[15];
        if (w <= 0) return null;

        const clipX = (m[0] * p.x + m[4] * p.y + m[8] * p.z + m[12]) / w;
        const clipY = (m[1] * p.x + m[5] * p.y + m[9] * p.z + m[13]) / w;

        return {
            x: (clipX * 0.5 + 0.5) * viewportWidth,
            y: (1 - (clipY * 0.5 + 0.5)) * viewportHeight,
        };
    }
}
/** Whether a camera is eligible to be `main`: switched on, attached and alive. */
function live(camera) {
    return camera.enabled === true
        && camera.actor != null
        && !camera.actor.isDestroyed
        && camera.actor.isActive;
}

registerComponent(Camera3D, { category: 'Rendering', summary: 'The 3D view transform.' });
