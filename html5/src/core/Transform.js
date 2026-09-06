// -----------------------------------------------------------------------------
// Transform — the 2D transform every Actor carries, at component index 0.
//
// Rotation is in radians, as in the C# engine. World values are computed lazily
// and cached; `markDirty` recurses into children but stops at a subtree that is
// already dirty, so a burst of edits in one frame costs one walk.
// -----------------------------------------------------------------------------

import { Component } from './Component.js';
import { Vector2, Matrix4 } from '../math/index.js';

/** Position, rotation and scale in 2D, with a parent/child hierarchy. */
export class Transform extends Component {
    // The transform is stored as flat fields on the actor in a scene file, not as
    // a component, so it declares no schema of its own.
    static schema = {};

    constructor() {
        super();
        this._localPosition = new Vector2(0, 0);
        this._localRotation = 0;
        this._localScale = new Vector2(1, 1);

        this._worldPosition = new Vector2(0, 0);
        this._worldRotation = 0;
        this._worldScale = new Vector2(1, 1);
        this._dirty = true;

        this._parent = null;
        this._children = [];
    }

    // ---- Local space ---------------------------------------------------------

    get localPosition() { return this._localPosition; }
    set localPosition(value) { this._localPosition = Vector2.from(value); this.markDirty(); }

    /** Rotation about the origin, in radians. */
    get localRotation() { return this._localRotation; }
    set localRotation(value) { this._localRotation = +value || 0; this.markDirty(); }

    get localScale() { return this._localScale; }
    set localScale(value) { this._localScale = Vector2.from(value); this.markDirty(); }

    // ---- World space ---------------------------------------------------------

    get position() { this._recalculate(); return this._worldPosition; }
    set position(value) {
        const world = Vector2.from(value);
        // The world point must be expressed in the PARENT's space to become a
        // local offset. Inverting through this transform would be circular: it
        // would use the very position being assigned.
        this._localPosition = this._parent ? this._parent.inverseTransformPoint(world) : world;
        this.markDirty();
    }

    get rotation() { this._recalculate(); return this._worldRotation; }
    set rotation(value) {
        this._localRotation = this._parent ? (+value || 0) - this._parent.rotation : (+value || 0);
        this.markDirty();
    }

    get scale() { this._recalculate(); return this._worldScale; }
    set scale(value) {
        const world = Vector2.from(value);
        if (!this._parent) {
            this._localScale = world;
        } else {
            const ps = this._parent.scale;
            this._localScale = (ps.x !== 0 && ps.y !== 0)
                ? new Vector2(world.x / ps.x, world.y / ps.y)
                : world;
        }
        this.markDirty();
    }

    // ---- Convenience accessors ----------------------------------------------
    // `transform.x` is how the script bridge and the editor address a position,
    // so it is a first-class accessor rather than something callers reach through
    // `position` for.

    get x() { return this.position.x; }
    set x(value) { this.position = new Vector2(value, this.position.y); }

    get y() { return this.position.y; }
    set y(value) { this.position = new Vector2(this.position.x, value); }

    get scaleX() { return this.scale.x; }
    set scaleX(value) { this.scale = new Vector2(value, this.scale.y); }

    get scaleY() { return this.scale.y; }
    set scaleY(value) { this.scale = new Vector2(this.scale.x, value); }

    /** The local +X axis in world space. */
    get right() { const r = this.rotation; return new Vector2(Math.cos(r), Math.sin(r)); }

    /** The local +Y axis in world space. Y grows downwards, as in screen space. */
    get up() { const r = this.rotation; return new Vector2(-Math.sin(r), Math.cos(r)); }

    // ---- Hierarchy -----------------------------------------------------------

    get parent() { return this._parent; }
    get children() { return this._children; }

    /**
     * Re-parents the transform.
     * @param {?Transform} newParent Null detaches to the scene root.
     * @param {boolean} [keepWorldPosition=true] Preserve the world pose across the change.
     */
    setParent(newParent, keepWorldPosition = true) {
        if (this._parent === newParent) return;

        const worldPos = keepWorldPosition ? this.position.clone() : this.localPosition.clone();
        const worldRot = keepWorldPosition ? this.rotation : this.localRotation;
        const worldScl = keepWorldPosition ? this.scale.clone() : this.localScale.clone();

        if (this._parent) {
            const i = this._parent._children.indexOf(this);
            if (i >= 0) this._parent._children.splice(i, 1);
        }

        this._parent = newParent ?? null;
        if (this._parent) this._parent._children.push(this);

        if (keepWorldPosition) {
            this.position = worldPos;
            this.rotation = worldRot;
            this.scale = worldScl;
        }

        this.markDirty();
    }

    // ---- Matrices ------------------------------------------------------------

    /** Scale, then rotate about Z, then translate. */
    getLocalMatrix() {
        return Matrix4.multiply(
            Matrix4.multiply(
                Matrix4.scale(this._localScale.x, this._localScale.y, 1),
                Matrix4.rotationZ(this._localRotation)),
            Matrix4.translation(this._localPosition.x, this._localPosition.y, 0));
    }

    getWorldMatrix() {
        this._recalculate();
        return Matrix4.multiply(
            Matrix4.multiply(
                Matrix4.scale(this._worldScale.x, this._worldScale.y, 1),
                Matrix4.rotationZ(this._worldRotation)),
            Matrix4.translation(this._worldPosition.x, this._worldPosition.y, 0));
    }

    // ---- Space conversion ----------------------------------------------------

    /** Takes a point from this transform's local space into world space. */
    transformPoint(localPoint) {
        const p = Vector2.from(localPoint);
        const rot = this.rotation;
        const cos = Math.cos(rot), sin = Math.sin(rot);
        const s = this.scale;
        const sx = p.x * s.x, sy = p.y * s.y;
        const origin = this.position;
        return new Vector2(
            origin.x + sx * cos - sy * sin,
            origin.y + sx * sin + sy * cos);
    }

    /** Takes a world point into this transform's local space. */
    inverseTransformPoint(worldPoint) {
        const p = Vector2.from(worldPoint);
        const origin = this.position;
        const dx = p.x - origin.x, dy = p.y - origin.y;
        const rot = -this.rotation;
        const cos = Math.cos(rot), sin = Math.sin(rot);
        const rx = dx * cos - dy * sin;
        const ry = dx * sin + dy * cos;
        const s = this.scale;
        return (s.x !== 0 && s.y !== 0) ? new Vector2(rx / s.x, ry / s.y) : new Vector2(rx, ry);
    }

    // ---- Utility -------------------------------------------------------------

    /** Rotates so the local +X axis points at `target`. */
    lookAt(target) {
        const t = Vector2.from(target);
        const origin = this.position;
        const dx = t.x - origin.x, dy = t.y - origin.y;
        if (dx * dx + dy * dy > 0) this.rotation = Math.atan2(dy, dx);
    }

    /** Distance from this transform's world position to another's. */
    distanceTo(other) {
        const a = this.position;
        const b = other?.position ?? Vector2.from(other);
        return Math.hypot(b.x - a.x, b.y - a.y);
    }

    /** Moves by a world-space delta. */
    translate(delta) {
        const d = Vector2.from(delta);
        this.position = Vector2.add(this.position, d);
    }

    // ---- Internals -----------------------------------------------------------

    _recalculate() {
        if (!this._dirty) return;
        this._dirty = false;

        if (!this._parent) {
            this._worldPosition = this._localPosition.clone();
            this._worldRotation = this._localRotation;
            this._worldScale = this._localScale.clone();
            return;
        }

        const pPos = this._parent.position;
        const pRot = this._parent.rotation;
        const pScl = this._parent.scale;

        this._worldScale = Vector2.multiply(pScl, this._localScale);
        this._worldRotation = pRot + this._localRotation;

        const cos = Math.cos(pRot), sin = Math.sin(pRot);
        const sx = this._localPosition.x * pScl.x;
        const sy = this._localPosition.y * pScl.y;
        this._worldPosition = new Vector2(
            pPos.x + sx * cos - sy * sin,
            pPos.y + sx * sin + sy * cos);
    }

    /**
     * Marks this transform and its subtree as needing recalculation.
     *
     * Marking only self is not enough: a descendant whose own flag was already
     * clear would return its cached world transform without consulting the
     * ancestor that moved. Recursion stops at a subtree that is already dirty.
     */
    markDirty() {
        if (this._dirty) return;
        this._dirty = true;
        for (const child of this._children) child.markDirty();
    }

    toString() {
        return `Transform(pos=${this._localPosition}, rot=${this._localRotation.toFixed(2)}, scale=${this._localScale})`;
    }
}
