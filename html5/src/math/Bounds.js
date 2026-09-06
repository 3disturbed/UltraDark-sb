// -----------------------------------------------------------------------------
// Rect and Bounds — the 2D and 3D axis-aligned volumes the engine tests against.
// -----------------------------------------------------------------------------

import { Vector2 } from './Vector2.js';
import { Vector3 } from './Vector3.js';

/** An axis-aligned 2D rectangle, positioned by its top-left corner. */
export class Rect {
    constructor(x = 0, y = 0, width = 0, height = 0) {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    get left()   { return this.x; }
    get right()  { return this.x + this.width; }
    get top()    { return this.y; }
    get bottom() { return this.y + this.height; }

    get center() { return new Vector2(this.x + this.width / 2, this.y + this.height / 2); }
    get size()   { return new Vector2(this.width, this.height); }

    clone() { return new Rect(this.x, this.y, this.width, this.height); }

    contains(point) {
        return point.x >= this.x && point.x <= this.right
            && point.y >= this.y && point.y <= this.bottom;
    }

    /** True when the two rectangles overlap. Touching edges count as overlapping. */
    intersects(other) {
        return this.x <= other.right && this.right >= other.x
            && this.y <= other.bottom && this.bottom >= other.y;
    }

    /** The overlapping region, or null when they do not overlap. */
    intersection(other) {
        const x = Math.max(this.x, other.x);
        const y = Math.max(this.y, other.y);
        const r = Math.min(this.right, other.right);
        const b = Math.min(this.bottom, other.bottom);
        if (r < x || b < y) return null;
        return new Rect(x, y, r - x, b - y);
    }

    /** The smallest rectangle containing both. */
    union(other) {
        const x = Math.min(this.x, other.x);
        const y = Math.min(this.y, other.y);
        return new Rect(x, y,
            Math.max(this.right, other.right) - x,
            Math.max(this.bottom, other.bottom) - y);
    }

    /** Grows the rectangle by `amount` on every side. */
    inflate(amount) {
        return new Rect(
            this.x - amount, this.y - amount,
            this.width + amount * 2, this.height + amount * 2);
    }

    /** A rectangle from its centre and full size, the form colliders use. */
    static fromCenter(center, size) {
        return new Rect(center.x - size.x / 2, center.y - size.y / 2, size.x, size.y);
    }

    toJSON() { return [this.x, this.y, this.width, this.height]; }
    toString() { return `Rect(${this.x}, ${this.y}, ${this.width}x${this.height})`; }
}

/** An axis-aligned 3D box, stored as centre and half-extents. */
export class Bounds {
    /**
     * @param {Vector3} [center] Centre of the box.
     * @param {Vector3} [size]   Full size on each axis, not half-extents.
     */
    constructor(center = new Vector3(), size = new Vector3()) {
        this.center = Vector3.from(center);
        this.size = Vector3.from(size);
    }

    get extents() { return Vector3.scale(this.size, 0.5); }
    get min() { return Vector3.subtract(this.center, this.extents); }
    get max() { return Vector3.add(this.center, this.extents); }

    clone() { return new Bounds(this.center.clone(), this.size.clone()); }

    contains(point) {
        const min = this.min, max = this.max;
        return point.x >= min.x && point.x <= max.x
            && point.y >= min.y && point.y <= max.y
            && point.z >= min.z && point.z <= max.z;
    }

    intersects(other) {
        const aMin = this.min, aMax = this.max;
        const bMin = other.min, bMax = other.max;
        return aMin.x <= bMax.x && aMax.x >= bMin.x
            && aMin.y <= bMax.y && aMax.y >= bMin.y
            && aMin.z <= bMax.z && aMax.z >= bMin.z;
    }

    /** Grows the box just enough to contain `point`. */
    encapsulate(point) {
        const min = this.min, max = this.max;
        const newMin = new Vector3(
            Math.min(min.x, point.x), Math.min(min.y, point.y), Math.min(min.z, point.z));
        const newMax = new Vector3(
            Math.max(max.x, point.x), Math.max(max.y, point.y), Math.max(max.z, point.z));
        return Bounds.fromMinMax(newMin, newMax);
    }

    /** The point inside the box nearest to `point`. */
    closestPoint(point) {
        const min = this.min, max = this.max;
        return new Vector3(
            Math.max(min.x, Math.min(point.x, max.x)),
            Math.max(min.y, Math.min(point.y, max.y)),
            Math.max(min.z, Math.min(point.z, max.z)));
    }

    /** The radius of the sphere that fully contains this box. */
    get boundingRadius() { return this.extents.length; }

    static fromMinMax(min, max) {
        const size = Vector3.subtract(max, min);
        return new Bounds(Vector3.add(min, Vector3.scale(size, 0.5)), size);
    }

    toString() { return `Bounds(center=${this.center}, size=${this.size})`; }
}
