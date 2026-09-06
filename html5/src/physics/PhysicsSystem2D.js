// -----------------------------------------------------------------------------
// PhysicsSystem2D — the 2D world: integration, contacts, and the queries.
//
// One system per scene, reached as `scene.physics2D`. The C# engine keeps a
// process-wide singleton, which means two open scenes share a world; scoping it
// to the scene means the editor can have an edit-time scene and a play-time copy
// without their bodies colliding with each other.
// -----------------------------------------------------------------------------

import { Vector2 } from '../math/index.js';
import { CollisionData } from '../core/Component.js';
import { Rigidbody2D, BodyType } from './Rigidbody2D.js';
import { Collider2D } from './Collider2D.js';
import { aabbOverlap, collide, raycastShape, Circle, Polygon } from './Shapes2D.js';

/** What a raycast found. */
export class RaycastHit2D {
    constructor(actor, point, normal, distance, collider) {
        this.actor = actor;
        this.point = point;
        this.normal = normal;
        this.distance = distance;
        this.collider = collider;
    }
}

/** Simulates 2D physics for one scene. */
export class PhysicsSystem2D {
    /**
     * @param {object} [options]
     * @param {import('../core/Scene.js').Scene} [options.scene] Restricts simulation to one scene's actors.
     * @param {Vector2} [options.gravity]
     */
    constructor({ scene = null, gravity = new Vector2(0, 980) } = {}) {
        /** The scene this world simulates. Null means every registered collider. */
        this.scene = scene;

        /**
         * Downwards is positive Y, as on screen. The default is tuned for a
         * pixel-scale 2D game, matching the C# engine's `WorldGravity`.
         */
        this.gravity = Vector2.from(gravity);

        /** Solver passes per step. More is stiffer and more expensive. */
        this.solverIterations = 6;

        /** How much overlap is corrected per step, 0..1. */
        this.positionCorrection = 0.5;

        /** Overlap left uncorrected, so resting bodies stop jittering. */
        this.slop = 0.01;

        // Contact pairs that were touching last step, so enter/stay/exit can be
        // distinguished. Keyed by an order-independent pair id.
        this._contacts = new Map();

        this.enabled = true;
    }

    /**
     * The colliders this world simulates: enabled, on an active actor, and in
     * this system's scene.
     */
    get colliders() {
        return Collider2D.all.filter((c) =>
            c.enabled
            && c.actor
            && c.actor.isActive
            && !c.actor.isDestroyed
            && (this.scene === null || c.actor.scene === this.scene));
    }

    /** How many colliders are in the world. */
    get colliderCount() { return this.colliders.length; }

    // -------------------------------------------------------------------------
    // Simulation
    // -------------------------------------------------------------------------

    /** Advances the world by one fixed step. Called by the engine host. */
    fixedStep(dt) {
        if (!this.enabled || dt <= 0) return;

        const colliders = this.colliders;

        // 1. Integrate forces and move everything.
        const bodies = new Set();
        for (const collider of colliders) {
            const body = collider.getComponent(Rigidbody2D);
            if (body && !bodies.has(body)) {
                bodies.add(body);
                body.integrate(dt, this.gravity);
                body.applyVelocity(dt);
            }
        }

        // 2. Broad phase, then narrow phase.
        const pairs = this._broadPhase(colliders);
        const manifolds = [];

        for (const [a, b] of pairs) {
            const manifold = collide(a.shape, b.shape);
            if (!manifold) continue;
            manifolds.push({ a: a.collider, b: b.collider, manifold });
        }

        // 3. Resolve, then correct the leftover overlap.
        for (let i = 0; i < this.solverIterations; i++) {
            for (const contact of manifolds) this._resolve(contact);
        }
        for (const contact of manifolds) this._correctPositions(contact);

        // 4. Dispatch enter/stay/exit.
        this._dispatchContacts(manifolds);
    }

    _broadPhase(colliders) {
        // Shapes are built once per step and reused: `getWorldShape` allocates,
        // and an n^2 sweep would otherwise rebuild each one n times.
        const entries = colliders.map((collider) => {
            const shape = collider.getWorldShape();
            return { collider, shape, aabb: shape.aabb, body: collider.getComponent(Rigidbody2D) };
        });

        // Sweep on X: sorting by left edge lets the inner loop stop as soon as a
        // candidate starts beyond the current one's right edge.
        entries.sort((p, q) => p.aabb.minX - q.aabb.minX);

        const pairs = [];
        for (let i = 0; i < entries.length; i++) {
            const a = entries[i];
            for (let j = i + 1; j < entries.length; j++) {
                const b = entries[j];
                if (b.aabb.minX > a.aabb.maxX) break;

                // Two immovable bodies can never resolve against each other.
                const aStatic = !a.body || a.body.bodyType === BodyType.Static;
                const bStatic = !b.body || b.body.bodyType === BodyType.Static;
                if (aStatic && bStatic) continue;

                if (!aabbOverlap(a.aabb, b.aabb)) continue;
                pairs.push([a, b]);
            }
        }
        return pairs;
    }

    _resolve({ a, b, manifold }) {
        // A trigger detects but never pushes.
        if (a.isTrigger || b.isTrigger) return;

        const bodyA = a.getComponent(Rigidbody2D);
        const bodyB = b.getComponent(Rigidbody2D);

        const inverseMassA = bodyA?.inverseMass ?? 0;
        const inverseMassB = bodyB?.inverseMass ?? 0;
        const totalInverseMass = inverseMassA + inverseMassB;
        if (totalInverseMass <= 0) return;

        const velocityA = bodyA?.linearVelocity ?? Vector2.zero;
        const velocityB = bodyB?.linearVelocity ?? Vector2.zero;

        const relativeX = velocityB.x - velocityA.x;
        const relativeY = velocityB.y - velocityA.y;
        const separating = relativeX * manifold.normal.x + relativeY * manifold.normal.y;

        // Already moving apart: resolving now would suck them back together.
        if (separating > 0) return;

        const restitution = Math.max(a.restitution, b.restitution);
        const impulseMagnitude = -(1 + restitution) * separating / totalInverseMass;

        const ix = manifold.normal.x * impulseMagnitude;
        const iy = manifold.normal.y * impulseMagnitude;

        if (bodyA && inverseMassA > 0) {
            bodyA.linearVelocity.x -= ix * inverseMassA;
            bodyA.linearVelocity.y -= iy * inverseMassA;
        }
        if (bodyB && inverseMassB > 0) {
            bodyB.linearVelocity.x += ix * inverseMassB;
            bodyB.linearVelocity.y += iy * inverseMassB;
        }

        this._applyFriction(a, b, manifold, impulseMagnitude, inverseMassA, inverseMassB);
    }

    _applyFriction(a, b, manifold, normalImpulse, inverseMassA, inverseMassB) {
        const bodyA = a.getComponent(Rigidbody2D);
        const bodyB = b.getComponent(Rigidbody2D);

        const velocityA = bodyA?.linearVelocity ?? Vector2.zero;
        const velocityB = bodyB?.linearVelocity ?? Vector2.zero;

        const relativeX = velocityB.x - velocityA.x;
        const relativeY = velocityB.y - velocityA.y;

        // The component of relative motion along the surface.
        const alongNormal = relativeX * manifold.normal.x + relativeY * manifold.normal.y;
        const tangentX = relativeX - manifold.normal.x * alongNormal;
        const tangentY = relativeY - manifold.normal.y * alongNormal;
        const tangentLength = Math.hypot(tangentX, tangentY);
        if (tangentLength < 1e-6) return;

        const tx = tangentX / tangentLength;
        const ty = tangentY / tangentLength;

        const totalInverseMass = inverseMassA + inverseMassB;
        let frictionMagnitude = -(relativeX * tx + relativeY * ty) / totalInverseMass;

        // Coulomb's law: friction cannot exceed the normal force times mu, or it
        // would drive the surfaces backwards instead of merely stopping them.
        const mu = Math.sqrt(a.friction * b.friction);
        const limit = Math.abs(normalImpulse) * mu;
        frictionMagnitude = Math.max(-limit, Math.min(limit, frictionMagnitude));

        if (bodyA && inverseMassA > 0) {
            bodyA.linearVelocity.x -= tx * frictionMagnitude * inverseMassA;
            bodyA.linearVelocity.y -= ty * frictionMagnitude * inverseMassA;
        }
        if (bodyB && inverseMassB > 0) {
            bodyB.linearVelocity.x += tx * frictionMagnitude * inverseMassB;
            bodyB.linearVelocity.y += ty * frictionMagnitude * inverseMassB;
        }
    }

    _correctPositions({ a, b, manifold }) {
        if (a.isTrigger || b.isTrigger) return;

        const bodyA = a.getComponent(Rigidbody2D);
        const bodyB = b.getComponent(Rigidbody2D);
        const inverseMassA = bodyA?.inverseMass ?? 0;
        const inverseMassB = bodyB?.inverseMass ?? 0;
        const totalInverseMass = inverseMassA + inverseMassB;
        if (totalInverseMass <= 0) return;

        const depth = Math.max(manifold.depth - this.slop, 0);
        if (depth <= 0) return;

        const correction = (depth / totalInverseMass) * this.positionCorrection;
        const cx = manifold.normal.x * correction;
        const cy = manifold.normal.y * correction;

        if (bodyA && inverseMassA > 0) {
            const p = a.transform.position;
            a.transform.position = new Vector2(p.x - cx * inverseMassA, p.y - cy * inverseMassA);
        }
        if (bodyB && inverseMassB > 0) {
            const p = b.transform.position;
            b.transform.position = new Vector2(p.x + cx * inverseMassB, p.y + cy * inverseMassB);
        }
    }

    _dispatchContacts(manifolds) {
        const current = new Map();

        for (const { a, b, manifold } of manifolds) {
            const key = pairKey(a, b);
            current.set(key, { a, b, manifold });

            const wasTouching = this._contacts.has(key);
            const isTrigger = a.isTrigger || b.isTrigger;

            if (isTrigger) {
                fire(a.actor, wasTouching ? 'onTriggerStay' : 'onTriggerEnter', b.actor);
                fire(b.actor, wasTouching ? 'onTriggerStay' : 'onTriggerEnter', a.actor);
            } else {
                const relative = relativeSpeed(a, b);
                fire(a.actor, wasTouching ? 'onCollisionStay' : 'onCollisionEnter',
                    new CollisionData({
                        other: b.actor, contactPoint: manifold.contactPoint,
                        normal: manifold.normal, relativeVelocity: relative,
                    }));
                fire(b.actor, wasTouching ? 'onCollisionStay' : 'onCollisionEnter',
                    new CollisionData({
                        other: a.actor, contactPoint: manifold.contactPoint,
                        normal: manifold.normal.clone().negate(), relativeVelocity: relative,
                    }));
            }
        }

        // Anything that was touching and no longer is has just separated.
        for (const [key, previous] of this._contacts) {
            if (current.has(key)) continue;

            const { a, b } = previous;
            if (a.actor?.isDestroyed || b.actor?.isDestroyed) continue;

            if (a.isTrigger || b.isTrigger) {
                fire(a.actor, 'onTriggerExit', b.actor);
                fire(b.actor, 'onTriggerExit', a.actor);
            } else {
                fire(a.actor, 'onCollisionExit', new CollisionData({ other: b.actor }));
                fire(b.actor, 'onCollisionExit', new CollisionData({ other: a.actor }));
            }
        }

        this._contacts = current;
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------

    /**
     * Casts a ray and returns the nearest hit, or null.
     *
     * @param {Vector2} origin
     * @param {Vector2} direction Normalised internally.
     * @param {number} [maxDistance=Infinity]
     * @param {object} [options]
     * @param {(collider: Collider2D) => boolean} [options.filter]
     * @param {boolean} [options.hitTriggers=false]
     */
    raycast(origin, direction, maxDistance = Infinity, options = {}) {
        const from = Vector2.from(origin);
        const dir = Vector2.from(direction).normalize();
        let nearest = null;

        for (const collider of this.colliders) {
            if (collider.isTrigger && !options.hitTriggers) continue;
            if (options.filter && !options.filter(collider)) continue;

            const hit = raycastShape(from, dir, maxDistance, collider.getWorldShape());
            if (!hit) continue;
            if (nearest && hit.distance >= nearest.distance) continue;

            nearest = new RaycastHit2D(collider.actor, hit.point, hit.normal, hit.distance, collider);
        }

        return nearest;
    }

    /** Every hit along a ray, nearest first. */
    raycastAll(origin, direction, maxDistance = Infinity, options = {}) {
        const from = Vector2.from(origin);
        const dir = Vector2.from(direction).normalize();
        const hits = [];

        for (const collider of this.colliders) {
            if (collider.isTrigger && !options.hitTriggers) continue;
            if (options.filter && !options.filter(collider)) continue;

            const hit = raycastShape(from, dir, maxDistance, collider.getWorldShape());
            if (hit) hits.push(new RaycastHit2D(collider.actor, hit.point, hit.normal, hit.distance, collider));
        }

        return hits.sort((a, b) => a.distance - b.distance);
    }

    /** Every collider overlapping a circle. */
    overlapCircle(center, radius, options = {}) {
        return this._overlap(new Circle(Vector2.from(center), radius), options);
    }

    /** Every collider overlapping a box. */
    overlapBox(center, size, rotation = 0, options = {}) {
        return this._overlap(Polygon.box(Vector2.from(center), Vector2.from(size), rotation), options);
    }

    /** Every collider containing a point. */
    overlapPoint(point, options = {}) {
        return this._overlap(new Circle(Vector2.from(point), 1e-4), options);
    }

    _overlap(shape, options) {
        const box = shape.aabb;
        const found = [];

        for (const collider of this.colliders) {
            if (collider.isTrigger && !options.hitTriggers) continue;
            if (options.filter && !options.filter(collider)) continue;

            const other = collider.getWorldShape();
            if (!aabbOverlap(box, other.aabb)) continue;
            if (collide(shape, other)) found.push(collider);
        }

        return found;
    }

    /** Forgets every recorded contact, so the next step reports fresh enters. */
    clear() { this._contacts.clear(); }
}

function pairKey(a, b) {
    // Order-independent, so a pair has one identity however the sweep found it.
    return a.actor.id < b.actor.id
        ? `${a.actor.id}:${componentIndex(a)}|${b.actor.id}:${componentIndex(b)}`
        : `${b.actor.id}:${componentIndex(b)}|${a.actor.id}:${componentIndex(a)}`;
}

function componentIndex(collider) {
    return collider.actor.getAllComponents().indexOf(collider);
}

function relativeSpeed(a, b) {
    const va = a.getComponent(Rigidbody2D)?.linearVelocity ?? Vector2.zero;
    const vb = b.getComponent(Rigidbody2D)?.linearVelocity ?? Vector2.zero;
    return Vector2.distance(va, vb);
}

function fire(actor, hook, payload) {
    if (!actor || actor.isDestroyed) return;
    try {
        actor[hook](payload);
    } catch (err) {
        console.error(`[PhysicsSystem2D] ${hook} threw:`, err);
    }
}
