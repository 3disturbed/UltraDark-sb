// -----------------------------------------------------------------------------
// ChibiBuilder — a recipe becomes a subtree of primitive-mesh actors.
//
// The whole feature rests on one observation: a chibi's proportions hide its
// joints, so nothing has to bend, so nothing needs a skinning shader, a bone
// palette or a model file. A character is a hierarchy of cubes, spheres and
// capsules, which is exactly what both engines can already draw.
//
// A joint actor carries rotation only; the parts hanging off it are separate
// children holding the offset and the scale. That is what puts a limb's pivot at
// the shoulder rather than half-way down the upper arm, and it means animation
// writes nothing but `localEulerAngles` on sixteen actors.
//
// Mirrored in SexyBiscuit.Engine/Chibi/ChibiBuilder.cs.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { Transform3D } from '../core/Transform3D.js';
import { MeshRenderer } from '../rendering/MeshRenderer.js';
import { MeshPrimitive } from '../rendering/PrimitiveMesh.js';
import { Vector3, Color } from '../math/index.js';
import {
    ChibiParts, JOINTS, SOCKETS, SLOTS, ROOT_NAME, isMirrored, expand, mirrorVector, mirrorEuler,
} from './ChibiRig.js';
import { normalise } from './ChibiRecipe.js';

/** Chibis are flat-shaded toys, not car paint. */
const ROUGHNESS = 0.78;

/** Slots authored against the reference head, and so scaled onto the real one. */
const FITTED_TO_HEAD = new Set(['hair', 'eyes']);

/** Sockets on the head, which have to move with it for the same reason. */
const FITTED_SOCKETS = new Set(['Head', 'Face']);

const NO_FIT = [1, 1, 1];

/** One built character: the actor plus the lookups a game and the panel need. */
export class ChibiBuild {
    constructor(actor, recipe) {
        /** @type {Actor} The root. Move this, not the parts. */
        this.actor = actor;
        /** @type {object} The recipe as it was normalised, not as it was passed. */
        this.recipe = recipe;
        /** @type {Map<string, Transform3D>} Joint name to its transform, resolved once. */
        this.joints = new Map();
        /** @type {Map<string, Actor>} Socket name (without the prefix) to its actor. */
        this.sockets = new Map();
        /** @type {Array<{renderer: MeshRenderer, colour: string}>} Parts, by colour slot. */
        this.parts = [];
    }

    /** Repaints every part drawn in one colour slot. */
    setColour(slot, hex) {
        if (!(slot in this.recipe.colours)) return false;
        this.recipe.colours[slot] = hex;
        const colour = Color.from(hex);
        for (const part of this.parts) {
            if (part.colour === slot) part.renderer.albedoColor = colour;
        }
        return true;
    }
}

function proportion(recipe, name) {
    if (!name) return 1;
    return recipe.proportions[name] ?? 1;
}

function makeActor(name, parent) {
    const actor = new Actor(name);
    actor.addComponent(Transform3D);
    // false, not the default: a rig's offsets are already local, so keeping the
    // world transform would drag every joint back to where it was standing.
    if (parent) actor.attachTo(parent, false);
    return actor;
}

/**
 * Adds one primitive piece under `joint`, returning its renderer so the caller
 * can recolour it later without walking the tree again.
 */
function addPiece(piece, jointActor, side, recipe, build, name, colourOverride = null,
                  fit = NO_FIT) {
    const width = proportion(recipe, piece.widthScale);
    const length = proportion(recipe, piece.lengthScale);

    const actor = makeActor(name, jointActor);
    const t = actor.getComponent(Transform3D);

    const pos = mirrorVector(piece.pos, side);
    t.localPosition = new Vector3(
        pos[0] * width * fit[0], pos[1] * length * fit[1], pos[2] * width * fit[2]);
    t.localScale = new Vector3(
        piece.scale[0] * width * fit[0],
        piece.scale[1] * length * fit[1],
        piece.scale[2] * width * fit[2]);
    if (piece.rot) t.localEulerAngles = Vector3.from(mirrorEuler(piece.rot, side));

    const renderer = actor.addComponent(MeshRenderer);
    renderer.setPrimitive(MeshPrimitive[piece.mesh] ?? MeshPrimitive.Cube);
    renderer.albedoColor = Color.from(colourOverride ?? recipe.colours[piece.colour] ?? '#FFFFFF');
    renderer.roughness = ROUGHNESS;

    // An overridden accessory colour is not in any slot, so setColour must not
    // repaint it later when the slot it borrowed its name from changes.
    build.parts.push({ renderer, colour: colourOverride ? null : piece.colour });
    return actor;
}

/**
 * Builds a character.
 *
 * @param {object} recipe A recipe, raw or already normalised.
 * @param {(message: string) => void} [onWarning] Told about anything dropped.
 * @returns {ChibiBuild}
 */
export function build(recipe, onWarning = null) {
    const r = normalise(recipe, onWarning);

    const root = new Actor(r.name || ROOT_NAME);
    root.addComponent(Transform3D).localScale = new Vector3(
        r.proportions.height, r.proportions.height, r.proportions.height);

    const result = new ChibiBuild(root, r);
    const actors = new Map([['', root]]);
    const headFit = ChibiParts.headFit?.[r.style.head] ?? NO_FIT;
    // Eyes get their own fit: it carries how far forward that head's face is, which
    // differs by shape in a way the skull's proportions alone do not describe.
    const eyeFit = ChibiParts.headEyeFit?.[r.style.head] ?? headFit;

    // ---- Joints, parents first so every attach finds its parent -------------
    for (const joint of JOINTS) {
        const parent = actors.get(joint.parent) ?? root;
        const actor = makeActor(joint.name, parent);
        const scale = proportion(r, joint.posScale);
        actor.getComponent(Transform3D).localPosition = new Vector3(
            joint.pos[0], joint.pos[1] * scale, joint.pos[2]);

        actors.set(joint.name, actor);
        result.joints.set(joint.name, actor.getComponent(Transform3D));
    }

    // ---- Sockets: empty actors a game hangs a hat or a sword from -----------
    for (const socket of SOCKETS) {
        const actor = makeActor(`Socket_${socket.name}`, actors.get(socket.joint) ?? root);
        const fit = FITTED_SOCKETS.has(socket.name) ? headFit : NO_FIT;
        actor.getComponent(Transform3D).localPosition = new Vector3(
            socket.pos[0] * fit[0], socket.pos[1] * fit[1], socket.pos[2] * fit[2]);
        result.sockets.set(socket.name, actor);
    }

    // ---- The parts themselves ----------------------------------------------
    for (const slot of SLOTS) {
        const pieces = ChibiParts.parts[slot]?.[r.style[slot]] ?? [];
        const fit = slot === 'eyes' ? eyeFit : (FITTED_TO_HEAD.has(slot) ? headFit : NO_FIT);
        for (const piece of pieces) {
            for (const side of isMirrored(piece.joint) ? ['L', 'R'] : [null]) {
                const jointName = side ? expand(piece.joint, side) : piece.joint;
                const jointActor = actors.get(jointName);
                if (!jointActor) {
                    if (onWarning) onWarning(`${slot}/${r.style[slot]} names joint '${jointName}', which the rig has not got.`);
                    continue;
                }
                addPiece(piece, jointActor, side ?? 'L', r, result,
                    `${slot}_${piece.mesh}`, null, fit);
            }
        }
    }

    // ---- Accessories, which hang off sockets rather than joints -------------
    for (const entry of r.accessories) {
        for (const piece of ChibiParts.accessories[entry.part] ?? []) {
            for (const side of isMirrored(piece.socket) ? ['L', 'R'] : [null]) {
                const name = side ? expand(piece.socket, side) : piece.socket;
                const socket = result.sockets.get(name);
                if (!socket) {
                    if (onWarning) onWarning(`accessory '${entry.part}' names socket '${name}', which the rig has not got.`);
                    continue;
                }
                // A hat is placed relative to the socket, which has already moved
                // with the head, so only its size needs fitting -- and only on the
                // head, where a mismatch is a hat that does not sit on the skull.
                const fit = FITTED_SOCKETS.has(name) ? headFit : NO_FIT;
                addPiece(piece, socket, side ?? 'L', r, result,
                    `${entry.part}_${piece.mesh}`, entry.colour, fit);
            }
        }
    }

    return result;
}
