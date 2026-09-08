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
import { Vector2, Vector3, SBMath } from '../../src/math/index.js';
import { rayVsBounds } from '../../src/physics/PhysicsSystem3D.js';

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
        this._gizmoDrag = null;
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

            // Transform handles get first refusal. Without this guard a handle
            // drag also begins an orbit, which makes the object appear to run
            // away from the cursor rather than move with it.
            if (this._startGizmo(e)) return;

            // Middle button or Shift pans; anything else orbits. A touch drag
            // orbits, and two fingers pinch to zoom.
            this._dragging = (e.button === 1 || e.shiftKey) ? 'pan' : 'orbit';
        });

        surface.addEventListener('pointermove', (e) => {
            if (this._gizmoDrag) {
                this._updateGizmo(e);
                return;
            }
            if (!this._dragging || this.state.isPlaying) return;

            const dx = e.movementX ?? 0;
            const dy = e.movementY ?? 0;
            if (this._dragging === 'pan') this._pan(dx, dy);
            else this._orbit(dx, dy);
        });

        surface.addEventListener('pointerup', (e) => {
            const start = this._pointerStart;
            if (this._gizmoDrag) {
                this._finishGizmo();
                this._pointerStart = null;
                return;
            }
            this._dragging = null;
            this._pointerStart = null;
            if (!start || this.state.isPlaying) return;

            // A short press that barely moved is a click, and a click selects.
            const moved = Math.hypot(e.clientX - start.x, e.clientY - start.y);
            if (moved < 5 && performance.now() - start.time < 600) this._pick(e);
        });

        surface.addEventListener('pointercancel', () => {
            this._dragging = null;
            this._finishGizmo(true);
        });

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
            this._selectPicked(found, event);
            return;
        }

        this._selectPicked(this._pick2D(scene, point), event);
    }

    _selectPicked(actor, event) {
        if (event.ctrlKey || event.metaKey) {
            if (actor) this.state.toggleActor(actor);
            return;
        }
        this.state.selectActor(actor);
    }

    _pick3D(scene, point) {
        const ray = this._camera.screenToWorldRay(
            point, this.engine.canvas3D.width, this.engine.canvas3D.height);

        let best = null;
        let bestDistance = Infinity;

        for (const renderer of MeshRenderer.all) {
            if (renderer.actor?.scene !== scene || !renderer.actor.isActive) continue;

            // Against the box, not a sphere around it. A sphere is a fine proxy
            // for a cube and a terrible one for anything flat or long: the default
            // floor's was a twenty-one-metre ball centred on the origin, so it won
            // every pick in the scene.
            const hit = rayVsBounds(ray.origin, ray.direction, Infinity, renderer.worldBounds);
            if (!hit || hit.distance >= bestDistance) continue;

            bestDistance = hit.distance;
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

    /** Draws the selection outline and editor gizmo over the finished frame. */
    drawOverlay() {
        const ctx = this.engine?.ctx;
        if (!ctx || !this._camera) return;
        if (!this.state.viewport3D) return;

        for (const actor of this.state.selectedActors) this._drawSelectionBounds(ctx, actor);

        const primary = this.state.selectedActor;
        const transform = primary?.getComponent(Transform3D);
        if (transform && !primary.isDestroyed) this._drawGizmo(ctx, transform);
    }

    _drawSelectionBounds(ctx, actor) {
        if (!actor || actor.isDestroyed) return;
        const t3d = actor.getComponent(Transform3D);
        if (!t3d) return;

        const renderer = actor.getComponent(MeshRenderer);
        const width = this.engine.canvas2D.width;
        const height = this.engine.canvas2D.height;

        // Project the eight corners of the actor's box and outline what they
        // cover. A circle sized from the bounding radius drew a screen-filling
        // ring around anything flat or long, which said nothing about what was
        // actually selected.
        const box = renderer ? renderer.worldBounds : null;
        const points = box
            ? boxCorners(box).map((corner) => this._camera.worldToScreen(corner, width, height))
            : [this._camera.worldToScreen(t3d.position, width, height)];

        const visible = points.filter(Boolean);
        if (visible.length === 0) return;   // entirely behind the camera

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const p of visible) {
            if (p.x < minX) minX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.x > maxX) maxX = p.x;
            if (p.y > maxY) maxY = p.y;
        }

        // An actor with no renderer projects to a single point; give it a handle
        // big enough to see.
        const padding = box ? 3 : 14;

        ctx.save();
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.strokeStyle = '#FFB454';
        ctx.lineWidth = 2;
        ctx.setLineDash([6, 4]);
        ctx.strokeRect(
            minX - padding, minY - padding,
            (maxX - minX) + padding * 2, (maxY - minY) + padding * 2);
        ctx.restore();
    }

    // -------------------------------------------------------------------------
    // Transform gizmo
    // -------------------------------------------------------------------------

    /** Returns the canvas-space point for a pointer event. */
    _canvasPoint(event) {
        const rect = this.surface.getBoundingClientRect();
        return {
            x: (event.clientX - rect.left) * this.engine.canvas2D.width / rect.width,
            y: (event.clientY - rect.top) * this.engine.canvas2D.height / rect.height,
        };
    }

    _gizmoGeometry(transform) {
        if (!this._camera || !this.engine) return null;
        const width = this.engine.canvas2D.width;
        const height = this.engine.canvas2D.height;
        const origin = this._camera.worldToScreen(transform.position, width, height);
        if (!origin) return null;

        const bases = [Vector3.right, Vector3.up, Vector3.forward];
        const colours = ['#e86a5f', '#6fbf73', '#5b8dd9'];
        const axes = bases.map((basis, index) => {
            const world = this.state.transformSpace === 'local'
                ? Vector3.transform(basis, transform.rotation)
                : basis;
            const probe = this._camera.worldToScreen(
                Vector3.add(transform.position, world), width, height);
            if (!probe) return { world, colour: colours[index], screen: null, end: origin, worldPerPixel: 0 };

            const dx = probe.x - origin.x;
            const dy = probe.y - origin.y;
            const length = Math.hypot(dx, dy);
            if (length < 0.5) return { world, colour: colours[index], screen: null, end: origin, worldPerPixel: 0 };

            const screen = { x: dx / length, y: dy / length };
            return {
                world, colour: colours[index], screen,
                end: { x: origin.x + screen.x * 76, y: origin.y + screen.y * 76 },
                worldPerPixel: 1 / length,
            };
        });
        return { origin, axes };
    }

    _drawGizmo(ctx, transform) {
        const geometry = this._gizmoGeometry(transform);
        if (!geometry) return;

        const mode = this.state.gizmoMode;
        ctx.save();
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.lineWidth = 3;
        ctx.setLineDash([]);

        for (const axis of geometry.axes) {
            if (!axis.screen) continue;
            ctx.strokeStyle = axis.colour;
            if (mode === 'rotate') {
                ctx.beginPath();
                ctx.arc(geometry.origin.x, geometry.origin.y, 38, 0, Math.PI * 2);
                ctx.stroke();
            } else {
                ctx.beginPath();
                ctx.moveTo(geometry.origin.x, geometry.origin.y);
                ctx.lineTo(axis.end.x, axis.end.y);
                ctx.stroke();
                ctx.fillStyle = axis.colour;
                ctx.beginPath();
                ctx.arc(axis.end.x, axis.end.y, mode === 'scale' ? 6 : 5, 0, Math.PI * 2);
                ctx.fill();
            }
        }

        ctx.fillStyle = '#ffffff';
        ctx.strokeStyle = '#14161c';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(geometry.origin.x, geometry.origin.y, 7, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
        ctx.restore();
    }

    _pickGizmo(point, geometry) {
        if (Math.hypot(point.x - geometry.origin.x, point.y - geometry.origin.y) <= 11)
            return 'free';

        let chosen = null;
        let best = 10;
        for (let index = 0; index < geometry.axes.length; index++) {
            const axis = geometry.axes[index];
            if (!axis.screen) continue;
            const distance = distanceToSegment(point, geometry.origin, axis.end);
            if (distance < best) { best = distance; chosen = index; }
        }
        return chosen;
    }

    _startGizmo(event) {
        if (!this.state.viewport3D || event.button === 1 || this.state.isPlaying) return false;
        const actor = this.state.selectedActor;
        const transform = actor?.getComponent(Transform3D);
        if (!actor || !transform) return false;

        const geometry = this._gizmoGeometry(transform);
        if (!geometry) return false;
        const axis = this._pickGizmo(this._canvasPoint(event), geometry);
        if (axis == null) return false;

        // The primary actor supplies the gizmo's origin and axes. Every selected
        // 3D transform receives the same operation around its own origin; this
        // keeps a multi-selection's relative layout intact until pivot modes
        // (median/bounds) are introduced.
        const transforms = this.state.selectedActors
            .map((selected) => ({ actor: selected, transform: selected.getComponent(Transform3D) }))
            .filter((entry) => entry.transform && !entry.actor.isDestroyed)
            .map((entry) => ({ ...entry, start: snapshotTransform(entry.transform) }));

        this._gizmoDrag = {
            actor,
            transform,
            transforms,
            axis,
            startPoint: this._canvasPoint(event),
        };
        return true;
    }

    _updateGizmo(event) {
        const drag = this._gizmoDrag;
        if (!drag) return;
        const geometry = this._gizmoGeometry(drag.transform);
        if (!geometry) return;

        const point = this._canvasPoint(event);
        const delta = { x: point.x - drag.startPoint.x, y: point.y - drag.startPoint.y };
        const mode = this.state.gizmoMode;

        if (mode === 'translate') {
            let move;
            if (drag.axis === 'free') {
                const camera = this._camera.getTransform3D();
                const scale = this._orbitDistance * 0.0022;
                move = Vector3.add(Vector3.scale(camera.right, -delta.x * scale),
                    Vector3.scale(camera.up, delta.y * scale));
            } else {
                const axis = geometry.axes[drag.axis];
                const pixels = delta.x * axis.screen.x + delta.y * axis.screen.y;
                move = Vector3.scale(axis.world, pixels * axis.worldPerPixel);
            }
            for (const entry of drag.transforms) {
                let position = Vector3.add(entry.start.position, move);
                if (this.state.snapEnabled) position = snapVector(position, this.state.translateSnap);
                entry.transform.position = position;
            }
        } else if (mode === 'rotate') {
            const degrees = snapValue((delta.x - delta.y) * 0.45,
                this.state.snapEnabled ? this.state.rotateSnap : 0);
            for (const entry of drag.transforms) {
                const euler = entry.start.euler.clone();
                if (drag.axis === 'free') euler.y += degrees;
                else if (drag.axis === 0) euler.x += degrees;
                else if (drag.axis === 1) euler.y += degrees;
                else euler.z += degrees;
                entry.transform.eulerAngles = euler;
            }
        } else {
            const amount = (delta.x - delta.y) / 100;
            const scaleFactor = Math.max(0.01, 1 + amount);
            for (const entry of drag.transforms) {
                let scale = entry.start.scale.clone();
                if (drag.axis === 'free') scale.scale(scaleFactor);
                else if (drag.axis === 0) scale.x *= scaleFactor;
                else if (drag.axis === 1) scale.y *= scaleFactor;
                else scale.z *= scaleFactor;
                if (this.state.snapEnabled) scale = snapVector(scale, this.state.scaleSnap);
                entry.transform.localScale = scale;
            }
        }
        this.state.markDirty();
    }

    _finishGizmo(cancel = false) {
        const drag = this._gizmoDrag;
        if (!drag) return;
        this._gizmoDrag = null;

        const end = drag.transforms.map((entry) => ({ ...entry, end: snapshotTransform(entry.transform) }));
        if (cancel) {
            for (const entry of drag.transforms) applyTransform(entry.transform, entry.start);
            return;
        }
        if (end.every((entry) => sameTransform(entry.start, entry.end))) return;

        const label = end.length === 1 ? `Transform ${drag.actor.name}` : `Transform ${end.length} actors`;
        this.editor.history.push(label,
            () => {
                for (const entry of end) applyTransform(entry.transform, entry.start);
                this.inspectorRefresh();
            },
            () => {
                for (const entry of end) applyTransform(entry.transform, entry.end);
                this.inspectorRefresh();
            });
        this.inspectorRefresh();
    }

    inspectorRefresh() { this.editor.inspector?.render(); }
}

function snapshotTransform(transform) {
    return {
        position: transform.position.clone(),
        scale: transform.localScale.clone(),
        euler: transform.eulerAngles.clone(),
    };
}

function applyTransform(transform, value) {
    transform.position = value.position;
    transform.localScale = value.scale;
    transform.eulerAngles = value.euler;
}

function sameTransform(a, b) {
    return a.position.equals(b.position) && a.scale.equals(b.scale) && a.euler.equals(b.euler);
}

function snapValue(value, increment) {
    return increment > 0 ? Math.round(value / increment) * increment : value;
}

function snapVector(value, increment) {
    return new Vector3(snapValue(value.x, increment), snapValue(value.y, increment), snapValue(value.z, increment));
}

function distanceToSegment(point, start, end) {
    const dx = end.x - start.x;
    const dy = end.y - start.y;
    const lengthSq = dx * dx + dy * dy;
    if (lengthSq < 1e-6) return Math.hypot(point.x - start.x, point.y - start.y);
    const t = Math.max(0, Math.min(1, ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSq));
    return Math.hypot(point.x - (start.x + dx * t), point.y - (start.y + dy * t));
}

/** The eight corners of an axis-aligned box. */
function boxCorners(bounds) {
    const min = bounds.min;
    const max = bounds.max;
    const corners = [];

    for (const x of [min.x, max.x]) {
        for (const y of [min.y, max.y]) {
            for (const z of [min.z, max.z]) corners.push(new Vector3(x, y, z));
        }
    }
    return corners;
}
