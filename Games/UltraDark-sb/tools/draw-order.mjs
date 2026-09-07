// draw-order.mjs -- read the scene the way the renderer will, and fail if any
// layer is in the wrong place.
//
// This is the check that no engine gate can be: every test in the repository
// reads text, and a draw order is a picture. A game laid out against the wrong
// layerDepth direction renders as a flat rectangle with every script running,
// every test green and the validator saying OK.
//
// In UltraDark it is worse than cosmetic, because the dark is a sprite. The
// overlay at 0.80 dims everything drawn before it and nothing drawn after it,
// so which side of that line a thing sits on is a design decision:
//
//     below 0.80   the world -- floor, walls, enemies, the ship, projectiles
//     0.80         the dark
//     above 0.80   the light -- muzzle flash, explosions, rings; and the HUD
//
// Put an explosion under the overlay and the only light in a wave-25 fight is
// dimmed by the darkness it exists to push back. Put the floor over the game and
// there is no game. Both are silent.
//
//     node tools/draw-order.mjs

import { boot } from './harness.mjs';

const DARK = 0.80;

// What each actor is for, and which side of the dark it belongs on.
// A name not listed here is reported rather than ignored -- an unclassified
// sprite is a sprite nobody decided about.
const EXPECT = [
    { match: /^Floor$/,            band: 'world', order: 0,  what: 'the arena floor' },
    { match: /^Grid$/,             band: 'world', order: 1,  what: 'grid lines' },
    { match: /^Wall$/,             band: 'world', order: 2,  what: 'the boundary' },
    { match: /^Pickup$/,           band: 'world', order: 3,  what: 'cores and consumables' },
    { match: /^Hangar\d+$/,        band: 'world', order: 3,  what: 'the hangar ships' },
    { match: /^Zone$/,             band: 'world', order: 4,  what: "the shepherd's dark zones" },
    { match: /^Enemy$/,            band: 'world', order: 5,  what: 'enemies' },
    { match: /^Tell$/,             band: 'world', order: 6,  what: 'sniper telegraphs' },
    { match: /^(Pylon|Turret)$/,   band: 'world', order: 7,  what: 'deployables' },
    { match: /^Boss$/,             band: 'world', order: 8,  what: 'the boss' },
    { match: /^Player$/,           band: 'world', order: 9,  what: 'the pilot' },
    { match: /^PilotNose$/,        band: 'world', order: 10, what: "the pilot's nose" },
    { match: /^Blade$/,            band: 'world', order: 11, what: 'orbital blades' },
    { match: /^ShotEnemy$/,        band: 'world', order: 12, what: 'enemy projectiles' },
    { match: /^Shot$/,             band: 'world', order: 13, what: 'player projectiles' },

    { match: /^NightOverlay$/,     band: 'dark',  order: 14, what: 'THE DARK' },

    { match: /^Fx$/,               band: 'light', order: 15, what: 'muzzle flash and explosions' },
];

// The HUD and the draft board are not in this ladder any more, and that is the
// point: they are screen-space UI now, drawn by the UI layer above every sprite
// there is. What used to need a place in the depth order -- five bars over the
// pilot's head, three cards and their pips -- needs none, and cannot be put on
// the wrong side of the dark by accident.

function classify(name) {
    for (const row of EXPECT) { if (row.match.test(name)) { return row; } }
    return null;
}

const g = boot();
const problems = [];
// boot() captures console.log so a script's log() can be read back, so the
// report is collected here and printed once the real console is restored.
const report = [];

try {
    // Drive the game into the busiest state it has: a late wave, so the dark is
    // on; a boss, so its rings exist; a draft open, so the cards are up.
    g.director().invoke('forceLaunch');
    await g.step(10);
    g.director().invoke('forceWave', 20);
    await g.step(60);

    // One of everything the swarm can make, so no kind is missed.
    for (let k = 0; k < 12; k++) { g.swarm().invoke('spawnKind', k, 200 + k * 40, 300, 1, 1); }
    g.pilot().invoke('addMod', 8);                       // an orbital blade
    g.director().invoke('spawnPylon', 100, 100, 20, 5);
    g.director().invoke('spawnTurret', -100, 100, 20, 5);
    g.director().invoke('spawnEffect', 0, 0, 40, 255, 255, 255, 5);
    g.director().invoke('onBossKilled', 0, 0, 0);        // drops pickups, deterministically
    g.bullets().invoke('fire', 0, 0, 0, 300, 1, 0, 4, 5, 0, 0);
    g.bullets().invoke('fire', 0, 0, 3, 300, 1, 1, 4, 5, 0, 0);
    g.board().invoke('setHeading', 'DRAFT');
    g.board().invoke('open', 3);
    for (let i = 0; i < 3; i++) { g.board().invoke('setCard', i, 'TEST', 'a line', '', '#ffffff'); }
    await g.step(120);

    // ----------------------------------------------------------------------
    // Read every sprite the way SpriteBatch will
    // ----------------------------------------------------------------------
    const seen = new Map();          // name-class -> { depth, what, band, order }
    const unclassified = new Set();

    for (const actor of g.scene.allActors) {
        if (actor.isDestroyed) { continue; }
        for (const c of actor.getAllComponents()) {
            if (c.constructor.name !== 'SpriteRenderer') { continue; }

            const row = classify(actor.name);
            if (!row) { unclassified.add(actor.name); continue; }

            const depth = c.layerDepth;
            const prior = seen.get(row.what);
            if (prior && Math.abs(prior.depth - depth) > 1e-9) {
                problems.push(`${row.what} is drawn at two different depths (${prior.depth} and ${depth})`);
            }
            seen.set(row.what, { depth, ...row });
        }
    }

    const rows = [...seen.values()].sort((a, b) => a.order - b.order);

    report.push('--- UltraDark draw order ---------------------------------');
    for (const r of rows) {
        const side = r.depth < DARK ? 'dimmed by the dark'
                   : r.depth > DARK ? 'bright through the dark'
                   : '<< THE DARK >>';
        report.push(`  ${r.depth.toFixed(2)}  ${r.what.padEnd(30)} ${side}`);
    }

    // ----------------------------------------------------------------------
    // The rules
    // ----------------------------------------------------------------------

    // 1. Higher is nearer, and the listed order is the intended order.
    for (let i = 1; i < rows.length; i++) {
        if (rows[i].depth < rows[i - 1].depth) {
            problems.push(`${rows[i].what} (${rows[i].depth}) is behind ${rows[i - 1].what} (${rows[i - 1].depth}), which is the wrong way round`);
        }
    }

    // 2. Nothing in the world may sit on or over the dark, and nothing that is
    //    meant to be a light may sit under it.
    for (const r of rows) {
        if (r.band === 'world' && r.depth >= DARK) {
            problems.push(`${r.what} at ${r.depth} is at or above the dark, so the night will never dim it`);
        }
        if (r.band === 'light' && r.depth <= DARK) {
            problems.push(`${r.what} at ${r.depth} is under the dark, so the only light in a late wave is dimmed by it`);
        }
        if (r.band === 'dark' && Math.abs(r.depth - DARK) > 1e-9) {
            problems.push(`the overlay is at ${r.depth}, not the ${DARK} every other layer is placed against`);
        }
    }

    // 3. The floor is the back of the world. Anything under it is invisible.
    const floor = rows.find((r) => r.what === 'the arena floor');
    for (const r of rows) {
        if (r !== floor && floor && r.depth <= floor.depth) {
            problems.push(`${r.what} at ${r.depth} is at or behind the floor at ${floor.depth}`);
        }
    }

    // 4. Everything that draws must have been decided about.
    for (const name of unclassified) {
        problems.push(`'${name}' draws a sprite and is not in the ladder -- classify it or stop drawing it`);
    }

    if (!floor) { problems.push('no floor was drawn at all'); }
    if (!rows.find((r) => r.what === 'THE DARK')) { problems.push('the dark overlay was never built'); }

    // 5. The HUD and the board must NOT be sprites. If one comes back into the
    //    world it is a regression to the era before the contract had a viewport.
    for (const name of ['BarBack0', 'Card0', 'Pip0_0', 'NightOverlay']) {
        const actor = g.scene.allActors.find((a) => a.name === name && !a.isDestroyed);
        if (name === 'NightOverlay') { continue; }
        if (actor) { problems.push(`'${name}' is a world sprite again -- the HUD and the board are screen-space UI now`); }
    }

} finally {
    g.restore();
}

for (const line of report) { console.log(line); }

if (problems.length > 0) {
    console.log('');
    for (const p of problems) { console.log(`  FAIL: ${p}`); }
    process.exit(1);
}

console.log('  OK -- every layer is on the side of the dark it was meant to be on');
