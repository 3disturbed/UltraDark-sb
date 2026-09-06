// -----------------------------------------------------------------------------
// Shapes2D — collision geometry and the narrow phase.
//
// The C# engine wraps Aether.Physics2D, a Box2D port. There is no equivalent
// dependency here worth carrying — a whole Box2D build to move rectangles around
// is a lot of bytes for a phone to download — so this is a compact impulse
// solver over circles and convex polygons, with the same component API on top.
//
// Contacts are generated with the separating-axis theorem, which handles rotated
// boxes and arbitrary convex polygons uniformly.
// -----------------------------------------------------------------------------

import { Vector2 } from '../math/index.js';

/** A circle in world space. */
export class Circle {
    constructor(center, radius) {
        this.center = center;
        this.radius = radius;
    }

    /** The axis-aligned box that contains it. */
    get aabb() {
        return {
            minX: this.center.x - this.radius, minY: this.center.y - this.radius,
            maxX: this.center.x + this.radius, maxY: this.center.y + this.radius,
        };
    }
}

/** A convex polygon in world space, vertices in order. */
export class Polygon {
    constructor(vertices) {
        this.vertices = vertices;
    }

    get aabb() {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const v of this.vertices) {
            if (v.x < minX) minX = v.x;
            if (v.y < minY) minY = v.y;
            if (v.x > maxX) maxX = v.x;
            if (v.y > maxY) maxY = v.y;
        }
        return { minX, minY, maxX, maxY };
    }

    /** The outward normal of each edge. */
    get normals() {
        const out = [];
        const n = this.vertices.length;
        for (let i = 0; i < n; i++) {
            const a = this.vertices[i];
            const b = this.vertices[(i + 1) % n];
            // Winding is clockwise in screen space, where Y grows downwards, so
            // (dy, -dx) points outwards.
            out.push(new Vector2(b.y - a.y, -(b.x - a.x)).normalize());
        }
        return out;
    }

    /** A box centred at `center`, `size` across, rotated by `rotation` radians. */
    static box(center, size, rotation = 0) {
        const hw = size.x / 2, hh = size.y / 2;
        const corners = [[-hw, -hh], [hw, -hh], [hw, hh], [-hw, hh]];

        if (rotation === 0) {
            return new Polygon(corners.map(([x, y]) => new Vector2(center.x + x, center.y + y)));
        }

        const cos = Math.cos(rotation), sin = Math.sin(rotation);
        return new Polygon(corners.map(([x, y]) => new Vector2(
            center.x + x * cos - y * sin,
            center.y + x * sin + y * cos)));
    }
}

/** True when two axis-aligned boxes overlap. The broad phase's only test. */
export function aabbOverlap(a, b) {
    return a.minX <= b.maxX && a.maxX >= b.minX && a.minY <= b.maxY && a.maxY >= b.minY;
}

/**
 * A contact between two shapes.
 * `normal` points from A towards B; `depth` is how far they overlap along it.
 */
export class Manifold {
    constructor(normal, depth, contactPoint) {
        this.normal = normal;
        this.depth = depth;
        this.contactPoint = contactPoint;
    }
}

/** Tests any two shapes, dispatching on their types. Returns null when apart. */
export function collide(a, b) {
    const aIsCircle = a instanceof Circle;
    const bIsCircle = b instanceof Circle;

    if (aIsCircle && bIsCircle) return circleVsCircle(a, b);
    if (aIsCircle) {
        const m = polygonVsCircle(b, a);
        // The test is written polygon-first; flipping the normal restores the
        // "from A towards B" contract.
        return m ? new Manifold(m.normal.negate(), m.depth, m.contactPoint) : null;
    }
    if (bIsCircle) return polygonVsCircle(a, b);
    return polygonVsPolygon(a, b);
}

function circleVsCircle(a, b) {
    const dx = b.center.x - a.center.x;
    const dy = b.center.y - a.center.y;
    const distanceSq = dx * dx + dy * dy;
    const radii = a.radius + b.radius;

    if (distanceSq >= radii * radii) return null;

    const distance = Math.sqrt(distanceSq);
    // Concentric circles have no meaningful normal; pick one so the solver can
    // still push them apart rather than dividing by zero.
    const normal = distance > 1e-9
        ? new Vector2(dx / distance, dy / distance)
        : new Vector2(0, -1);

    const contact = new Vector2(
        a.center.x + normal.x * a.radius,
        a.center.y + normal.y * a.radius);

    return new Manifold(normal, radii - distance, contact);
}

function polygonVsCircle(polygon, circle) {
    const vertices = polygon.vertices;
    const n = vertices.length;

    let closest = null;
    let closestDistanceSq = Infinity;
    let insideAll = true;

    // Nearest point on the polygon's boundary, plus a check for the circle centre
    // being inside — the two cases need opposite normal directions.
    for (let i = 0; i < n; i++) {
        const a = vertices[i];
        const b = vertices[(i + 1) % n];
        const point = closestPointOnSegment(a, b, circle.center);

        const dx = circle.center.x - point.x;
        const dy = circle.center.y - point.y;
        const distanceSq = dx * dx + dy * dy;

        if (distanceSq < closestDistanceSq) {
            closestDistanceSq = distanceSq;
            closest = point;
        }

        const edgeX = b.x - a.x, edgeY = b.y - a.y;
        const toCentreX = circle.center.x - a.x, toCentreY = circle.center.y - a.y;
        if (edgeX * toCentreY - edgeY * toCentreX < 0) insideAll = false;
    }

    if (!insideAll && closestDistanceSq > circle.radius * circle.radius) return null;

    const distance = Math.sqrt(closestDistanceSq);
    let normal;
    let depth;

    if (insideAll) {
        // Centre inside: push out along the shortest way to the boundary.
        normal = distance > 1e-9
            ? new Vector2((closest.x - circle.center.x) / distance, (closest.y - circle.center.y) / distance)
            : new Vector2(0, -1);
        depth = circle.radius + distance;
    } else {
        normal = distance > 1e-9
            ? new Vector2((circle.center.x - closest.x) / distance, (circle.center.y - closest.y) / distance)
            : new Vector2(0, -1);
        depth = circle.radius - distance;
    }

    return new Manifold(normal, depth, closest);
}

function polygonVsPolygon(a, b) {
    let smallestDepth = Infinity;
    let smallestAxis = null;

    for (const axes of [a.normals, b.normals]) {
        for (const axis of axes) {
            const projectionA = project(a.vertices, axis);
            const projectionB = project(b.vertices, axis);

            // A gap on any axis means no collision at all — that is the theorem.
            if (projectionA.max < projectionB.min || projectionB.max < projectionA.min) return null;

            const depth = Math.min(projectionA.max - projectionB.min, projectionB.max - projectionA.min);
            if (depth < smallestDepth) {
                smallestDepth = depth;
                smallestAxis = axis;
            }
        }
    }

    if (!smallestAxis) return null;

    // The axis came from one shape's edge and may face either way; orient it
    // from A towards B so the solver pushes them apart rather than together.
    const centerA = centroid(a.vertices);
    const centerB = centroid(b.vertices);
    const toB = new Vector2(centerB.x - centerA.x, centerB.y - centerA.y);
    const normal = Vector2.dot(smallestAxis, toB) < 0 ? smallestAxis.clone().negate() : smallestAxis;

    return new Manifold(normal, smallestDepth, supportPoint(a.vertices, normal));
}

function project(vertices, axis) {
    let min = Infinity, max = -Infinity;
    for (const v of vertices) {
        const d = v.x * axis.x + v.y * axis.y;
        if (d < min) min = d;
        if (d > max) max = d;
    }
    return { min, max };
}

function centroid(vertices) {
    let x = 0, y = 0;
    for (const v of vertices) { x += v.x; y += v.y; }
    return new Vector2(x / vertices.length, y / vertices.length);
}

/** The vertex furthest along an axis — a serviceable single contact point. */
function supportPoint(vertices, axis) {
    let best = vertices[0];
    let bestDistance = -Infinity;
    for (const v of vertices) {
        const d = v.x * axis.x + v.y * axis.y;
        if (d > bestDistance) { bestDistance = d; best = v; }
    }
    return best.clone();
}

function closestPointOnSegment(a, b, point) {
    const abx = b.x - a.x, aby = b.y - a.y;
    const lengthSq = abx * abx + aby * aby;
    if (lengthSq < 1e-12) return a.clone();

    let t = ((point.x - a.x) * abx + (point.y - a.y) * aby) / lengthSq;
    t = t < 0 ? 0 : (t > 1 ? 1 : t);
    return new Vector2(a.x + abx * t, a.y + aby * t);
}

/**
 * Ray against a shape.
 * @returns {?{distance: number, point: Vector2, normal: Vector2}}
 */
export function raycastShape(origin, direction, maxDistance, shape) {
    return shape instanceof Circle
        ? raycastCircle(origin, direction, maxDistance, shape)
        : raycastPolygon(origin, direction, maxDistance, shape);
}

function raycastCircle(origin, direction, maxDistance, circle) {
    const ox = origin.x - circle.center.x;
    const oy = origin.y - circle.center.y;

    const b = ox * direction.x + oy * direction.y;
    const c = ox * ox + oy * oy - circle.radius * circle.radius;

    // Pointing away from a circle we are already outside of.
    if (c > 0 && b > 0) return null;

    const discriminant = b * b - c;
    if (discriminant < 0) return null;

    let distance = -b - Math.sqrt(discriminant);
    if (distance < 0) distance = 0;          // origin inside the circle
    if (distance > maxDistance) return null;

    const point = new Vector2(origin.x + direction.x * distance, origin.y + direction.y * distance);
    const normal = new Vector2(point.x - circle.center.x, point.y - circle.center.y).normalize();
    return { distance, point, normal };
}

function raycastPolygon(origin, direction, maxDistance, polygon) {
    const vertices = polygon.vertices;
    const n = vertices.length;
    let nearest = null;

    for (let i = 0; i < n; i++) {
        const a = vertices[i];
        const b = vertices[(i + 1) % n];

        const edgeX = b.x - a.x, edgeY = b.y - a.y;
        const denominator = direction.x * edgeY - direction.y * edgeX;
        if (Math.abs(denominator) < 1e-9) continue;     // parallel

        const dx = a.x - origin.x, dy = a.y - origin.y;
        const t = (dx * edgeY - dy * edgeX) / denominator;   // along the ray
        const u = (dx * direction.y - dy * direction.x) / denominator;  // along the edge

        if (t < 0 || t > maxDistance || u < 0 || u > 1) continue;
        if (nearest && t >= nearest.distance) continue;

        const normal = new Vector2(edgeY, -edgeX).normalize();
        nearest = {
            distance: t,
            point: new Vector2(origin.x + direction.x * t, origin.y + direction.y * t),
            // Face the normal back towards where the ray came from.
            normal: Vector2.dot(normal, direction) > 0 ? normal.negate() : normal,
        };
    }

    return nearest;
}
