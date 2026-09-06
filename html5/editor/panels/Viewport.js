// -----------------------------------------------------------------------------
// Viewport — the scene view, its camera, and picking.
//
// The editor camera is an Actor kept deliberately *outside* the scene, so it is
// never serialised, never destroyed by a scene swap, and never shows up in the
// hierarchy — the same arrangement the C# editor uses.
// -----------------------------------------------------------------------------

import { el } from '../dom.js';
import { Actor } from '../../src/core/Actor.js';
import { Transform3D } from '../../src/core/Transform3D.js';
import { Camera3D } from '../../src/rendering/Camera3D.js';
import { Camera2D } from '../../src/rendering/Camera2D.js';
import { MeshRenderer } from '../../src/rendering/MeshRenderer.js';
import { SpriteRenderer } from '../../src/rendering/SpriteRenderer.js';
import { Vector2, Vector3, Quaternion, Color, SBMath } from '../../src/math/index.js';

/** The scene view. */
export class ViewportPanel {
    constructor(state, editor) {
        this.state = state;
        this.editor = editor;

        this.root = el('div.sb-viewport');
        this.surface = el('div.sb-viewport-surface');
        this.root.append(this.surface);

        /** Metres per second the fly camera moves. */
        this.flySpeed = 8;
        this.lookSensitivity = 0.22;

        this._cameraActor = null;
        this._camera = null;
        this._orbitTarget = new Vector3(0, 0.5, 0);
        this._orbitDistance = 9;
        this._yaw = 0;
        this._pitch = -18;

        this._dragging = null;
        this._pointerStart = null;
        this._pinchStart = null;
    }

    /** The camera the renderer draws through while editing. */
    get camera() { return this._camera; }

    /** Builds the editor camera and wires the view's own pointer handling. */
    attach(engine) {
        this.engine = engine;
        engine.attach(this.surface);

        this._cameraActor = new Actor('__EditorCamera__');
        this._cameraActor.addComponent(Transform3D);
        this._camera = this._cameraActor.addComponent(Camera3D);
        this._applyOrbit();

        this._installPointerHandlers();
        return this;
    }

    /** Points the renderer at whichever camera the editor should be showing. */
    syncRenderCamera() {
        if (!this.engine?.renderer3D) return;

        // Looking through the game's camera is the point of the toggle, and of
        // play mode: the editor camera would otherwise override it.
        this.engine.renderer3D.overrideCamera =
            (this.state.useGameCamera || this.state.isPlaying) ? null : this._camera;
    }

    // -------------------------------------------------------------------------
    // Camera
    // -------------------------------------------------------------------------

    /** Frames an actor, filling roughly two thirds of the view with it. */
    focusOn(actor) {
        if (!actor) return;

        const t3d = actor.getComponent(Transform3D);
        const centre = t3d
            ? t3d.position.clone()
            : new Vector3(actor.transform.position.x, actor.transform.position.y, 0);

        const renderer = actor.getComponent(MeshRenderer);
        const radius = renderer ? Math.max(renderer.worldBounds.boundingRadius, 0.5) : 1;

        this._orbitTarget = centre;
        this._orbitDistance = SBMath.clamp(radius * 3.2, 1.5, 200);
        this._applyOrbit();
    }

    /** Places the camera on its orbit around the focus point. */
    _applyOrbit() {
        const t = this._cameraActor.transform3D;
        const pitch = this._pitch * SBMath.DEG2RAD;
        const yaw = this._yaw * SBMath.DEG2RAD;

        const horizontal = Math.cos(pitch) * this._orbitDistance;
        t.position = new Vector3(
            this._orbitTarget.x + horizontal * Math.sin(yaw),
            this._orbitTarget.y - Math.sin(pitch) * this._orbitDistance,
            this._orbitTarget.z + horizontal * Math.cos(yaw));

        t.lookAt(this._orbitTarget);
    }

    /** Moves the focus point in the camera's own plane. */
    _pan(dx, dy) {
        const t = this._cameraActor.transform3D;
        // Scale the pan by distance, so dragging feels the same whether the
        // camera is a metre away or fifty.
        const scale = this._orbitDistance * 0.0022;
        this._orbitTarget = Vector3.add(this._orbitTarget, Vector3.add(
            Vector3.scale(t.right, -dx * scale),
            Vector3.scale(t.up, dy * scale)));
        this._applyOrbit();
    }

    _zoom(delta) {
        // Multiplicative, so a notch covers the same proportion of the distance
        // at every scale rather than crawling when far out.
        this._orbitDistance = SBMath.clamp(this._orbitDistance * (1 - delta * 0.12), 0.4, 500);
        this._applyOrbit();
    }

    _orbit(dx, dy) {
        this._yaw -= dx * this.lookSensitivity;
        this._pitch = SBMath.clamp(this._pitch - dy * this.lookSensitivity, -89, 89);
        this._applyOrbit();
    }

    // -------------------------------------------------------------------------
    // Pointer handling
    // -------------------------------------------------------------------------

    _installPointerHandlers() {
        const surface = this.surface;

        surface.addEventListener('pointerdown', (e) => {
            if (this.state.isPlaying) return;   // the game owns input while playing

            surface.setPointerCapture(e.pointerId);
            this._pointerStart = { x: e.clientX, y: e.clientY, time: performance.now() };

            // Middle button or Shift pans; anything else orbits. A touch drag
            // orbits, and two fingers pinch to zoom.
            this._dragging = (e.button === 1 || e.shiftKey) ? 'pan' : 'orbit';
        });

        surface.addEventListener('pointermove', (e) => {
            if (!this._dragging || this.state.isPlaying) return;

            const dx = e.movementX ?? 0;
            const dy = e.movementY ?? 0;
            if (this._dragging === 'pan') this._pan(dx, dy);
            else this._orbit(dx, dy);
        });

        surface.addEventListener('pointerup', (e) => {
            const start = this._pointerStart;
            this._dragging = null;
            this._pointerStart = null;
            if (!start || this.state.isPlaying) return;

            // A short press that barely moved is a click, and a click selects.
            const moved = Math.hypot(e.clientX - start.x, e.clientY - start.y);
            if (moved < 5 && performance.now() - start.time < 600) this._pick(e);
        });

        surface.addEventListener('pointercancel', () => { this._dragging = null; });

        surface.addEventListener('wheel', (e) => {
            if (this.state.isPlaying) return;
            e.preventDefault();
            this._zoom(-e.deltaY / 100);
        }, { passive: false });

        // Two-finger pinch, for tablets.
        const active = new Map();
        surface.addEventListener('pointerdown', (e) => {
            if (e.pointerType === 'touch') active.set(e.pointerId, e);
        });
        surface.addEventListener('pointermove', (e) => {
            if (e.pointerType !== 'touch' || !active.has(e.pointerId)) return;
            active.set(e.pointerId, e);
            if (active.size !== 2) return;

            const [a, b] = [...active.values()];
            const distance = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);

            if (this._pinchStart) this._zoom((distance - this._pinchStart) / 60);
            this._pinchStart = distance;
            this._dragging = null;   // a pinch is not an orbit
        });
        const endTouch = (e) => {
            active.delete(e.pointerId);
            if (active.size < 2) this._pinchStart = null;
        };
        surface.addEventListener('pointerup', endTouch);
        surface.addEventListener('pointercancel', endTouch);
    }

    /**
     * Selects whatever is under the pointer.
     *
     * A ray against each renderer's bounding sphere: cheap, and precise enough
     * for picking, which only has to agree with what the eye expects.
     */
    _pick(event) {
        const scene = this.editor.scene;
        if (!scene || !this._camera) return;

        const rect = this.surface.getBoundingClientRect();
        const ratio = this.engine.canvas2D.width / rect.width;
        const point = {
            x: (event.clientX - rect.left) * ratio,
            y: (event.clientY - rect.top) * ratio,
        };

        if (this.state.viewport3D) {
            const found = this._pick3D(scene, point);
            this.state.selectActor(found);
            return;
        }

        this.state.selectActor(this._pick2D(scene, point));
    }

    _pick3D(scene, point) {
        const ray = this._camera.screenToWorldRay(
            point, this.engine.canvas3D.width, this.engine.canvas3D.height);

        let best = null;
        let bestDistance = Infinity;

        for (const renderer of MeshRenderer.all) {
            if (renderer.actor?.scene !== scene || !renderer.actor.isActive) continue;

            const bounds = renderer.worldBounds;
            const hit = raySphere(ray.origin, ray.direction, bounds.center, bounds.boundingRadius);
            if (hit === null || hit >= bestDistance) continue;

            bestDistance = hit;
            best = renderer.actor;
        }

        return best;
    }

    _pick2D(scene, point) {
        const camera = Camera2D.main;
        const world = camera
            ? camera.screenToWorld(new Vector2(point.x, point.y))
            : new Vector2(point.x, point.y);

        // Later actors draw on top, so search backwards to hit the visible one.
        const candidates = SpriteRenderer.all ?? [];
        for (let i = candidates.length - 1; i >= 0; i--) {
            const renderer = candidates[i];
            if (renderer.actor?.scene !== scene) continue;

            const position = renderer.transform.position;
            const scale = renderer.transform.scale;
            const half = new Vector2(
                Math.abs(renderer.size.x * scale.x) / 2,
                Math.abs(renderer.size.y * scale.y) / 2);

            if (Math.abs(world.x - position.x) <= half.x && Math.abs(world.y - position.y) <= half.y) {
                return renderer.actor;
            }
        }

        // Fall back to nearest-actor within a small radius, so an actor with no
        // renderer is still selectable.
        let best = null;
        let bestDistance = 40;
        for (const actor of scene.allActors) {
            const distance = Vector2.distance(actor.transform.position, world);
            if (distance < bestDistance) { bestDistance = distance; best = actor; }
        }
        return best;
    }

    /** Draws the selection outline over the finished frame. */
    drawOverlay() {
        const actor = this.state.selectedActor;
        const ctx = this.engine?.ctx;
        if (!actor || actor.isDestroyed || !ctx || !this._camera) return;

        const t3d = actor.getComponent(Transform3D);
        if (!t3d || !this.state.viewport3D) return;

        const renderer = actor.getComponent(MeshRenderer);
        const bounds = renderer ? renderer.worldBounds : null;
        const centre = bounds?.center ?? t3d.position;
        const radius = bounds ? bounds.boundingRadius : 0.5;

        const screen = this._camera.worldToScreen(
            centre, this.engine.canvas2D.width, this.engine.canvas2D.height);
        if (!screen) return;

        // Project a point one radius to the side to get the on-screen size,
        // rather than guessing at a fixed pixel radius.
        const edge = this._camera.worldToScreen(
            Vector3.add(centre, Vector3.scale(this._cameraActor.transform3D.right, radius)),
            this.engine.canvas2D.width, this.engine.canvas2D.height);
        const screenRadius = edge ? Math.max(Math.abs(edge.x - screen.x), 8) : 24;

        ctx.save();
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.strokeStyle = '#FFB454';
        ctx.lineWidth = 2;
        ctx.setLineDash([6, 4]);
        ctx.beginPath();
        ctx.arc(screen.x, screen.y, screenRadius, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();
    }
}

/** The nearest ray-sphere intersection ahead of the origin, or null. */
function raySphere(origin, direction, centre, radius) {
    const ox = origin.x - centre.x;
    const oy = origin.y - centre.y;
    const oz = origin.z - centre.z;

    const b = ox * direction.x + oy * direction.y + oz * direction.z;
    const c = ox * ox + oy * oy + oz * oz - radius * radius;

    if (c > 0 && b > 0) return null;              // outside and pointing away
    const discriminant = b * b - c;
    if (discriminant < 0) return null;

    const t = -b - Math.sqrt(discriminant);
    return t < 0 ? 0 : t;                          // 0 when the origin is inside
}
