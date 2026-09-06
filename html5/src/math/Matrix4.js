// -----------------------------------------------------------------------------
// Matrix4 — 4x4 matrix with XNA/MonoGame semantics.
//
// Layout and convention are XNA's, not WebGL's: row-major storage
// (m[0..3] is row 0) and row-vector multiplication, so `S * R * T` reads in the
// order the transforms apply, exactly as the C# `Transform.GetWorldMatrix` does.
//
// That layout uploads to WebGL unchanged. GL reads a uniform matrix as
// column-major, so it interprets our row 0 as its column 0 — that is, it sees
// the transpose. Since GL also multiplies the other way round (`M * v` with a
// column vector), the two inversions cancel and the result is identical. Upload
// `toArray()` with `transpose = false`; never flip it "to fix" the orientation.
// -----------------------------------------------------------------------------

import { Vector3 } from './Vector3.js';

/** A 4x4 transformation matrix in XNA row-major, row-vector form. */
export class Matrix4 {
    /** @param {number[]} [values] Sixteen elements in row-major order. Defaults to identity. */
    constructor(values) {
        this.m = values
            ? Float32Array.from(values)
            : new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
    }

    static get identity() { return new Matrix4(); }

    clone() { return new Matrix4(this.m); }
    copy(other) { this.m.set(other.m); return this; }

    /** The raw row-major elements, ready to hand to `gl.uniformMatrix4fv`. */
    toArray() { return this.m; }

    // ---- Construction --------------------------------------------------------

    static translation(x, y, z) {
        if (typeof x === 'object') ({ x, y, z } = Vector3.from(x));
        return new Matrix4([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1]);
    }

    static scale(x, y, z) {
        if (typeof x === 'object') ({ x, y, z } = Vector3.from(x));
        else if (y === undefined) { y = x; z = x; }
        return new Matrix4([x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1]);
    }

    static rotationX(radians) {
        const c = Math.cos(radians), s = Math.sin(radians);
        return new Matrix4([1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]);
    }

    static rotationY(radians) {
        const c = Math.cos(radians), s = Math.sin(radians);
        return new Matrix4([c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]);
    }

    static rotationZ(radians) {
        const c = Math.cos(radians), s = Math.sin(radians);
        return new Matrix4([c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
    }

    /** The rotation part of a unit quaternion, as a matrix. */
    static fromQuaternion(q) {
        const { x, y, z, w } = q;
        const x2 = x + x, y2 = y + y, z2 = z + z;
        const xx = x * x2, xy = x * y2, xz = x * z2;
        const yy = y * y2, yz = y * z2, zz = z * z2;
        const wx = w * x2, wy = w * y2, wz = w * z2;

        return new Matrix4([
            1 - (yy + zz), xy + wz,       xz - wy,       0,
            xy - wz,       1 - (xx + zz), yz + wx,       0,
            xz + wy,       yz - wx,       1 - (xx + yy), 0,
            0,             0,             0,             1,
        ]);
    }

    /** Scale, then rotate, then translate — the order every transform uses. */
    static compose(position, rotation, scale) {
        const m = Matrix4.fromQuaternion(rotation).m;
        const s = Vector3.from(scale);
        const p = Vector3.from(position);

        return new Matrix4([
            m[0] * s.x, m[1] * s.x, m[2] * s.x, 0,
            m[4] * s.y, m[5] * s.y, m[6] * s.y, 0,
            m[8] * s.z, m[9] * s.z, m[10] * s.z, 0,
            p.x,        p.y,        p.z,        1,
        ]);
    }

    // ---- Camera matrices -----------------------------------------------------

    /**
     * A right-handed view matrix, matching `Matrix.CreateLookAt`.
     * The camera looks down its own -Z, as MonoGame's does.
     */
    static lookAt(eye, target, up) {
        const e = Vector3.from(eye);
        const zAxis = Vector3.subtract(e, Vector3.from(target)).normalize();  // backwards
        let xAxis = Vector3.cross(Vector3.from(up), zAxis);

        if (xAxis.lengthSquared < 1e-12) {
            // Looking straight along `up`; any perpendicular axis will do.
            xAxis = Vector3.cross(Math.abs(zAxis.y) > 0.99 ? Vector3.unitZ : Vector3.unitY, zAxis);
        }
        xAxis.normalize();
        const yAxis = Vector3.cross(zAxis, xAxis);

        return new Matrix4([
            xAxis.x, yAxis.x, zAxis.x, 0,
            xAxis.y, yAxis.y, zAxis.y, 0,
            xAxis.z, yAxis.z, zAxis.z, 0,
            -Vector3.dot(xAxis, e), -Vector3.dot(yAxis, e), -Vector3.dot(zAxis, e), 1,
        ]);
    }

    /**
     * Right-handed perspective projection to the WebGL clip range z in [-1, 1].
     *
     * MonoGame targets Direct3D's [0, 1] range; WebGL's NDC is [-1, 1], so the
     * third column differs from `Matrix.CreatePerspectiveFieldOfView`. The
     * visible result is the same, and depth precision is correct for GL.
     */
    static perspective(fovYRadians, aspect, near, far) {
        const f = 1 / Math.tan(fovYRadians / 2);
        const nf = 1 / (near - far);

        return new Matrix4([
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) * nf, -1,
            0, 0, 2 * far * near * nf, 0,
        ]);
    }

    /** Right-handed orthographic projection to clip z in [-1, 1]. */
    static orthographic(left, right, bottom, top, near, far) {
        const lr = 1 / (left - right);
        const bt = 1 / (bottom - top);
        const nf = 1 / (near - far);

        return new Matrix4([
            -2 * lr, 0, 0, 0,
            0, -2 * bt, 0, 0,
            0, 0, 2 * nf, 0,
            (left + right) * lr, (top + bottom) * bt, (far + near) * nf, 1,
        ]);
    }

    // ---- Operations ----------------------------------------------------------

    /**
     * Concatenates two matrices. `Matrix4.multiply(a, b)` applies `a` first,
     * then `b` — XNA's `a * b`, so `S * R * T` reads in application order.
     */
    static multiply(a, b) {
        const A = a.m, B = b.m;
        const out = new Float32Array(16);

        for (let row = 0; row < 4; row++) {
            const a0 = A[row * 4], a1 = A[row * 4 + 1], a2 = A[row * 4 + 2], a3 = A[row * 4 + 3];
            for (let col = 0; col < 4; col++) {
                out[row * 4 + col] =
                    a0 * B[col] + a1 * B[4 + col] + a2 * B[8 + col] + a3 * B[12 + col];
            }
        }
        return new Matrix4(out);
    }

    multiply(other) { return Matrix4.multiply(this, other); }

    static transpose(a) {
        const m = a.m;
        return new Matrix4([
            m[0], m[4], m[8], m[12],
            m[1], m[5], m[9], m[13],
            m[2], m[6], m[10], m[14],
            m[3], m[7], m[11], m[15],
        ]);
    }

    /**
     * The full inverse, or identity when the matrix is singular.
     *
     * The cofactor expansion is layout-agnostic — running it over our row-major
     * array yields the row-major inverse — so the classic flat-array formulation
     * is used verbatim.
     */
    static invert(a) {
        const m = a.m;
        const a00 = m[0],  a01 = m[1],  a02 = m[2],  a03 = m[3];
        const a10 = m[4],  a11 = m[5],  a12 = m[6],  a13 = m[7];
        const a20 = m[8],  a21 = m[9],  a22 = m[10], a23 = m[11];
        const a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];

        const b00 = a00 * a11 - a01 * a10;
        const b01 = a00 * a12 - a02 * a10;
        const b02 = a00 * a13 - a03 * a10;
        const b03 = a01 * a12 - a02 * a11;
        const b04 = a01 * a13 - a03 * a11;
        const b05 = a02 * a13 - a03 * a12;
        const b06 = a20 * a31 - a21 * a30;
        const b07 = a20 * a32 - a22 * a30;
        const b08 = a20 * a33 - a23 * a30;
        const b09 = a21 * a32 - a22 * a31;
        const b10 = a21 * a33 - a23 * a31;
        const b11 = a22 * a33 - a23 * a32;

        const det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
        if (Math.abs(det) < 1e-12) return Matrix4.identity;
        const d = 1 / det;

        return new Matrix4([
            (a11 * b11 - a12 * b10 + a13 * b09) * d,
            (a02 * b10 - a01 * b11 - a03 * b09) * d,
            (a31 * b05 - a32 * b04 + a33 * b03) * d,
            (a22 * b04 - a21 * b05 - a23 * b03) * d,

            (a12 * b08 - a10 * b11 - a13 * b07) * d,
            (a00 * b11 - a02 * b08 + a03 * b07) * d,
            (a32 * b02 - a30 * b05 - a33 * b01) * d,
            (a20 * b05 - a22 * b02 + a23 * b01) * d,

            (a10 * b10 - a11 * b08 + a13 * b06) * d,
            (a01 * b08 - a00 * b10 - a03 * b06) * d,
            (a30 * b04 - a31 * b02 + a33 * b00) * d,
            (a21 * b02 - a20 * b04 - a23 * b00) * d,

            (a11 * b07 - a10 * b09 - a12 * b06) * d,
            (a00 * b09 - a01 * b07 + a02 * b06) * d,
            (a31 * b01 - a30 * b03 - a32 * b00) * d,
            (a20 * b03 - a21 * b01 + a22 * b00) * d,
        ]);
    }

    /** Transforms a point, applying translation. */
    static transformPoint(m, v) {
        const a = m.m;
        const w = a[3] * v.x + a[7] * v.y + a[11] * v.z + a[15] || 1;
        return new Vector3(
            (a[0] * v.x + a[4] * v.y + a[8] * v.z + a[12]) / w,
            (a[1] * v.x + a[5] * v.y + a[9] * v.z + a[13]) / w,
            (a[2] * v.x + a[6] * v.y + a[10] * v.z + a[14]) / w);
    }

    /** Transforms a direction, ignoring translation. */
    static transformDirection(m, v) {
        const a = m.m;
        return new Vector3(
            a[0] * v.x + a[4] * v.y + a[8] * v.z,
            a[1] * v.x + a[5] * v.y + a[9] * v.z,
            a[2] * v.x + a[6] * v.y + a[10] * v.z);
    }
}
