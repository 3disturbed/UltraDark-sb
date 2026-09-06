// -----------------------------------------------------------------------------
// Vector4 — four-component vector, used for shader parameters and colour maths.
// -----------------------------------------------------------------------------

/** A four-component vector of single-precision floats. */
export class Vector4 {
    constructor(x = 0, y = 0, z = 0, w = 0) {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    static get zero() { return new Vector4(0, 0, 0, 0); }
    static get one()  { return new Vector4(1, 1, 1, 1); }

    set(x, y, z, w) { this.x = x; this.y = y; this.z = z; this.w = w; return this; }
    copy(v)  { this.x = v.x; this.y = v.y; this.z = v.z; this.w = v.w; return this; }
    clone()  { return new Vector4(this.x, this.y, this.z, this.w); }

    add(v)      { this.x += v.x; this.y += v.y; this.z += v.z; this.w += v.w; return this; }
    subtract(v) { this.x -= v.x; this.y -= v.y; this.z -= v.z; this.w -= v.w; return this; }
    scale(s)    { this.x *= s; this.y *= s; this.z *= s; this.w *= s; return this; }

    get length()        { return Math.hypot(this.x, this.y, this.z, this.w); }
    get lengthSquared() { return this.x ** 2 + this.y ** 2 + this.z ** 2 + this.w ** 2; }

    normalize() {
        const len = this.length;
        if (len > 1e-9) { this.x /= len; this.y /= len; this.z /= len; this.w /= len; }
        return this;
    }

    dot(v) { return this.x * v.x + this.y * v.y + this.z * v.z + this.w * v.w; }

    equals(v, epsilon = 1e-6) {
        return Math.abs(this.x - v.x) <= epsilon
            && Math.abs(this.y - v.y) <= epsilon
            && Math.abs(this.z - v.z) <= epsilon
            && Math.abs(this.w - v.w) <= epsilon;
    }

    toArray() { return [this.x, this.y, this.z, this.w]; }
    toJSON()  { return [this.x, this.y, this.z, this.w]; }
    toString() { return `(${this.x}, ${this.y}, ${this.z}, ${this.w})`; }

    static lerp(a, b, t) {
        return new Vector4(
            a.x + (b.x - a.x) * t,
            a.y + (b.y - a.y) * t,
            a.z + (b.z - a.z) * t,
            a.w + (b.w - a.w) * t);
    }

    /** Reads `[x, y, z, w]`, `{ x, y, z, w }`, `{ X, Y, Z, W }`, a scalar or null. */
    static from(value) {
        if (value == null) return new Vector4();
        if (typeof value === 'number') return new Vector4(value, value, value, value);
        if (Array.isArray(value)) {
            return new Vector4(+value[0] || 0, +value[1] || 0, +value[2] || 0, +value[3] || 0);
        }
        return new Vector4(
            +(value.x ?? value.X ?? 0) || 0,
            +(value.y ?? value.Y ?? 0) || 0,
            +(value.z ?? value.Z ?? 0) || 0,
            +(value.w ?? value.W ?? 0) || 0);
    }
}
