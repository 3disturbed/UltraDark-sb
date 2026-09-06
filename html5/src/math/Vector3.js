// -----------------------------------------------------------------------------
// Vector3 — 3D vector matching Microsoft.Xna.Framework.Vector3.
// The handedness matches MonoGame: forward is -Z, up is +Y, right is +X.
// -----------------------------------------------------------------------------

/** A three-component vector of single-precision floats. */
export class Vector3 {
    constructor(x = 0, y = 0, z = 0) {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    // ---- Named constants -----------------------------------------------------
    // Fresh instances rather than shared singletons; see the note on Vector2.

    static get zero()    { return new Vector3(0, 0, 0); }
    static get one()     { return new Vector3(1, 1, 1); }
    static get up()      { return new Vector3(0, 1, 0); }
    static get down()    { return new Vector3(0, -1, 0); }
    static get left()    { return new Vector3(-1, 0, 0); }
    static get right()   { return new Vector3(1, 0, 0); }
    /** MonoGame's forward is -Z. Keeping that convention keeps ported camera code correct. */
    static get forward() { return new Vector3(0, 0, -1); }
    static get backward(){ return new Vector3(0, 0, 1); }
    static get unitX()   { return new Vector3(1, 0, 0); }
    static get unitY()   { return new Vector3(0, 1, 0); }
    static get unitZ()   { return new Vector3(0, 0, 1); }

    // ---- Instance operations -------------------------------------------------

    set(x, y, z) { this.x = x; this.y = y; this.z = z; return this; }
    copy(v)      { this.x = v.x; this.y = v.y; this.z = v.z; return this; }
    clone()      { return new Vector3(this.x, this.y, this.z); }

    add(v)      { this.x += v.x; this.y += v.y; this.z += v.z; return this; }
    subtract(v) { this.x -= v.x; this.y -= v.y; this.z -= v.z; return this; }
    multiply(v) { this.x *= v.x; this.y *= v.y; this.z *= v.z; return this; }
    divide(v)   { this.x /= v.x; this.y /= v.y; this.z /= v.z; return this; }
    scale(s)    { this.x *= s; this.y *= s; this.z *= s; return this; }
    negate()    { this.x = -this.x; this.y = -this.y; this.z = -this.z; return this; }

    get length()        { return Math.hypot(this.x, this.y, this.z); }
    get lengthSquared() { return this.x * this.x + this.y * this.y + this.z * this.z; }

    normalize() {
        const len = this.length;
        if (len > 1e-9) { this.x /= len; this.y /= len; this.z /= len; }
        return this;
    }

    normalized() { return this.clone().normalize(); }

    dot(v) { return this.x * v.x + this.y * v.y + this.z * v.z; }

    /** Cross product, in place. */
    cross(v) {
        const x = this.y * v.z - this.z * v.y;
        const y = this.z * v.x - this.x * v.z;
        const z = this.x * v.y - this.y * v.x;
        this.x = x; this.y = y; this.z = z;
        return this;
    }

    distanceTo(v) { return Math.hypot(v.x - this.x, v.y - this.y, v.z - this.z); }
    distanceSquaredTo(v) {
        const dx = v.x - this.x, dy = v.y - this.y, dz = v.z - this.z;
        return dx * dx + dy * dy + dz * dz;
    }

    equals(v, epsilon = 1e-6) {
        return Math.abs(this.x - v.x) <= epsilon
            && Math.abs(this.y - v.y) <= epsilon
            && Math.abs(this.z - v.z) <= epsilon;
    }

    /** Serialises as `[x, y, z]`, the shape `SceneSerializer` writes for a Vector3. */
    toArray() { return [this.x, this.y, this.z]; }
    toJSON()  { return [this.x, this.y, this.z]; }
    toString() { return `(${this.x}, ${this.y}, ${this.z})`; }

    // ---- Static (allocating) forms ------------------------------------------

    static add(a, b)      { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
    static subtract(a, b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
    static multiply(a, b) { return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z); }
    static scale(v, s)    { return new Vector3(v.x * s, v.y * s, v.z * s); }
    static negate(v)      { return new Vector3(-v.x, -v.y, -v.z); }
    static dot(a, b)      { return a.x * b.x + a.y * b.y + a.z * b.z; }
    static normalize(v)   { return v.clone().normalize(); }

    static cross(a, b) {
        return new Vector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);
    }

    static distance(a, b) { return Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z); }
    static distanceSquared(a, b) {
        const dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
        return dx * dx + dy * dy + dz * dz;
    }

    static lerp(a, b, t) {
        return new Vector3(
            a.x + (b.x - a.x) * t,
            a.y + (b.y - a.y) * t,
            a.z + (b.z - a.z) * t);
    }

    static moveTowards(from, to, maxDelta) {
        const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
        const dist = Math.hypot(dx, dy, dz);
        if (dist <= maxDelta || dist < 1e-9) return to.clone();
        const k = maxDelta / dist;
        return new Vector3(from.x + dx * k, from.y + dy * k, from.z + dz * k);
    }

    static reflect(v, normal) {
        const d = 2 * (v.x * normal.x + v.y * normal.y + v.z * normal.z);
        return new Vector3(
            v.x - d * normal.x,
            v.y - d * normal.y,
            v.z - d * normal.z);
    }

    /** Rotates `v` by a quaternion — the equivalent of XNA's Vector3.Transform. */
    static transform(v, q) {
        // t = 2 * (q.xyz X v); v' = v + q.w * t + (q.xyz X t)
        const tx = 2 * (q.y * v.z - q.z * v.y);
        const ty = 2 * (q.z * v.x - q.x * v.z);
        const tz = 2 * (q.x * v.y - q.y * v.x);
        return new Vector3(
            v.x + q.w * tx + (q.y * tz - q.z * ty),
            v.y + q.w * ty + (q.z * tx - q.x * tz),
            v.z + q.w * tz + (q.x * ty - q.y * tx));
    }

    /** Reads `[x, y, z]`, `{ x, y, z }`, `{ X, Y, Z }`, a scalar or null. */
    static from(value) {
        if (value == null) return new Vector3();
        if (typeof value === 'number') return new Vector3(value, value, value);
        if (Array.isArray(value)) {
            return new Vector3(+value[0] || 0, +value[1] || 0, +value[2] || 0);
        }
        return new Vector3(
            +(value.x ?? value.X ?? 0) || 0,
            +(value.y ?? value.Y ?? 0) || 0,
            +(value.z ?? value.Z ?? 0) || 0);
    }
}
