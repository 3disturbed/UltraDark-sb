// -----------------------------------------------------------------------------
// Transform3D — position, rotation and scale in 3D.
//
// Not added automatically: adding one is how a 2D actor becomes a 3D one, which
// is the same rule the C# engine follows and why a scene file only carries
// `position3`/`rotation3`/`scale3` for actors that have it.
//
// Euler angles are degrees in pitch, yaw, roll order, and the conversion routes
// through the same yaw-pitch-roll construction the C# `Transform3D` uses, so a
// rotation authored in either engine reads identically in the other.
// -----------------------------------------------------------------------------

import { Component } from './Component.js';
import { Vector3, Quaternion, Matrix4 } from '../math/index.js';

/** Position, rotation and scale in 3D, with a parent/child hierarchy. */
export class Transform3D extends Component {
    static schema = {};

    constructor() {
        super();
        this._localPosition = new Vector3(0, 0, 0);
        this._localRotation = Quaternion.identity;
        this._localScale = new Vector3(1, 1, 1);

        this._worldPosition = new Vector3(0, 0, 0);
        this._worldRotation = Quaternion.identity;
        this._worldScale = new Vector3(1, 1, 1);
        this._dirty = true;

        this._parent = null;
        this._children = [];
    }

    // ---- Local space ---------------------------------------------------------

    get localPosition() { return this._localPosition; }
    set localPosition(value) { this._localPosition = Vector3.from(value); this.markDirty(); }

    get localRotation() { return this._localRotation; }
    set localRotation(value) { this._localRotation = Quaternion.from(value); this.markDirty(); }

    get localScale() { return this._localScale; }
    set localScale(value) { this._localScale = Vector3.from(value); this.markDirty(); }

    /** Local rotation as pitch, yaw and roll in degrees. */
    get localEulerAngles() { return Quaternion.toEuler(this._localRotation); }
    set localEulerAngles(value) { this._localRotation = Quaternion.fromEuler(value); this.markDirty(); }

    // ---- World space ---------------------------------------------------------

    get position() { this._recalculate(); return this._worldPosition; }
    set position(value) {
        const world = Vector3.from(value);
        this._localPosition = this._parent ? this._parent.inverseTransformPoint(world) : world;
        this.markDirty();
    }

    get rotation() { this._recalculate(); return this._worldRotation; }
    set rotation(value) {
        const world = Quaternion.from(value);
        this._localRotation = this._parent
            ? Quaternion.multiply(Quaternion.inverse(this._parent.rotation), world)
            : world;
        this.markDirty();
    }

    get scale() { this._recalculate(); return this._worldScale; }
    set scale(value) {
        const world = Vector3.from(value);
        if (!this._parent) {
            this._localScale = world;
        } else {
            const ps = this._parent.scale;
            this._localScale = (ps.x !== 0 && ps.y !== 0 && ps.z !== 0)
                ? new Vector3(world.x / ps.x, world.y / ps.y, world.z / ps.z)
                : world;
        }
        this.markDirty();
    }

    /** World rotation as pitch, yaw and roll in degrees. */
    get eulerAngles() { return Quaternion.toEuler(this.rotation); }
    set eulerAngles(value) { this.rotation = Quaternion.fromEuler(value); }

    // ---- Convenience accessors ----------------------------------------------

    get x() { return this.position.x; }
    set x(v) { const p = this.position; this.position = new Vector3(v, p.y, p.z); }

    get y() { return this.position.y; }
    set y(v) { const p = this.position; this.position = new Vector3(p.x, v, p.z); }

    get z() { return this.position.z; }
    set z(v) { const p = this.position; this.position = new Vector3(p.x, p.y, v); }

    // ---- Direction vectors ---------------------------------------------------
    // Forward is -Z, matching MonoGame, so ported camera code stays correct.

    get forward() { return Vector3.transform(Vector3.forward, this.rotation); }
    get right()   { return Vector3.transform(Vector3.right, this.rotation); }
    get up()      { return Vector3.transform(Vector3.up, this.rotation); }

    // ---- Hierarchy -----------------------------------------------------------

    get parent() { return this._parent; }
    get children() { return this._children; }

    setParent(newParent, keepWorldTransform = true) {
        if (this._parent === newParent) return;

        const wp = keepWorldTransform ? this.position.clone() : this.localPosition.clone();
        const wr = keepWorldTransform ? this.rotation.clone() : this.localRotation.clone();
        const ws = keepWorldTransform ? this.scale.clone() : this.localScale.clone();

        if (this._parent) {
            const i = this._parent._children.indexOf(this);
            if (i >= 0) this._parent._children.splice(i, 1);
        }

        this._parent = newParent ?? null;
        if (this._parent) this._parent._children.push(this);

        if (keepWorldTransform) {
            this.position = wp;
            this.rotation = wr;
            this.scale = ws;
        }

        this.markDirty();
    }

    // ---- Matrices ------------------------------------------------------------

    getLocalMatrix() {
        return Matrix4.compose(this._localPosition, this._localRotation, this._localScale);
    }

    getWorldMatrix() {
        this._recalculate();
        return Matrix4.compose(this._worldPosition, this._worldRotation, this._worldScale);
    }

    // ---- Space conversion ----------------------------------------------------

    transformPoint(localPoint) {
        const p = Vector3.from(localPoint);
        const scaled = Vector3.multiply(p, this.scale);
        return Vector3.add(Vector3.transform(scaled, this.rotation), this.position);
    }

    inverseTransformPoint(worldPoint) {
        const p = Vector3.subtract(Vector3.from(worldPoint), this.position);
        const unrotated = Vector3.transform(p, Quaternion.inverse(this.rotation));
        const s = this.scale;
        return (s.x !== 0 && s.y !== 0 && s.z !== 0)
            ? new Vector3(unrotated.x / s.x, unrotated.y / s.y, unrotated.z / s.z)
            : unrotated;
    }

    /** Rotates a direction into world space, ignoring position and scale. */
    transformDirection(localDirection) {
        return Vector3.transform(Vector3.from(localDirection), this.rotation);
    }

    // ---- Utility -------------------------------------------------------------

    /** Rotates so the local forward axis points at `target`. */
    lookAt(target, up = Vector3.up) {
        const dir = Vector3.subtract(Vector3.from(target), this.position);
        if (dir.lengthSquared < 1e-12) return;
        this.rotation = Quaternion.lookRotation(dir.normalize(), Vector3.from(up));
    }

    /** Moves by a world-space delta. */
    translate(delta) {
        this.position = Vector3.add(this.position, Vector3.from(delta));
    }

    distanceTo(other) {
        const b = other?.position ?? Vector3.from(other);
        return Vector3.distance(this.position, b);
    }

    // ---- Internals -----------------------------------------------------------

    _recalculate() {
        if (!this._dirty) return;
        this._dirty = false;

        if (!this._parent) {
            this._worldPosition = this._localPosition.clone();
            this._worldRotation = this._localRotation.clone();
            this._worldScale = this._localScale.clone();
            return;
        }

        const pPos = this._parent.position;
        const pRot = this._parent.rotation;
        const pScl = this._parent.scale;

        this._worldScale = Vector3.multiply(pScl, this._localScale);
        this._worldRotation = Quaternion.multiply(pRot, this._localRotation);
        this._worldPosition = Vector3.add(
            Vector3.transform(Vector3.multiply(this._localPosition, pScl), pRot),
            pPos);
    }

    markDirty() {
        if (this._dirty) return;
        this._dirty = true;
        for (const child of this._children) child.markDirty();
    }

    /** Builds a rotation from pitch, yaw and roll in degrees. */
    static eulerToQuaternion(eulerDegrees) { return Quaternion.fromEuler(eulerDegrees); }

    /** The inverse: pitch, yaw and roll in degrees. */
    static quaternionToEuler(q) { return Quaternion.toEuler(q); }

    toString() {
        return `Transform3D(pos=${this._localPosition}, euler=${this.localEulerAngles})`;
    }
}
