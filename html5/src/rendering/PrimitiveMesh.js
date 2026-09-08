// -----------------------------------------------------------------------------
// PrimitiveMesh — procedural geometry, mirroring Rendering/PrimitiveMesh.cs.
//
// Each builder returns interleaved position/normal/uv data plus indices, ready
// to upload once and share between every renderer using that shape. The C#
// version caches VertexBuffers per primitive; this caches the CPU-side arrays
// and lets the renderer own the GL buffers.
// -----------------------------------------------------------------------------

import { Vector3, Bounds } from '../math/index.js';

/** The built-in shapes. Matches the C# `MeshPrimitive` enum. */
export const MeshPrimitive = Object.freeze({
    None: 'None',
    Cube: 'Cube',
    Sphere: 'Sphere',
    Plane: 'Plane',
    Cylinder: 'Cylinder',
    Capsule: 'Capsule',
    Cone: 'Cone',
    Torus: 'Torus',
    Quad: 'Quad',
});

/** Vertex and index data for one shape. */
export class Geometry {
    /**
     * @param {Float32Array} vertices Interleaved position(3), normal(3), uv(2).
     * @param {Uint16Array|Uint32Array} indices
     * @param {Bounds} bounds
     */
    constructor(vertices, indices, bounds) {
        this.vertices = vertices;
        this.indices = indices;
        this.bounds = bounds;
        this.triangleCount = indices.length / 3;

        /** @type {?object} GL buffers, attached by the renderer on first draw. */
        this.gpu = null;
    }

    /** Floats per vertex: 3 position, 3 normal, 2 texture coordinate. */
    static get stride() { return 8; }
}

/** How many segments a curved surface is divided into around its axis. */
export let radialSegments = 24;
/** How many segments a curved surface is divided into along its axis. */
export let ringSegments = 16;

const _cache = new Map();

/** The geometry for a primitive, built once and reused. */
export function getPrimitive(type) {
    if (type === MeshPrimitive.None) return null;

    const cached = _cache.get(type);
    if (cached) return cached;

    const built = build(type);
    if (built) _cache.set(type, built);
    return built;
}

/** The local bounds of a primitive, without building its geometry. */
export function getPrimitiveBounds(type) {
    switch (type) {
        case MeshPrimitive.Plane:
            return new Bounds(new Vector3(0, 0, 0), new Vector3(1, 0.001, 1));
        // A quad stands up in XY, so its thin axis is Z. Saying otherwise gave every
        // billboard a bounding box lying flat on the floor, which culls it from the side.
        case MeshPrimitive.Quad:
            return new Bounds(new Vector3(0, 0, 0), new Vector3(1, 1, 0.001));
        case MeshPrimitive.Sphere:
        case MeshPrimitive.Cube:
        case MeshPrimitive.Cylinder:
        case MeshPrimitive.Cone:
            return new Bounds(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
        case MeshPrimitive.Capsule:
            return new Bounds(new Vector3(0, 0, 0), new Vector3(1, 2, 1));
        case MeshPrimitive.Torus:
            return new Bounds(new Vector3(0, 0, 0), new Vector3(2, 0.5, 2));
        default:
            return new Bounds(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
    }
}

/** Discards every cached shape. Call after changing the segment counts. */
export function clearPrimitiveCache() { _cache.clear(); }

/** Sets the tessellation used by curved primitives built from now on. */
export function setTessellation(radial, rings) {
    radialSegments = Math.max(3, radial);
    ringSegments = Math.max(2, rings);
    clearPrimitiveCache();
}

function build(type) {
    switch (type) {
        case MeshPrimitive.Cube:     return buildCube();
        case MeshPrimitive.Sphere:   return buildSphere();
        case MeshPrimitive.Plane:    return buildPlane();
        case MeshPrimitive.Quad:     return buildQuad();
        case MeshPrimitive.Cylinder: return buildCylinder(0.5, 0.5);
        case MeshPrimitive.Cone:     return buildCylinder(0.5, 0);
        case MeshPrimitive.Capsule:  return buildCapsule();
        case MeshPrimitive.Torus:    return buildTorus();
        default:                     return buildCube();
    }
}

// -----------------------------------------------------------------------------
// Builders
// -----------------------------------------------------------------------------

function buildCube() {
    const h = 0.5;
    // Each face gets its own four vertices: sharing corners would average the
    // normals and round off the edges.
    const faces = [
        { normal: [0, 0, 1],  corners: [[-h, -h, h], [h, -h, h], [h, h, h], [-h, h, h]] },
        { normal: [0, 0, -1], corners: [[h, -h, -h], [-h, -h, -h], [-h, h, -h], [h, h, -h]] },
        { normal: [1, 0, 0],  corners: [[h, -h, h], [h, -h, -h], [h, h, -h], [h, h, h]] },
        { normal: [-1, 0, 0], corners: [[-h, -h, -h], [-h, -h, h], [-h, h, h], [-h, h, -h]] },
        { normal: [0, 1, 0],  corners: [[-h, h, h], [h, h, h], [h, h, -h], [-h, h, -h]] },
        { normal: [0, -1, 0], corners: [[-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h]] },
    ];

    const vertices = [];
    const indices = [];
    const uvs = [[0, 0], [1, 0], [1, 1], [0, 1]];

    for (const face of faces) {
        const base = vertices.length / Geometry.stride;
        face.corners.forEach((corner, i) => {
            vertices.push(...corner, ...face.normal, ...uvs[i]);
        });
        indices.push(base, base + 1, base + 2, base, base + 2, base + 3);
    }

    return new Geometry(
        new Float32Array(vertices), new Uint16Array(indices),
        new Bounds(new Vector3(0, 0, 0), new Vector3(1, 1, 1)));
}

function buildSphere(radius = 0.5) {
    const vertices = [];
    const indices = [];

    for (let ring = 0; ring <= ringSegments; ring++) {
        const phi = (ring / ringSegments) * Math.PI;
        const y = Math.cos(phi);
        const ringRadius = Math.sin(phi);

        for (let segment = 0; segment <= radialSegments; segment++) {
            const theta = (segment / radialSegments) * Math.PI * 2;
            const nx = ringRadius * Math.cos(theta);
            const nz = ringRadius * Math.sin(theta);

            vertices.push(
                nx * radius, y * radius, nz * radius,
                nx, y, nz,
                segment / radialSegments, ring / ringSegments);
        }
    }

    const stride = radialSegments + 1;
    for (let ring = 0; ring < ringSegments; ring++) {
        for (let segment = 0; segment < radialSegments; segment++) {
            const a = ring * stride + segment;
            const b = a + stride;
            indices.push(a, b, a + 1, a + 1, b, b + 1);
        }
    }

    return new Geometry(
        new Float32Array(vertices), new Uint16Array(indices),
        new Bounds(new Vector3(0, 0, 0), new Vector3(radius * 2, radius * 2, radius * 2)));
}

function buildPlane(size = 1, subdivisions = 1) {
    const vertices = [];
    const indices = [];
    const half = size / 2;

    for (let z = 0; z <= subdivisions; z++) {
        for (let x = 0; x <= subdivisions; x++) {
            const u = x / subdivisions;
            const v = z / subdivisions;
            vertices.push(-half + u * size, 0, -half + v * size, 0, 1, 0, u, v);
        }
    }

    const stride = subdivisions + 1;
    for (let z = 0; z < subdivisions; z++) {
        for (let x = 0; x < subdivisions; x++) {
            const a = z * stride + x;
            const b = a + stride;
            indices.push(a, b, a + 1, a + 1, b, b + 1);
        }
    }

    return new Geometry(
        new Float32Array(vertices), new Uint16Array(indices),
        new Bounds(new Vector3(0, 0, 0), new Vector3(size, 0.001, size)));
}

function buildQuad() {
    // An upright plane, for billboards and sprites in 3D.
    const h = 0.5;
    const vertices = new Float32Array([
        -h, -h, 0, 0, 0, 1, 0, 1,
         h, -h, 0, 0, 0, 1, 1, 1,
         h,  h, 0, 0, 0, 1, 1, 0,
        -h,  h, 0, 0, 0, 1, 0, 0,
    ]);
    return new Geometry(
        vertices, new Uint16Array([0, 1, 2, 0, 2, 3]),
        new Bounds(new Vector3(0, 0, 0), new Vector3(1, 1, 0.001)));
}

/** A cylinder, or a cone when `topRadius` is zero. */
function buildCylinder(bottomRadius = 0.5, topRadius = 0.5, height = 1) {
    const vertices = [];
    const indices = [];
    const halfHeight = height / 2;

    // Side wall.
    for (let segment = 0; segment <= radialSegments; segment++) {
        const theta = (segment / radialSegments) * Math.PI * 2;
        const cos = Math.cos(theta), sin = Math.sin(theta);
        const u = segment / radialSegments;

        // The normal follows the slope, so a cone shades as a cone rather than
        // as a cylinder that happens to taper.
        const slope = (bottomRadius - topRadius) / height;
        const normal = new Vector3(cos, slope, sin).normalize();

        vertices.push(cos * bottomRadius, -halfHeight, sin * bottomRadius, normal.x, normal.y, normal.z, u, 1);
        vertices.push(cos * topRadius, halfHeight, sin * topRadius, normal.x, normal.y, normal.z, u, 0);
    }

    for (let segment = 0; segment < radialSegments; segment++) {
        const a = segment * 2;
        indices.push(a, a + 1, a + 2, a + 1, a + 3, a + 2);
    }

    // Caps, each with its own centre vertex so the flat normal is not blended
    // into the wall's.
    const addCap = (y, radius, normalY) => {
        if (radius <= 0) return;
        const centre = vertices.length / Geometry.stride;
        vertices.push(0, y, 0, 0, normalY, 0, 0.5, 0.5);

        for (let segment = 0; segment <= radialSegments; segment++) {
            const theta = (segment / radialSegments) * Math.PI * 2;
            const cos = Math.cos(theta), sin = Math.sin(theta);
            vertices.push(cos * radius, y, sin * radius, 0, normalY, 0, cos * 0.5 + 0.5, sin * 0.5 + 0.5);
        }

        for (let segment = 0; segment < radialSegments; segment++) {
            const a = centre + 1 + segment;
            if (normalY > 0) indices.push(centre, a, a + 1);
            else indices.push(centre, a + 1, a);
        }
    };

    addCap(halfHeight, topRadius, 1);
    addCap(-halfHeight, bottomRadius, -1);

    const maxRadius = Math.max(bottomRadius, topRadius);
    return new Geometry(
        new Float32Array(vertices), new Uint16Array(indices),
        new Bounds(new Vector3(0, 0, 0), new Vector3(maxRadius * 2, height, maxRadius * 2)));
}

function buildCapsule(radius = 0.5, cylinderHeight = 1) {
    const vertices = [];
    const indices = [];
    const halfHeight = cylinderHeight / 2;
    const capRings = Math.max(2, Math.floor(ringSegments / 2));

    // Top hemisphere, then bottom, then the wall between them — built as one
    // vertex grid so the seams share vertices and shade smoothly.
    const rows = [];

    for (let ring = 0; ring <= capRings; ring++) {
        const phi = (ring / capRings) * (Math.PI / 2);
        rows.push({ y: halfHeight + Math.cos(phi) * radius, r: Math.sin(phi) * radius, ny: Math.cos(phi), offset: halfHeight });
    }
    for (let ring = 0; ring <= capRings; ring++) {
        const phi = Math.PI / 2 + (ring / capRings) * (Math.PI / 2);
        rows.push({ y: -halfHeight + Math.cos(phi) * radius, r: Math.sin(phi) * radius, ny: Math.cos(phi), offset: -halfHeight });
    }

    rows.forEach((row, rowIndex) => {
        for (let segment = 0; segment <= radialSegments; segment++) {
            const theta = (segment / radialSegments) * Math.PI * 2;
            const cos = Math.cos(theta), sin = Math.sin(theta);
            const normal = new Vector3(cos * (row.r / radius), row.ny, sin * (row.r / radius)).normalize();
            vertices.push(
                cos * row.r, row.y, sin * row.r,
                normal.x, normal.y, normal.z,
                segment / radialSegments, rowIndex / (rows.length - 1));
        }
    });

    const stride = radialSegments + 1;
    for (let row = 0; row < rows.length - 1; row++) {
        for (let segment = 0; segment < radialSegments; segment++) {
            const a = row * stride + segment;
            const b = a + stride;
            indices.push(a, b, a + 1, a + 1, b, b + 1);
        }
    }

    const total = cylinderHeight + radius * 2;
    return new Geometry(
        new Float32Array(vertices), new Uint16Array(indices),
        new Bounds(new Vector3(0, 0, 0), new Vector3(radius * 2, total, radius * 2)));
}

function buildTorus(majorRadius = 0.75, minorRadius = 0.25) {
    const vertices = [];
    const indices = [];

    for (let major = 0; major <= radialSegments; major++) {
        const u = (major / radialSegments) * Math.PI * 2;
        const cosU = Math.cos(u), sinU = Math.sin(u);

        for (let minor = 0; minor <= ringSegments; minor++) {
            const v = (minor / ringSegments) * Math.PI * 2;
            const cosV = Math.cos(v), sinV = Math.sin(v);

            const nx = cosV * cosU;
            const ny = sinV;
            const nz = cosV * sinU;

            vertices.push(
                (majorRadius + minorRadius * cosV) * cosU,
                minorRadius * sinV,
                (majorRadius + minorRadius * cosV) * sinU,
                nx, ny, nz,
                major / radialSegments, minor / ringSegments);
        }
    }

    const stride = ringSegments + 1;
    for (let major = 0; major < radialSegments; major++) {
        for (let minor = 0; minor < ringSegments; minor++) {
            const a = major * stride + minor;
            const b = a + stride;
            indices.push(a, b, a + 1, a + 1, b, b + 1);
        }
    }

    const extent = (majorRadius + minorRadius) * 2;
    return new Geometry(
        new Float32Array(vertices), new Uint16Array(indices),
        new Bounds(new Vector3(0, 0, 0), new Vector3(extent, minorRadius * 2, extent)));
}
