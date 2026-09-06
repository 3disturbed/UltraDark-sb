// -----------------------------------------------------------------------------
// PhysicsSystem3D — 3D collision, ground detection and raycasting.
//
// An axis-aligned solver. The C# engine wraps BEPU, which is a full rigid-body
// simulation; matching that in the browser would mean shipping a WASM physics
// engine. This covers what a 3D scene actually needs — characters that stand on
// things and do not walk through walls, rigid bodies that fall and rest, and
// raycasts for picking and line of sight — in a few hundred lines.
// -----------------------------------------------------------------------------

import { Vector3, Bounds } from '../math/index.js';
import { CollisionData } from '../core/Component.js';
import { Collider3D } from './Collider3D.js';
import { Rigidbody3D } from './Rigidbody3D.js';

/** What a 3D raycast found. */
export class RaycastHit3D {
    constructor(actor, point, normal, distance, collider) {
        this.actor = actor;
        this.point = point;
        this.normal = normal;
        this.distance = distance;
        this.collider = collider;
    }
}

/** Simulates 3D physics for one scene. */
export class PhysicsSystem3D {
    constructor({ scene = null, gravity = new Vector3(0, -9.81, 0) } = {}) {
        this.scene = scene;
        this.gravity = Vector3.from(gravity);
        this.enabled = true;

        this._contacts = new Map();
    }

    /** The colliders this world simulates. */
    get colliders() {
        return Collider3D.all.filter((c) =>
            c.enabled
            && c.actor
            && c.actor.isActive
            && !c.actor.isDestroyed
            && (this.scene === null || c.actor.scene === this.scene));
    }

    get colliderCount() { return this.colliders.length; }

    /** Advances the world by one fixed step. */
    fixedStep(dt) {
        if (!this.enabled || dt <= 0) return;

        const entries = this.colliders.map((collider) => ({
            collider,
            body: collider.getComponent(Rigidbody3D),
            bounds: collider.getWorldBounds(),
        }));

        // Integrate the dynamic bodies.
        const stepped = new Set();
        for (const entry of entries) {
            if (!entry.body || stepped.has(entry.body)) continue;
            stepped.add(entry.body);
            entry.body.integrate(dt, this.gravity);
            entry.body.applyVelocity(dt);
            entry.bounds = entry.collider.getWorldBounds();
        }

        const touching = new Map();

        for (let i = 0; i < entries.length; i++) {
            for (let j = i + 1; j < entries.length; j++) {
                const a = entries[i];
                const b = entries[j];

                const aStatic = !a.body || a.body.isKinematic;
                const bStatic = !b.body || b.body.isKinematic;
                if (aStatic && bStatic) continue;
                if (!a.bounds.intersects(b.bounds)) continue;

                touching.set(pairKey(a.collider, b.collider), { a: a.collider, b: b.collider });

                if (!a.collider.isTrigger && !b.collider.isTrigger) {
                    this._separate(a, b);
                }
            }
        }

        this._dispatch(touching);
    }

    _separate(a, b) {
        // Push apart along the axis of least overlap: it is the shortest way out
        // and the one that reads as "landed on top" rather than "shoved sideways".
        const overlap = axisOverlap(a.bounds, b.bounds);
        if (!overlap) return;

        const inverseMassA = a.body?.inverseMass ?? 0;
        const inverseMassB = b.body?.inverseMass ?? 0;
        const total = inverseMassA + inverseMassB;
        if (total <= 0) return;

        const push = Vector3.scale(overlap.normal, overlap.depth / total);

        if (a.body && inverseMassA > 0) {
            const t = a.collider.actor.transform3D;
            t.position = Vector3.subtract(t.position, Vector3.scale(push, inverseMassA));
            zeroAlong(a.body.linearVelocity, overlap.normal, -1);
        }
        if (b.body && inverseMassB > 0) {
            const t = b.collider.actor.transform3D;
            t.position = Vector3.add(t.position, Vector3.scale(push, inverseMassB));
            zeroAlong(b.body.linearVelocity, overlap.normal, 1);
        }

        a.bounds = a.collider.getWorldBounds();
        b.bounds = b.collider.getWorldBounds();
    }

    _dispatch(touching) {
        for (const [key, pair] of touching) {
            const wasTouching = this._contacts.has(key);
            const { a, b } = pair;

            if (a.isTrigger || b.isTrigger) {
                fire(a.actor, wasTouching ? 'onTriggerStay' : 'onTriggerEnter', b.actor);
                fire(b.actor, wasTouching ? 'onTriggerStay' : 'onTriggerEnter', a.actor);
            } else {
                fire(a.actor, wasTouching ? 'onCollisionStay' : 'onCollisionEnter',
                    new CollisionData({ other: b.actor }));
                fire(b.actor, wasTouching ? 'onCollisionStay' : 'onCollisionEnter',
                    new CollisionData({ other: a.actor }));
            }
        }

        for (const [key, pair] of this._contacts) {
            if (touching.has(key)) continue;
            const { a, b } = pair;
            if (a.actor?.isDestroyed || b.actor?.isDestroyed) continue;

            if (a.isTrigger || b.isTrigger) {
                fire(a.actor, 'onTriggerExit', b.actor);
                fire(b.actor, 'onTriggerExit', a.actor);
            } else {
                fire(a.actor, 'onCollisionExit', new CollisionData({ other: b.actor }));
                fire(b.actor, 'onCollisionExit', new CollisionData({ other: a.actor }));
            }
        }

        this._contacts = touching;
    }

    // -------------------------------------------------------------------------
    // Character support
    // -------------------------------------------------------------------------

    /**
     * Pushes a capsule out of anything it overlaps and returns the freed position.
     *
     * @param {Vector3} position Capsule centre.
     * @param {number} radius
     * @param {number} halfHeight
     * @param {import('../core/Actor.js').Actor} owner Skipped, so it does not fight itself.
     */
    resolveCapsule(position, radius, halfHeight, owner) {
        let resolved = position.clone();

        // Two passes: freeing a capsule from one wall can push it into another,
        // and a corner needs both resolved before the result is stable.
        for (let pass = 0; pass < 2; pass++) {
            const capsule = new Bounds(resolved, new Vector3(radius * 2, halfHeight * 2, radius * 2));
            let moved = false;

            for (const collider of this.colliders) {
                if (collider.actor === owner || collider.isTrigger) continue;

                const bounds = collider.getWorldBounds();
                if (!capsule.intersects(bounds)) continue;

                const overlap = axisOverlap(capsule, bounds);
                if (!overlap || overlap.depth <= 0) continue;

                // Standing on something is the ground check's job; resolving it
                // here would launch the character up every step.
                if (overlap.normal.y > 0.5) continue;

                resolved = Vector3.subtract(resolved, Vector3.scale(overlap.normal, overlap.depth));
                moved = true;
                break;
            }

            if (!moved) break;
        }

        return resolved;
    }

    /**
     * Looks for ground beneath a capsule.
     *
     * @param {Vector3} position Capsule centre.
     * @param {number} radius
     * @param {number} feetOffset Distance from the centre down to the soles.
     * @param {number} snapDistance How far below the feet to reach.
     * @param {import('../core/Actor.js').Actor} owner Skipped, so it cannot stand on itself.
     * @param {number} [stepUpHeight=0] How far *above* the feet a surface may be and still
     *   count as ground — a kerb to step onto, or geometry the character has sunk into.
     * @returns {?{y: number, normal: Vector3, actor: object}} The surface height, or null.
     */
    groundCheck(position, radius, feetOffset, snapDistance, owner, stepUpHeight = 0) {
        const feetY = position.y - feetOffset;
        let best = null;

        for (const collider of this.colliders) {
            if (collider.actor === owner || collider.isTrigger) continue;

            const bounds = collider.getWorldBounds();
            const top = bounds.max.y;

            // A surface counts as ground from `snapDistance` below the feet up to
            // `stepUpHeight` above them. Rejecting everything above the feet
            // outright made a character that started even slightly inside the
            // floor fall through it forever: the floor read as a ceiling, and
            // nothing else was ever going to catch it.
            if (top > feetY + stepUpHeight || top < feetY - snapDistance) continue;

            // Horizontal overlap, so the character does not hover off an edge.
            const min = bounds.min, max = bounds.max;
            if (position.x + radius < min.x || position.x - radius > max.x) continue;
            if (position.z + radius < min.z || position.z - radius > max.z) continue;

            if (!best || top > best.y) {
                best = { y: top, normal: new Vector3(0, 1, 0), actor: collider.actor };
            }
        }

        return best;
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------

    /** Casts a ray and returns the nearest hit, or null. */
    raycast(origin, direction, maxDistance = Infinity, options = {}) {
        const from = Vector3.from(origin);
        const dir = Vector3.from(direction).normalize();
        let nearest = null;

        for (const collider of this.colliders) {
            if (collider.isTrigger && !options.hitTriggers) continue;
            if (options.filter && !options.filter(collider)) continue;
            if (options.ignore && collider.actor === options.ignore) continue;

            const hit = rayVsBounds(from, dir, maxDistance, collider.getWorldBounds());
            if (!hit) continue;
            if (nearest && hit.distance >= nearest.distance) continue;

            nearest = new RaycastHit3D(collider.actor, hit.point, hit.normal, hit.distance, collider);
        }

        return nearest;
    }

    /** Every collider whose bounds overlap a sphere. */
    overlapSphere(center, radius, options = {}) {
        const c = Vector3.from(center);
        const query = new Bounds(c, new Vector3(radius * 2, radius * 2, radius * 2));

        return this.colliders.filter((collider) => {
            if (collider.isTrigger && !options.hitTriggers) return false;
            if (options.filter && !options.filter(collider)) return false;

            const bounds = collider.getWorldBounds();
            if (!query.intersects(bounds)) return false;
            // The box test is conservative; check the true distance to the box.
            return Vector3.distance(bounds.closestPoint(c), c) <= radius;
        });
    }

    /** Every collider whose bounds overlap a box. */
    overlapBox(center, size, options = {}) {
        const query = new Bounds(Vector3.from(center), Vector3.from(size));
        return this.colliders.filter((collider) => {
            if (collider.isTrigger && !options.hitTriggers) return false;
            if (options.filter && !options.filter(collider)) return false;
            return query.intersects(collider.getWorldBounds());
        });
    }

    clear() { this._contacts.clear(); }
}

/** The axis of least overlap between two boxes, and how deep it is. */
function axisOverlap(a, b) {
    const aMin = a.min, aMax = a.max;
    const bMin = b.min, bMax = b.max;

    const overlapX = Math.min(aMax.x, bMax.x) - Math.max(aMin.x, bMin.x);
    const overlapY = Math.min(aMax.y, bMax.y) - Math.max(aMin.y, bMin.y);
    const overlapZ = Math.min(aMax.z, bMax.z) - Math.max(aMin.z, bMin.z);

    if (overlapX <= 0 || overlapY <= 0 || overlapZ <= 0) return null;

    // The normal points from A towards B, so the caller pushes A back along it.
    if (overlapX <= overlapY && overlapX <= overlapZ) {
        return { normal: new Vector3(b.center.x >= a.center.x ? 1 : -1, 0, 0), depth: overlapX };
    }
    if (overlapY <= overlapZ) {
        return { normal: new Vector3(0, b.center.y >= a.center.y ? 1 : -1, 0), depth: overlapY };
    }
    return { normal: new Vector3(0, 0, b.center.z >= a.center.z ? 1 : -1), depth: overlapZ };
}

/** Cancels the component of a velocity that drives it further into a contact. */
function zeroAlong(velocity, normal, sign) {
    const along = velocity.x * normal.x + velocity.y * normal.y + velocity.z * normal.z;
    if (along * sign > 0) return;   // already moving apart
    velocity.x -= normal.x * along;
    velocity.y -= normal.y * along;
    velocity.z -= normal.z * along;
}

/** Slab-method ray against an axis-aligned box. */
function rayVsBounds(origin, direction, maxDistance, bounds) {
    const min = bounds.min, max = bounds.max;
    let tMin = 0;
    let tMax = maxDistance;
    let hitAxis = 0;
    let hitSign = -1;

    const axes = ['x', 'y', 'z'];
    for (let i = 0; i < 3; i++) {
        const axis = axes[i];
        const d = direction[axis];
        const o = origin[axis];

        if (Math.abs(d) < 1e-9) {
            // Parallel to this slab: a miss unless the origin is already inside it.
            if (o < min[axis] || o > max[axis]) return null;
            continue;
        }

        const inverse = 1 / d;
        let t1 = (min[axis] - o) * inverse;
        let t2 = (max[axis] - o) * inverse;
        let sign = -1;

        if (t1 > t2) { const swap = t1; t1 = t2; t2 = swap; sign = 1; }

        if (t1 > tMin) { tMin = t1; hitAxis = i; hitSign = sign; }
        if (t2 < tMax) tMax = t2;
        if (tMin > tMax) return null;
    }

    const normal = new Vector3(0, 0, 0);
    normal[axes[hitAxis]] = hitSign;

    return {
        distance: tMin,
        point: Vector3.add(origin, Vector3.scale(direction, tMin)),
        normal,
    };
}

function pairKey(a, b) {
    return a.actor.id < b.actor.id
        ? `${a.actor.id}|${b.actor.id}`
        : `${b.actor.id}|${a.actor.id}`;
}

function fire(actor, hook, payload) {
    if (!actor || actor.isDestroyed) return;
    try {
        actor[hook](payload);
    } catch (err) {
        console.error(`[PhysicsSystem3D] ${hook} threw:`, err);
    }
}

export { rayVsBounds };
