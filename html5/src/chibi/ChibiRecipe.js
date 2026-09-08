// -----------------------------------------------------------------------------
// ChibiRecipe — the thirty-odd numbers and colours that describe a character.
//
// The whole point is that this is small and legible: a `.chibi` file is meant to
// be edited by hand as readily as by the panel, which is why colours are hex
// strings rather than the engine's {R,G,B,A} objects, and why every field has a
// default so `{}` is a valid character.
//
// Mirrored in SexyBiscuit.Engine/Chibi/ChibiRecipe.cs, including the random
// number generator, so a seed names the same character on both engines.
// -----------------------------------------------------------------------------

import { ChibiParts, SLOTS, COLOUR_SLOTS, variantsFor, accessoryNames } from './ChibiRig.js';

/** Proportion multipliers, all 1 at the default build. */
export const PROPORTIONS = Object.freeze([
    'height', 'headSize', 'bodyWidth', 'limbThickness', 'legLength', 'armLength',
]);

/** A proportion below this reads as a bug rather than a style choice. */
export const MIN_PROPORTION = 0.5;
/** And above this the parts pull away from the joints they hang on. */
export const MAX_PROPORTION = 2.0;

const DEFAULT_STYLE = Object.freeze({
    head: 'Round', hair: 'Bob', eyes: 'Dot', body: 'Tunic', legs: 'Trousers', feet: 'Shoes',
});

const DEFAULT_COLOURS = Object.freeze({
    skin: '#F2C6A0', hair: '#4A2E1E', eyes: '#241C18', top: '#5B8C5A',
    bottom: '#3A4A6B', shoes: '#2E2A28', accent: '#D9A441',
});

/** A complete recipe with every field at its default. */
export function defaultRecipe() {
    return {
        version: ChibiParts.version,
        name: 'Chibi',
        seed: 0,
        proportions: Object.fromEntries(PROPORTIONS.map((p) => [p, 1])),
        style: { ...DEFAULT_STYLE },
        colours: { ...DEFAULT_COLOURS },
        accessories: [],
    };
}

function clamp(value, low, high) {
    const n = Number(value);
    if (!Number.isFinite(n)) return 1;
    return Math.min(high, Math.max(low, n));
}

/**
 * Fills in everything the raw object left out and drops anything the part table
 * does not recognise.
 *
 * A style name that no longer exists falls back to the default rather than
 * throwing: renaming a hairstyle should not make every saved character
 * unloadable, and `onWarning` gives the editor somewhere to say so.
 */
export function normalise(raw, onWarning = null) {
    const warn = (message) => { if (onWarning) onWarning(message); };
    const recipe = defaultRecipe();
    if (!raw || typeof raw !== 'object') return recipe;

    if (typeof raw.name === 'string' && raw.name.trim()) recipe.name = raw.name.trim();
    if (Number.isFinite(Number(raw.seed))) recipe.seed = Math.trunc(Number(raw.seed));

    for (const key of PROPORTIONS) {
        if (raw.proportions && raw.proportions[key] !== undefined) {
            recipe.proportions[key] = clamp(raw.proportions[key], MIN_PROPORTION, MAX_PROPORTION);
        }
    }

    for (const slot of SLOTS) {
        const wanted = raw.style?.[slot];
        if (wanted === undefined) continue;
        if (variantsFor(slot).includes(wanted)) recipe.style[slot] = wanted;
        else warn(`'${wanted}' is not a ${slot} style; using '${recipe.style[slot]}'.`);
    }

    for (const slot of COLOUR_SLOTS) {
        const wanted = raw.colours?.[slot];
        if (typeof wanted === 'string' && wanted.trim()) recipe.colours[slot] = wanted.trim();
    }

    for (const entry of raw.accessories ?? []) {
        if (!entry || !accessoryNames().includes(entry.part)) {
            warn(`'${entry?.part}' is not an accessory; skipping it.`);
            continue;
        }
        recipe.accessories.push({
            part: entry.part,
            colour: typeof entry.colour === 'string' ? entry.colour : null,
        });
    }

    return recipe;
}

/** Parses a `.chibi` file's text. Invalid JSON gives the default character. */
export function parse(text, onWarning = null) {
    try {
        return normalise(JSON.parse(text), onWarning);
    } catch (error) {
        if (onWarning) onWarning(`could not parse the recipe: ${error.message}`);
        return defaultRecipe();
    }
}

/** The text to write to a `.chibi` file. */
export function stringify(recipe) {
    return `${JSON.stringify(normalise(recipe), null, 2)}\n`;
}

// -----------------------------------------------------------------------------
// The random character
// -----------------------------------------------------------------------------

/**
 * mulberry32. Chosen because it is four lines of 32-bit integer arithmetic that
 * C# reproduces exactly — `Math.random` would give a different village on every
 * engine and every run, which is the opposite of what a seed is for.
 */
export function seededRandom(seed) {
    let state = seed >>> 0;
    return function next() {
        state = (state + 0x6D2B79F5) >>> 0;
        let t = state;
        t = Math.imul(t ^ (t >>> 15), t | 1) >>> 0;
        t = (t ^ (t + Math.imul(t ^ (t >>> 7), t | 61))) >>> 0;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

/**
 * A coordinated character from one integer.
 *
 * Colours come from the curated palettes rather than from random bytes, because
 * random bytes give forty characters the colour of mud; drawing a top and a
 * bottom as a pair keeps the outfit from fighting itself.
 */
export function random(seed) {
    const next = seededRandom(seed);
    const pick = (list) => list[Math.min(list.length - 1, Math.floor(next() * list.length))];
    const palettes = ChibiParts.palettes;

    const recipe = defaultRecipe();
    recipe.name = `Chibi ${Math.trunc(seed)}`;
    recipe.seed = Math.trunc(seed);

    for (const key of PROPORTIONS) {
        // +/-15%: enough that no two are the same height, little enough that the
        // parts still line up with the joints they hang on.
        recipe.proportions[key] = Math.round((0.85 + next() * 0.3) * 1000) / 1000;
    }

    for (const slot of SLOTS) recipe.style[slot] = pick(variantsFor(slot));

    const outfit = pick(palettes.outfit);
    recipe.colours = {
        skin: pick(palettes.skin),
        hair: pick(palettes.hair),
        eyes: pick(palettes.eyes),
        top: outfit[0],
        bottom: outfit[1],
        shoes: pick(palettes.shoes),
        accent: pick(palettes.accent),
    };

    if (next() < 0.4) recipe.accessories.push({ part: pick(accessoryNames()), colour: null });

    return recipe;
}
