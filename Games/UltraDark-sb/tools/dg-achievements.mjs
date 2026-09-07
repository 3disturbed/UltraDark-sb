// dg-achievements.mjs -- register this game's achievements with the Darks Games hub.
//
//   DG_APP_SECRET=... node tools/dg-achievements.mjs [--check]
//
// The hub refuses a progress report for a key it has no definition for, and the
// refusal is a 404 the game never sees -- `DG.achievement` is fire-and-forget on
// both engines, by design, because a hub that is down must not be able to stall a
// frame. So an achievement that was never registered simply never happens, with
// nothing anywhere to say so.
//
// That is why this file exists and why it is checked in beside the game: these
// definitions ARE the contract, and `--check` asserts that the keys here and the
// keys in Scripts/Social.js are the same set. Run that in the gate; run the
// registration by hand when the list changes.
//
// The upsert is idempotent, so re-running it is how you edit a name or a target.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const gameDir = path.resolve(here, '..');

const APP = 'ultradark-sb';
const HUB = process.env.DG_HUB ?? 'https://darksgames.app';

// `target` is how many increments unlock it; a one-shot leaves it at 1.
// `points` is the hub's own scoring, weighted by how hard the thing is.
export const ACHIEVEMENTS = [
    { key: 'first_blood', name: 'First Blood',
      description: 'Kill something.', points: 5 },

    { key: 'first_boss', name: 'Prime Cut',
      description: 'Bring down your first boss.', points: 15 },

    { key: 'the_dark', name: 'Into the Dark',
      description: 'Reach wave 16, where the lights go out.', points: 25 },

    { key: 'deep_run', name: 'Deep Run',
      description: 'Reach wave 25.', points: 40 },

    { key: 'tenfold', name: 'Tenfold',
      description: 'Hold the multiplier at x10.', points: 30 },

    { key: 'cursed', name: 'Cursed',
      description: 'Take a mod that costs you something.', points: 10 },

    { key: 'ultradark', name: 'The UltraDark',
      description: 'Kill the thing the game is named after.', points: 75 },

    { key: 'full_roster', name: 'Full Roster',
      description: 'Fly all eight pilots.', points: 35, target: 8 },

    { key: 'boss_slayer', name: 'Boss Slayer',
      description: 'Bring down five bosses.', points: 30, target: 5 },

    { key: 'exterminator', name: 'Exterminator',
      description: 'A thousand kills.', points: 50, target: 1000 },
];

// ---------------------------------------------------------------------------
// --check: the two lists are one list
// ---------------------------------------------------------------------------

/** Every achievement key `Scripts/Social.js` can report. */
export function keysInGame() {
    const source = fs.readFileSync(path.join(gameDir, 'Scripts/Social.js'), 'utf8');
    const keys = new Set();
    // The `var A_SOMETHING = "key";` block at the top is the whole list, and it
    // is declared that way so it can be read from out here without running it.
    for (const match of source.matchAll(/^var\s+A_[A-Z_]+\s*=\s*"([a-z0-9_]+)"/gm)) {
        keys.add(match[1]);
    }
    return keys;
}

export function differences() {
    const inGame = keysInGame();
    const inHub = new Set(ACHIEVEMENTS.map((a) => a.key));
    return {
        missingDefinition: [...inGame].filter((k) => !inHub.has(k)),
        neverReported: [...inHub].filter((k) => !inGame.has(k)),
    };
}

// ---------------------------------------------------------------------------

async function main() {
    const { missingDefinition, neverReported } = differences();
    let bad = false;

    for (const key of missingDefinition) {
        console.error(`  the game reports '${key}' and this file does not define it -- every report 404s`);
        bad = true;
    }
    for (const key of neverReported) {
        console.error(`  this file defines '${key}' and the game never reports it -- nobody can earn it`);
        bad = true;
    }
    if (bad) { process.exit(1); }

    console.log(`  ${ACHIEVEMENTS.length} achievements, and the game reports every one of them`);
    if (process.argv.includes('--check')) { return; }

    const secret = process.env.DG_APP_SECRET;
    if (!secret) {
        console.error('  DG_APP_SECRET is not set -- without it the hub cannot tell who is asking.');
        process.exit(1);
    }

    const response = await fetch(`${HUB}/api/v1/social/s2s/achievements`, {
        method: 'PUT',
        headers: {
            'content-type': 'application/json',
            'X-DG-App': APP,
            'X-DG-App-Secret': secret,
        },
        body: JSON.stringify({ achievements: ACHIEVEMENTS }),
    });

    const body = await response.text();
    if (!response.ok) {
        console.error(`  ${response.status} ${body}`);
        process.exit(1);
    }
    console.log(`  registered: ${body}`);
}

await main();
