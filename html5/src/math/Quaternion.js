// -----------------------------------------------------------------------------
// Quaternion — rotation, matching Microsoft.Xna.Framework.Quaternion.
//
// The euler conversions are deliberately identical to Transform3D's in the C#
// engine (`CreateFromYawPitchRoll` with euler read as pitch-X, yaw-Y, roll-Z in
// degrees), so a `rotation3` written by either engine loads the same in both.
// -----------------------------------------------------------------------------

import { Vector3 } from './Vector3.js';

const DEG2RAD = Math.PI / 180;
const RAD2DEG = 180 / Math.PI;

/** A rotation expressed as a unit quaternion. */
export class Quaternion {
    constructor(x = 0, y = 0, z = 0, w = 1) {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    static get identity() { return new Quaternion(0, 0, 0, 1); }

    set(x, y, z, w) { this.x = x; this.y = y; this.z = z; this.w = w; return this; }
    copy(q)  { this.x = q.x; this.y = q.y; this.z = q.z; this.w = q.w; return this; }
    clone()  { return new Quaternion(this.x, this.y, this.z, this.w); }

    get length()        { return Math.hypot(this.x, this.y, this.z, this.w); }
    get lengthSquared() { return this.x ** 2 + this.y ** 2 + this.z ** 2 + this.w ** 2; }

    normalize() {
        const len = this.length;
        if (len > 1e-9) { this.x /= len; this.y /= len; this.z /= len; this.w /= len; }
        else { this.x = 0; this.y = 0; this.z = 0; this.w = 1; }
        return this;
    }

    /** The conjugate, which for a unit quaternion is also the inverse. */
    conjugate() { this.x = -this.x; this.y = -this.y; this.z = -this.z; return this; }

    equals(q, epsilon = 1e-6) {
        return Math.abs(this.x - q.x) <= epsilon
            && Math.abs(this.y - q.y) <= epsilon
            && Math.abs(this.z - q.z) <= epsilon
            && Math.abs(this.w - q.w) <= epsilon;
    }

    /** Serialises as `[x, y, z, w]`, the shape a scene file's `rotation3` uses. */
    toArray() { return [this.x, this.y, this.z, this.w]; }
    toJSON()  { return [this.x, this.y, this.z, this.w]; }
    toString() { return `(${this.x}, ${this.y}, ${this.z}, ${this.w})`; }

    // ---- Static forms --------------------------------------------------------

    /**
     * Hamilton product. `Quaternion.multiply(a, b)` applies `b` first then `a`,
     * matching XNA's `a * b`.
     */
    static multiply(a, b) {
        return new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
    }

    static conjugate(q) { return new Quaternion(-q.x, -q.y, -q.z, q.w); }

    /** The inverse rotation. For a non-unit quaternion the norm is divided out. */
    static inverse(q) {
        const n = q.lengthSquared;
        if (n < 1e-12) return Quaternion.identity;
        return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n);
    }

    static dot(a, b) { return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w; }

    static normalize(q) { return q.clone().normalize(); }

    /** Rotation of `angle` radians about a (not necessarily unit) axis. */
    static fromAxisAngle(axis, angle) {
        const len = Math.hypot(axis.x, axis.y, axis.z);
        if (len < 1e-9) return Quaternion.identity;
        const half = angle * 0.5;
        const s = Math.sin(half) / len;
        return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Math.cos(half));
    }

    /**
     * XNA's `CreateFromYawPitchRoll`. Angles are radians: yaw about Y, pitch
     * about X, roll about Z, applied in that order.
     */
    static fromYawPitchRoll(yaw, pitch, roll) {
        const hr = roll * 0.5,  sr = Math.sin(hr), cr = Math.cos(hr);
        const hp = pitch * 0.5, sp = Math.sin(hp), cp = Math.cos(hp);
        const hy = yaw * 0.5,   sy = Math.sin(hy), cy = Math.cos(hy);

        return new Quaternion(
            cy * sp * cr + sy * cp * sr,
            sy * cp * cr - cy * sp * sr,
            cy * cp * sr - sy * sp * cr,
            cy * cp * cr + sy * sp * sr);
    }

    /**
     * Builds a rotation from euler angles in degrees, read as
     * `(pitch, yaw, roll)` — the same order the editor and the MCP tools use.
     */
    static fromEuler(eulerDegrees) {
        const e = Vector3.from(eulerDegrees);
        return Quaternion.fromYawPitchRoll(e.y * DEG2RAD, e.x * DEG2RAD, e.z * DEG2RAD);
    }

    /**
     * The true inverse of {@link Quaternion.fromEuler}: pitch, yaw and roll in degrees.
     *
     * `fromYawPitchRoll` composes as `Ry(yaw) * Rx(pitch) * Rz(roll)` — the YXZ
     * convention, in which *pitch* is the constrained middle axis. The extraction
     * below matches that composition, so `toEuler(fromEuler(e))` returns `e`.
     *
     * The C# engine's `Transform3D.QuaternionToEuler` does not: it applies the
     * standard ZYX aerospace formula (`asin` on yaw) to a quaternion built the
     * YXZ way, so it is only correct when one of the three angles is zero. A
     * rotation of, say, (10, -170, 25) degrees comes back from it as a different
     * rotation entirely, not merely a different spelling of the same one.
     *
     * Reproducing that here was not worth it. Scene files store `rotation3` as a
     * quaternion, so nothing shared between the engines changes; what does change
     * is that an editor rotation field can be read and written back without
     * corrupting the actor's orientation, which is not optional in a tool people
     * type numbers into. See html5/README.md for the full note.
     */
    static toEuler(q) {
        // From R = Ry(yaw) * Rx(pitch) * Rz(roll):
        //   R[1][2] = -sin(pitch)
        //   R[1][0] / R[1][1] = tan(roll)
        //   R[0][2] / R[2][2] = tan(yaw)
        const sinPitch = 2 * (q.w * q.x - q.y * q.z);

        // Looking straight up or down: cos(pitch) is zero and yaw and roll trade
        // off against each other. Pin roll and put the whole turn into yaw.
        if (Math.abs(sinPitch) >= 0.99999) {
            const pitch = Math.sign(sinPitch) * Math.PI / 2;
            const yaw = Math.atan2(-2 * (q.x * q.z - q.w * q.y), 1 - 2 * (q.y * q.y + q.z * q.z));
            return new Vector3(pitch * RAD2DEG, yaw * RAD2DEG, 0);
        }

        const pitch = Math.asin(sinPitch);
        const roll = Math.atan2(2 * (q.x * q.y + q.w * q.z), 1 - 2 * (q.x * q.x + q.z * q.z));
        const yaw = Math.atan2(2 * (q.x * q.z + q.w * q.y), 1 - 2 * (q.x * q.x + q.y * q.y));

        return new Vector3(pitch * RAD2DEG, yaw * RAD2DEG, roll * RAD2DEG);
    }

    /**
     * The C# engine's `Transform3D.QuaternionToEuler`, bug included.
     *
     * Kept so a tool can reproduce exactly what the C# editor would show for a
     * given rotation. Do not use it to read a rotation back: it is not the
     * inverse of {@link Quaternion.fromEuler}.
     */
    static toEulerCSharp(q) {
        const sinr = 2 * (q.w * q.x + q.y * q.z);
        const cosr = 1 - 2 * (q.x * q.x + q.y * q.y);
        const pitch = Math.atan2(sinr, cosr);

        const sinp = 2 * (q.w * q.y - q.z * q.x);
        const yaw = Math.abs(sinp) >= 1 ? Math.sign(sinp) * Math.PI / 2 : Math.asin(sinp);

        const siny = 2 * (q.w * q.z + q.x * q.y);
        const cosy = 1 - 2 * (q.y * q.y + q.z * q.z);
        const roll = Math.atan2(siny, cosy);

        return new Vector3(pitch * RAD2DEG, yaw * RAD2DEG, roll * RAD2DEG);
    }

    /** Spherical linear interpolation, taking the short way round. */
    static slerp(a, b, t) {
        let cos = Quaternion.dot(a, b);
        let bx = b.x, by = b.y, bz = b.z, bw = b.w;

        // Negating one end picks the shorter arc; q and -q are the same rotation.
        if (cos < 0) { cos = -cos; bx = -bx; by = -by; bz = -bz; bw = -bw; }

        let s0, s1;
        if (cos > 0.9995) {
            // Nearly parallel: lerp and renormalise, because sin(theta) -> 0.
            s0 = 1 - t;
            s1 = t;
        } else {
            const theta = Math.acos(cos);
            const sinTheta = Math.sin(theta);
            s0 = Math.sin((1 - t) * theta) / sinTheta;
            s1 = Math.sin(t * theta) / sinTheta;
        }

        return new Quaternion(
            a.x * s0 + bx * s1,
            a.y * s0 + by * s1,
            a.z * s0 + bz * s1,
            a.w * s0 + bw * s1).normalize();
    }

    /** Linear interpolation followed by renormalisation. Cheaper than slerp. */
    static lerp(a, b, t) {
        const dot = Quaternion.dot(a, b);
        const sign = dot < 0 ? -1 : 1;
        return new Quaternion(
            a.x + (b.x * sign - a.x) * t,
            a.y + (b.y * sign - a.y) * t,
            a.z + (b.z * sign - a.z) * t,
            a.w + (b.w * sign - a.w) * t).normalize();
    }

    /** A rotation whose forward axis points along `forward`, with `up` as the hint. */
    static lookRotation(forward, up = Vector3.up) {
        const f = Vector3.from(forward).normalize();
        if (f.lengthSquared < 1e-12) return Quaternion.identity;

        let u = Vector3.from(up).normalize();
        let r = Vector3.cross(u, f);
        if (r.lengthSquared < 1e-9) {
            // forward and up are parallel; pick any perpendicular axis.
            u = Math.abs(f.y) > 0.99 ? Vector3.unitZ : Vector3.unitY;
            r = Vector3.cross(u, f);
        }
        r.normalize();
        u = Vector3.cross(f, r).normalize();

        // Build from the rotation matrix whose columns are (right, up, -forward),
        // matching MonoGame's -Z forward convention.
        const m00 = r.x, m01 = r.y, m02 = r.z;
        const m10 = u.x, m11 = u.y, m12 = u.z;
        const m20 = -f.x, m21 = -f.y, m22 = -f.z;

        const trace = m00 + m11 + m22;
        if (trace > 0) {
            const s = Math.sqrt(trace + 1) * 2;
            return new Quaternion((m12 - m21) / s, (m20 - m02) / s, (m01 - m10) / s, 0.25 * s);
        }
        if (m00 > m11 && m00 > m22) {
            const s = Math.sqrt(1 + m00 - m11 - m22) * 2;
            return new Quaternion(0.25 * s, (m10 + m01) / s, (m20 + m02) / s, (m12 - m21) / s);
        }
        if (m11 > m22) {
            const s = Math.sqrt(1 + m11 - m00 - m22) * 2;
            return new Quaternion((m10 + m01) / s, 0.25 * s, (m21 + m12) / s, (m20 - m02) / s);
        }
        const s = Math.sqrt(1 + m22 - m00 - m11) * 2;
        return new Quaternion((m20 + m02) / s, (m21 + m12) / s, 0.25 * s, (m01 - m10) / s);
    }

    /** Reads `[x, y, z, w]`, `{ x, y, z, w }`, `{ X, Y, Z, W }` or null. */
    static from(value) {
        if (value == null) return Quaternion.identity;
        if (Array.isArray(value)) {
            return new Quaternion(
                +value[0] || 0, +value[1] || 0, +value[2] || 0,
                value.length > 3 ? (+value[3] || 0) : 1);
        }
        return new Quaternion(
            +(value.x ?? value.X ?? 0) || 0,
            +(value.y ?? value.Y ?? 0) || 0,
            +(value.z ?? value.Z ?? 0) || 0,
            +(value.w ?? value.W ?? 1) || 0);
    }
}
