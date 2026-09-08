// -----------------------------------------------------------------------------
// ChibiRig — the joint and socket contract every chibi shares.
//
// A clip authored against one character has to play on every other, so the joint
// names are not an implementation detail: they are the reason animation is
// portable. They come from chibi-parts.json, which the C# side reads through an
// embedded resource, so neither engine can invent a joint the other lacks.
//
// A name ending in '*' stands for a mirrored pair. Expanding it here rather than
// writing both sides into the data keeps L and R from drifting apart.
// -----------------------------------------------------------------------------

import parts from './chibi-parts.json' with { type: 'json' };

/** The raw part table, as loaded. Prefer the named exports below. */
export const ChibiParts = parts;

/** The two sides a mirrored name expands to, left first. */
export const SIDES = Object.freeze(['L', 'R']);

/** True when `name` describes a mirrored pair rather than a single joint. */
export function isMirrored(name) { return name.endsWith('*'); }

/** `Arm*` and `L` give `ArmL`; a name without a star is returned unchanged. */
export function expand(name, side) { return name.replace('*', side); }

/** Every name a mirrored-or-not name stands for, in left-then-right order. */
export function expandAll(name) {
    return isMirrored(name) ? SIDES.map((side) => expand(name, side)) : [name];
}

/**
 * Mirrors an offset for the right-hand side: x flips, and so do the two rotation
 * axes that would otherwise send a mirrored limb the wrong way round.
 */
export function mirrorVector(v, side) {
    return side === 'R' ? [-v[0], v[1], v[2]] : [v[0], v[1], v[2]];
}

/** @see mirrorVector — rotations mirror on their other two axes. */
export function mirrorEuler(r, side) {
    return side === 'R' ? [r[0], -r[1], -r[2]] : [r[0], r[1], r[2]];
}

/** Left, right, or a single unmirrored entry, as the name demands. */
function sidesOf(name) { return isMirrored(name) ? SIDES : [null]; }

function buildJoints() {
    const out = [];
    for (const joint of parts.joints) {
        for (const side of sidesOf(joint.name)) {
            out.push({
                name: side ? expand(joint.name, side) : joint.name,
                parent: side ? expand(joint.parent, side) : joint.parent,
                pos: mirrorVector(joint.pos, side ?? 'L'),
                posScale: joint.posScale ?? null,
            });
        }
    }
    return out;
}

function buildSockets() {
    const out = [];
    for (const socket of parts.sockets) {
        for (const side of sidesOf(socket.name)) {
            out.push({
                name: side ? expand(socket.name, side) : socket.name,
                joint: side ? expand(socket.joint, side) : socket.joint,
                pos: mirrorVector(socket.pos, side ?? 'L'),
            });
        }
    }
    return out;
}

/** Every joint, parents before children, so a builder can attach in one pass. */
export const JOINTS = Object.freeze(buildJoints());

/** Every joint name, in the same order. */
export const JOINT_NAMES = Object.freeze(JOINTS.map((j) => j.name));

/** Every attachment point, named without the `Socket_` prefix the actors carry. */
export const SOCKETS = Object.freeze(buildSockets());

/** Socket actor names as they appear in the hierarchy. */
export const SOCKET_NAMES = Object.freeze(SOCKETS.map((s) => `Socket_${s.name}`));

/** The style slots a recipe chooses from: head, hair, eyes, body, legs, feet. */
export const SLOTS = Object.freeze([...parts.slots]);

/** The colour names a part piece may refer to. */
export const COLOUR_SLOTS = Object.freeze([...parts.colourSlots]);

/** The variant names available for one style slot. */
export function variantsFor(slot) { return Object.keys(parts.parts[slot] ?? {}); }

/** Every accessory name. */
export function accessoryNames() { return Object.keys(parts.accessories); }

/** The root actor's name, and so the name a game looks a chibi up by. */
export const ROOT_NAME = 'Chibi';
