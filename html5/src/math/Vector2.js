// -----------------------------------------------------------------------------
// Vector2 — 2D vector, mirroring Microsoft.Xna.Framework.Vector2 as the C# engine
// uses it. Instances are mutable; every operator also has a static form that
// allocates, matching the C# call sites (`Vector2.Distance(a, b)`).
// -----------------------------------------------------------------------------

/** A two-component vector of single-precision floats. */
export class Vector2 {
    /**
     * @param {number} [x=0] Horizontal component.
     * @param {number} [y=0] Vertical component.
     */
    constructor(x = 0, y = 0) {
        this.x = x;
        this.y = y;
    }

    // ---- Named constants -----------------------------------------------------
    // Fresh instances, not shared singletons: these are mutable objects and a
    // shared `Vector2.Zero` would be corrupted by the first caller that wrote to
    // it. The C# originals are structs and copy on assignment; JS does not.

    static get zero()  { return new Vector2(0, 0); }
    static get one()   { return new Vector2(1, 1); }
    static get up()    { return new Vector2(0, -1); }
    static get down()  { return new Vector2(0, 1); }
    static get left()  { return new Vector2(-1, 0); }
    static get right() { return new Vector2(1, 0); }

    // ---- Instance operations -------------------------------------------------

    /** Sets both components in place and returns this. */
    set(x, y) { this.x = x; this.y = y; return this; }

    /** Copies another vector's components into this one. */
    copy(v) { this.x = v.x; this.y = v.y; return this; }

    /** Returns an independent copy. */
    clone() { return new Vector2(this.x, this.y); }

    add(v)      { this.x += v.x; this.y += v.y; return this; }
    subtract(v) { this.x -= v.x; this.y -= v.y; return this; }
    multiply(v) { this.x *= v.x; this.y *= v.y; return this; }
    divide(v)   { this.x /= v.x; this.y /= v.y; return this; }

    /** Multiplies both components by a scalar. */
    scale(s) { this.x *= s; this.y *= s; return this; }

    /** Negates both components. */
    negate() { this.x = -this.x; this.y = -this.y; return this; }

    get length() { return Math.hypot(this.x, this.y); }
    get lengthSquared() { return this.x * this.x + this.y * this.y; }

    /** Scales to unit length. A zero vector is left alone rather than producing NaN. */
    normalize() {
        const len = this.length;
        if (len > 1e-9) { this.x /= len; this.y /= len; }
        return this;
    }

    /** Returns a unit-length copy. */
    normalized() { return this.clone().normalize(); }

    dot(v) { return this.x * v.x + this.y * v.y; }

    /** The Z component of the 3D cross product — the signed area of the parallelogram. */
    cross(v) { return this.x * v.y - this.y * v.x; }

    distanceTo(v) { return Math.hypot(v.x - this.x, v.y - this.y); }
    distanceSquaredTo(v) {
        const dx = v.x - this.x, dy = v.y - this.y;
        return dx * dx + dy * dy;
    }

    /** Rotates around the origin by `radians`. */
    rotate(radians) {
        const c = Math.cos(radians), s = Math.sin(radians);
        const x = this.x * c - this.y * s;
        this.y = this.x * s + this.y * c;
        this.x = x;
        return this;
    }

    /** The angle from the positive X axis, in radians, in (-PI, PI]. */
    get angle() { return Math.atan2(this.y, this.x); }

    equals(v, epsilon = 1e-6) {
        return Math.abs(this.x - v.x) <= epsilon && Math.abs(this.y - v.y) <= epsilon;
    }

    /** Serialises as `[x, y]`, the shape `SceneSerializer` writes for a Vector2. */
    toArray() { return [this.x, this.y]; }
    toJSON()  { return [this.x, this.y]; }
    toString() { return `(${this.x}, ${this.y})`; }

    // ---- Static (allocating) forms ------------------------------------------

    static add(a, b)      { return new Vector2(a.x + b.x, a.y + b.y); }
    static subtract(a, b) { return new Vector2(a.x - b.x, a.y - b.y); }
    static multiply(a, b) { return new Vector2(a.x * b.x, a.y * b.y); }
    static scale(v, s)    { return new Vector2(v.x * s, v.y * s); }
    static negate(v)      { return new Vector2(-v.x, -v.y); }
    static dot(a, b)      { return a.x * b.x + a.y * b.y; }
    static cross(a, b)    { return a.x * b.y - a.y * b.x; }
    static distance(a, b) { return Math.hypot(b.x - a.x, b.y - a.y); }
    static distanceSquared(a, b) {
        const dx = b.x - a.x, dy = b.y - a.y;
        return dx * dx + dy * dy;
    }
    static normalize(v)   { return new Vector2(v.x, v.y).normalize(); }

    static lerp(a, b, t) {
        return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }

    /** Moves `from` towards `to` by at most `maxDelta` — the C# MoveTowards helper. */
    static moveTowards(from, to, maxDelta) {
        const dx = to.x - from.x, dy = to.y - from.y;
        const dist = Math.hypot(dx, dy);
        if (dist <= maxDelta || dist < 1e-9) return new Vector2(to.x, to.y);
        return new Vector2(from.x + dx / dist * maxDelta, from.y + dy / dist * maxDelta);
    }

    /** Reflects `v` about the (unit) `normal`. */
    static reflect(v, normal) {
        const d = 2 * (v.x * normal.x + v.y * normal.y);
        return new Vector2(v.x - d * normal.x, v.y - d * normal.y);
    }

    /** Unit vector at `radians` from the positive X axis. */
    static fromAngle(radians) { return new Vector2(Math.cos(radians), Math.sin(radians)); }

    /**
     * Reads the several shapes a Vector2 takes in scene files and tool arguments:
     * `[x, y]`, `{ x, y }`, `{ X, Y }`, a bare number (both components) or null.
     */
    static from(value) {
        if (value == null) return new Vector2();
        if (typeof value === 'number') return new Vector2(value, value);
        if (Array.isArray(value)) return new Vector2(+value[0] || 0, +value[1] || 0);
        const x = value.x ?? value.X ?? 0;
        const y = value.y ?? value.Y ?? 0;
        return new Vector2(+x || 0, +y || 0);
    }
}
