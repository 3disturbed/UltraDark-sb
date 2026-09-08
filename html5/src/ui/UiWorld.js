// -----------------------------------------------------------------------------
// UiWorld — the geometry that puts a canvas on a plane in the world.
//
// The mirror of SexyBiscuit.Engine/UI/UiWorld.cs, function for function.
//
// Free of any rendering context on purpose: every function here is pure arithmetic
// over numbers a test can write down, which is what lets both engines read one
// fixture and prove they agree. Two implementations of one answer is exactly what
// a shared fixture cannot pin, and world UI is all trigonometry.
//
// One convention runs through it, taken from the unit quad the renderer already has
// (`PrimitiveMesh.Quad`): local +X is canvas right, local +Y is canvas *up*, and the
// plane's normal is local +Z. Canvas Y grows downwards and the quad's does not, so
// exactly one flip happens -- here, and nowhere else.
// -----------------------------------------------------------------------------

import { Vector3 } from '../math/Vector3.js';
import { Matrix4 } from '../math/Matrix4.js';
import { UiFacing } from './UiEnums.js';

const EPSILON = 1e-6;

/**
 * Builds the plane a canvas occupies.
 *
 * The billboard bases come out of the inverted view matrix rather than a look-at per
 * canvas: the camera's own right and up vectors are two rows of that matrix, so facing
 * the camera costs one inverse for the frame instead of a normalise per canvas.
 *
 * @returns {{origin: Vector3, right: Vector3, up: Vector3, normal: Vector3, size: {x: number, y: number}}}
 */
export function build(anchor, facing, planeRotation, view, cameraPosition, worldSize) {
    let right, up, normal;

    switch (facing) {
        case UiFacing.Plane: {
            const m = Matrix4.fromQuaternion(planeRotation).m;
            right  = new Vector3(m[0], m[1], m[2]);
            up     = new Vector3(m[4], m[5], m[6]);
            normal = new Vector3(m[8], m[9], m[10]);
            break;
        }

        case UiFacing.VerticalBillboard: {
            // Turn about the world's up axis only, so a label over a character's head
            // stays level when the camera rolls or looks down at it.
            up = Vector3.up;

            const toCamera = Vector3.subtract(Vector3.from(cameraPosition), Vector3.from(anchor));
            toCamera.y = 0;

            normal = normalise(toCamera, Vector3.backward);
            right  = normalise(Vector3.cross(up, normal), Vector3.right);
            break;
        }

        default: {
            const inv = Matrix4.invert(view).m;
            right  = new Vector3(inv[0], inv[1], inv[2]);
            up     = new Vector3(inv[4], inv[5], inv[6]);
            normal = new Vector3(inv[8], inv[9], inv[10]);
            break;
        }
    }

    return {
        origin: Vector3.from(anchor),
        right:  normalise(right,  Vector3.right),
        up:     normalise(up,     Vector3.up),
        normal: normalise(normal, Vector3.backward),
        size:   { x: worldSize.x, y: worldSize.y },
    };
}

/** The transform that puts the unit quad where this basis says. */
export function quadTransform(basis) {
    const rotation = new Matrix4([
        basis.right.x,  basis.right.y,  basis.right.z,  0,
        basis.up.x,     basis.up.y,     basis.up.z,     0,
        basis.normal.x, basis.normal.y, basis.normal.z, 0,
        0,              0,              0,              1,
    ]);

    return Matrix4.multiply(
        Matrix4.multiply(Matrix4.scale(basis.size.x, basis.size.y, 1), rotation),
        Matrix4.translation(basis.origin.x, basis.origin.y, basis.origin.z));
}

/**
 * Where a ray crosses this canvas, in canvas units, or null when it misses.
 *
 * A miss is a genuine null rather than a clamped edge point. The caller turns it into a
 * coordinate far outside the canvas, which every rectangle test in the layout then rejects
 * on its own -- that is why hit-testing a canvas on a wall needs no new code in the input
 * router at all.
 */
export function rayToCanvas(basis, rayOrigin, rayDirection, canvasSize) {
    const facing = Vector3.dot(rayDirection, basis.normal);

    // Parallel to the plane, or so nearly so that the intersection is meaningless.
    if (Math.abs(facing) < EPSILON) return null;

    const distance = Vector3.dot(Vector3.subtract(basis.origin, rayOrigin), basis.normal) / facing;
    if (distance < 0) return null;      // The plane is behind the pointer.

    if (basis.size.x <= 0 || basis.size.y <= 0) return null;

    const local = Vector3.subtract(
        Vector3.add(Vector3.from(rayOrigin), Vector3.scale(rayDirection, distance)), basis.origin);

    const x = Vector3.dot(local, basis.right);
    const y = Vector3.dot(local, basis.up);

    return {
        x: (x / basis.size.x + 0.5) * canvasSize.x,
        y: (0.5 - y / basis.size.y) * canvasSize.y,
    };
}

/**
 * How far along a ray this canvas's plane is, or null when the ray misses it.
 *
 * What the host sorts by when several world canvases overlap on screen, so the one in front
 * takes the click and the ones behind it are told the pointer went elsewhere.
 */
export function rayDistance(basis, rayOrigin, rayDirection, canvasSize) {
    const point = rayToCanvas(basis, rayOrigin, rayDirection, canvasSize);
    if (!point) return null;
    if (point.x < 0 || point.y < 0 || point.x > canvasSize.x || point.y > canvasSize.y) return null;

    const facing = Vector3.dot(rayDirection, basis.normal);
    if (Math.abs(facing) < EPSILON) return null;

    return Vector3.dot(Vector3.subtract(basis.origin, rayOrigin), basis.normal) / facing;
}

/** A point in canvas units placed back into world space. */
export function canvasToWorld(basis, canvasPoint, canvasSize) {
    const u = canvasSize.x > 0 ? canvasPoint.x / canvasSize.x : 0;
    const v = canvasSize.y > 0 ? canvasPoint.y / canvasSize.y : 0;

    return Vector3.add(
        Vector3.add(basis.origin, Vector3.scale(basis.right, (u - 0.5) * basis.size.x)),
        Vector3.scale(basis.up, (0.5 - v) * basis.size.y));
}

function normalise(v, fallback) {
    return v.lengthSquared > 1e-12 ? Vector3.normalize(v) : fallback;
}
