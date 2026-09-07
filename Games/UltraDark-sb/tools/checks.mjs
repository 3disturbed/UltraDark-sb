// checks.mjs -- the game's own tests. `node --test tools/checks.mjs`
//
// The validator proves a script is inside the contract and `npm test` proves
// the engine works. Neither knows what UltraDark is. These do: they play real
// situations through the real scene and assert on what came out.
//
// Every one of these exists because it can fail silently. A boss that cannot be
// damaged, a mod list that deduplicates, a darkness that never arrives and a
// draw order that puts the ground over the game all pass `validate --strict`.

import test from 'node:test';
import assert from 'node:assert/strict';
import { boot } from './harness.mjs';

// ---------------------------------------------------------------------------
// Waves
// ---------------------------------------------------------------------------

test('a launched run reaches wave 1 and spawns something to shoot', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(240);
        assert.equal(g.director().invoke('getWave'), 1);
        assert.ok(g.swarm().invoke('alive') > 0, 'no enemy spawned in four seconds of wave 1');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('clearing a wave opens the draft, and the draft always resolves', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);

        g.director().invoke('forceWave', 1);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        await g.step(10);

        // Nothing is pressed: the intermission has to time out into a pick, or a
        // run can stall for ever on a card nobody chose.
        assert.equal(g.director().invoke('getPhase'), 2, 'an empty wave did not open the draft');
        await g.step(60 * 26);      // the intermission is 20s, from WAVE.INTERMISSION_S
        assert.ok(g.director().invoke('getWave') > 1, 'the draft never resolved on its own');
        assert.ok(g.pilot().invoke('modCount') > 0, 'resolving the draft granted no mod');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// Damage -- the path from a projectile to a dead thing
// ---------------------------------------------------------------------------

test('a projectile kills an enemy, which is the whole game', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');

        // One grunt, dead ahead of a shot along +x.
        g.swarm().invoke('spawnKind', 0, 300, 0, 1, 1);
        await g.step(2);
        assert.equal(g.swarm().invoke('alive'), 1);

        const player = g.find('Player');
        player.transform.x = 0;
        player.transform.y = 0;
        g.bullets().invoke('fire', 100, 0, 0, 900, 500, 0, 5, 2, 0, 0);

        await g.step(40);
        assert.equal(g.swarm().invoke('alive'), 0,
            'the shot passed through the enemy -- overlapCircle or damageActor is broken');
    } finally { g.restore(); }
});

test('a boss spawns, announces itself, and can be killed', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 5);
        await g.step(90);

        assert.ok(g.said('BRUTE PRIME') > 0, 'the boss never announced itself -- configure did not arrive');
        const boss = g.find('Boss');
        assert.ok(boss, 'no boss actor on a boss wave');

        const script = boss.getComponents(await import('./harness.mjs').then((m) => m.ScriptComponent))[0];
        assert.ok(script.invoke('getHealth01') > 0.99, 'the boss did not start at full health');

        // FOUNDRY is the only boss with an invulnerable window, and this is not it.
        script.invoke('takeDamage', 999999, 0);
        await g.step(10);

        assert.ok(g.said('BOSS DOWN') > 0, 'the boss took lethal damage and did not die');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('a boss dies to ordinary gunfire, not just to a direct call', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 5);
        await g.step(90);

        const boss = g.find('Boss');
        assert.ok(boss, 'no boss actor');

        // Park the boss and shoot it, which is the path a player actually uses:
        // Physics.overlapCircle has to see the boss collider and Bullets has to
        // find its script by tag.
        for (let i = 0; i < 400; i++) {
            boss.transform.x = 0;
            boss.transform.y = 0;
            g.bullets().invoke('fire', -60, 0, 0, 900, 40, 0, 6, 0.5, 0, 0);
            await g.step(4);
            if (g.said('BOSS DOWN') > 0) { break; }
        }

        assert.ok(g.said('BOSS DOWN') > 0,
            'four hundred shots into a stationary boss did not kill it');
    } finally { g.restore(); }
});

test('an enemy standing on the pilot can still be shot', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);

        // Every pilot, with the target already in contact. A muzzle offset puts
        // the first frame of a shot past whatever is touching the ship, and that
        // enemy is then immune to the gun pointed straight at it while it chews
        // through the pilot. It is invisible on the fast pilots, because they
        // kill things before the things arrive.
        for (let p = 0; p < 8; p++) {
            g.director().invoke('forceWave', 1);
            g.director().invoke('forceBudget', 0);
            g.pilot().invoke('setPilot', p);
            g.swarm().invoke('clearAll');

            const ship = g.find('Player');
            ship.transform.x = 0;
            ship.transform.y = 0;
            g.swarm().invoke('spawnKind', 0, 2, 0, 1, 0.001);   // a Drone, right on top
            await g.step(2);

            g.mouse(0, true);
            let killed = false;
            for (let f = 0; f < 40 && !killed; f++) {
                g.pilot().invoke('heal', 999);
                const e = g.byTag('Enemy')[0];
                if (e) { e.transform.x = 2; e.transform.y = 0; }   // hold it in contact
                await g.step(10);
                if (g.swarm().invoke('alive') === 0) { killed = true; }
            }
            g.mouse(0, false);

            assert.ok(killed,
                `pilot ${p} (${g.pilot().invoke('getPilotName')}) could not shoot an enemy standing on it`);
        }
    } finally { g.restore(); }
});

test('a fast projectile cannot pass through an enemy', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);

        // HAWK's railgun is the fastest thing in the game. At 1750 px/s a frame
        // is 29 px and a grunt's capture window is 24, so an unswept projectile
        // steps over it every time -- at every range, silently.
        for (const speed of [400, 900, 1750, 4000]) {
            g.director().invoke('forceWave', 1);
            g.director().invoke('forceBudget', 0);
            g.swarm().invoke('clearAll');
            g.swarm().invoke('spawnKind', 0, 380, 0, 1, 0);
            await g.step(2);

            // Two seconds and 380 px: comfortably enough for the slowest speed
            // here, so a failure is a miss and never a shot still in flight.
            g.bullets().invoke('fire', 0, 0, 0, speed, 9999, 0, 5, 2.5, 0, 0);
            await g.step(150);

            assert.equal(g.swarm().invoke('alive'), 0,
                `a projectile at ${speed} px/s passed straight through a grunt`);
        }
    } finally { g.restore(); }
});

test('auto-aim finds the boss, which is not in the swarm ledger', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 5);
        await g.step(60);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        await g.step(4);

        const boss = g.find('Boss');
        assert.ok(boss, 'no boss to aim at');
        boss.transform.x = 300;
        boss.transform.y = 0;
        await g.step(2);

        // Auto-aim is the default aim mode, and it reads the swarm's nearest
        // target. A boss it cannot see is a boss the pilot cannot shoot.
        const x = g.swarm().invoke('nearestX', 0, 0, 620);
        assert.ok(x > -900000, 'auto-aim found nothing with a boss right in front of it');
        assert.ok(Math.abs(x - boss.transform.x) < 60, `auto-aim pointed at ${x}, not the boss`);
    } finally { g.restore(); }
});

test('a pilot on auto-aim can kill a boss with no adds on the field', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 5);
        await g.step(60);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');

        g.mouse(0, true);
        for (let i = 0; i < 120; i++) {
            g.pilot().invoke('heal', 9999);      // the question is damage, not survival
            const boss = g.find('Boss');
            if (boss) { boss.transform.x = 200; boss.transform.y = 0; }
            const p = g.find('Player');
            if (p) { p.transform.x = 0; p.transform.y = 0; }
            await g.step(30);
            if (g.said('BOSS DOWN') > 0) { break; }
        }
        g.mouse(0, false);

        assert.ok(g.said('BOSS DOWN') > 0,
            'a minute of held fire on auto-aim did not kill a wave-5 boss');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// The screen. All of this used to be world-space sprites and a log line.
// ---------------------------------------------------------------------------

const uiTexts = (g) => g.ui.elements.filter((e) => e.visible && e.text).map((e) => String(e.text));

test('the HUD reads the run, not just the pilot', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);
        g.director().invoke('forceWave', 7);
        await g.step(10);

        const texts = uiTexts(g);
        assert.ok(texts.includes('WAVE'), 'no WAVE row on the HUD');
        assert.ok(texts.includes('SCORE'), 'no SCORE row on the HUD');
        assert.ok(texts.includes('CORES'), 'no CORES row on the HUD');
        assert.ok(texts.includes('HULL'), 'no hull bar on the HUD');

        // The stat rows come off the Director by tag, not forwarded through the
        // pilot, so this is what proves the `from` wiring works at all.
        assert.ok(texts.includes('7'), `the wave row does not show the wave: ${texts.join(' ')}`);
    } finally { g.restore(); }
});

test('the HUD title becomes the pilot who launched', async () => {
    const g = boot();
    try {
        await g.step(5);
        g.pilot().invoke('setPilot', 1);          // BLAZE
        g.director().invoke('forceLaunch');
        await g.step(10);
        assert.ok(uiTexts(g).includes('BLAZE'), 'the HUD still says something else');
    } finally { g.restore(); }
});

test('a draft card says what it is, in words', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 1);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        await g.step(20);

        assert.equal(g.director().invoke('getPhase'), 2, 'the draft did not open');

        const texts = uiTexts(g);
        assert.ok(texts.includes('DRAFT'), 'the board has no heading');

        // Every card carries a real mod name and a real description. Before the
        // UI globals this was a colour and a row of pips.
        const names = ['Piercer', 'Ricochet', 'Splitter', 'Heavy Rounds', 'Overclock',
            'Railshot', 'Long Barrel', 'Railgun Coils', 'Gunslinger', 'Heavyweight',
            'Orbital', 'Twin Orbital', 'Dash Nova', 'Nova Core', 'Static Coil',
            'Thorn Plating', 'Yield Boost', 'Thrusters', 'Twin Dash', 'Featherframe',
            'Plating', 'Overshield', 'Sprinter', 'Bounty Chip', 'Volatile', 'Shrapnel',
            'Bloodrush', 'Momentum', 'Kill Streak', 'Grudge Core', 'Adrenal Loop',
            'Scavenger', 'Glass Cannon', 'Berserker', 'Scattergun', 'Turtle Shell',
            "Gambler's Coil"];
        const shown = texts.filter((t) => names.includes(t));
        assert.equal(shown.length, 3, `expected three named cards, saw ${shown.length}: ${texts.join(' | ')}`);
    } finally { g.restore(); }
});

test('clicking a draft card takes it', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 1);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        await g.step(20);
        assert.equal(g.director().invoke('getPhase'), 2, 'the draft did not open');

        const before = g.pilot().invoke('modCount');

        // The middle card sits on the centre of the viewport. A click is a
        // release inside the element that also went down inside it.
        g.pointAt(1280 / 2, 720 / 2);
        g.pointerDown(true);
        await g.step(2);
        g.pointerDown(false);
        await g.step(3);

        assert.ok(g.pilot().invoke('modCount') > before, 'clicking the card granted nothing');
        assert.notEqual(g.director().invoke('getPhase'), 2, 'the board is still open after a pick');
    } finally { g.restore(); }
});

test('escape pauses the run and escape again lets it go', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(60);
        const wave = g.director().invoke('getWave');

        g.press('Escape');
        await g.step(2);
        assert.ok(uiTexts(g).includes('PAUSED'), 'no pause overlay');

        // Paused means the Director stops working, not just that a panel is up.
        const enemiesWhenPaused = g.swarm().invoke('alive');
        await g.step(240);
        assert.equal(g.director().invoke('getWave'), wave, 'the wave moved on while paused');
        assert.equal(g.swarm().invoke('alive'), enemiesWhenPaused, 'the arena kept filling while paused');

        g.press('Escape');
        await g.step(2);
        assert.ok(!uiTexts(g).includes('PAUSED'), 'the overlay stayed up');
    } finally { g.restore(); }
});

test('a dead pilot gets an overlay saying so, and R clears it', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);
        for (let i = 0; i < 30; i++) { g.pilot().invoke('hurt', 999); await g.step(40); }

        assert.equal(g.director().invoke('getPhase'), 4);
        assert.ok(uiTexts(g).includes('RUN OVER'), 'no game-over overlay');

        g.press('R');
        await g.step(4);
        assert.ok(uiTexts(g).includes('HANGAR'), 'R did not return to the hangar');
    } finally { g.restore(); }
});

test('pausing is refused where it would trap the player', async () => {
    const g = boot();
    try {
        await g.step(10);
        // The hangar and the game-over screen already own the overlay; pausing
        // one of them would replace the only thing telling the player what to do.
        assert.ok(uiTexts(g).includes('HANGAR'));
        g.press('Escape');
        await g.step(3);
        assert.ok(uiTexts(g).includes('HANGAR'), 'Escape replaced the hangar overlay');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// Mods -- stacking is the design, and deduplication would quietly remove it
// ---------------------------------------------------------------------------

test('mods stack: the same mod taken twice counts twice', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(5);

        const before = g.pilot().invoke('getDamageMul');
        g.pilot().invoke('addMod', 3);          // Heavy Rounds, x1.6 damage
        const once = g.pilot().invoke('getDamageMul');
        g.pilot().invoke('addMod', 3);
        const twice = g.pilot().invoke('getDamageMul');

        assert.ok(once > before, 'one Heavy Rounds changed nothing');
        assert.ok(twice > once, 'a second Heavy Rounds was deduplicated away');
        assert.ok(Math.abs(twice - before * 1.6 * 1.6) < 1e-9,
            'stacking is not multiplicative as computeStats declares');
        assert.equal(g.pilot().invoke('modCount'), 2);
    } finally { g.restore(); }
});

test('the pilot never reads an undefined stat, however start order falls', async () => {
    const g = boot();
    try {
        // The very first frame: the pilot has found the Upgrades actor but that
        // script may not have initialised, and a call into it returns undefined.
        // Anything derived from that is NaN, silently, for the rest of the run --
        // no error, no log line, and a weapon that does no damage.
        await g.step(1);

        for (const getter of ['getDamageMul', 'getHealth01', 'getShield01',
                              'getAbility01', 'getDash01', 'getConsumable01',
                              'getCoreBonus', 'getMagnet', 'getShockwave', 'getHp']) {
            const v = Number(g.pilot().invoke(getter));
            assert.ok(v === v, `${getter} is NaN on the first frame`);
        }
        assert.equal(g.pilot().invoke('getDamageMul'), 1, 'damage does not start at x1');
    } finally { g.restore(); }
});

test('taking a mod that raises max health also grants the health', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(5);
        // Plating is +1 max HP, because the pilot has three. Everything in
        // UltraDark is on a small integer scale.
        const before = g.pilot().invoke('getHp');
        g.pilot().invoke('addMod', 20);         // Plating, +1 max
        assert.equal(g.pilot().invoke('getHp'), before + 1);
        assert.ok(g.pilot().invoke('getHealth01') > 0.99, 'Plating left the pilot on a partial bar');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// The dark -- the title
// ---------------------------------------------------------------------------

test('the dark is off early, arrives on wave 16, and never exceeds full', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(5);
        const d = g.director();

        d.invoke('forceWave', 5);
        assert.equal(d.invoke('getDarkness'), 0, 'wave 5 is not supposed to be dark');

        d.invoke('forceWave', 14);
        const warn = d.invoke('getDarkness');
        assert.ok(warn > 0 && warn < 0.5, `wave 14 should be a warning, got ${warn}`);

        d.invoke('forceWave', 16);
        assert.ok(d.invoke('getDarkness') >= 0.6, 'wave 16 is the title and must be properly dark');

        d.invoke('forceWave', 90);
        assert.ok(d.invoke('getDarkness') <= 1.0, 'darkness ran past full');
    } finally { g.restore(); }
});

test('a boss darkness override is always given back', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(5);
        const d = g.director();

        d.invoke('forceWave', 2);
        d.invoke('setDarkOverride', 0.95);
        assert.ok(d.invoke('getDarkness') > 0.9);

        d.invoke('setDarkOverride', -1);
        assert.equal(d.invoke('getDarkness'), 0,
            'releasing the override left the arena black on an early wave');
    } finally { g.restore(); }
});

test('the dark overlay exists and follows the pilot', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        g.director().invoke('forceWave', 20);
        await g.step(60);

        const overlay = g.find('NightOverlay');
        assert.ok(overlay, 'the day-night cookie built no overlay');

        const player = g.find('Player');
        player.transform.x = 700;
        player.transform.y = -400;
        await g.step(3);

        assert.ok(Math.abs(overlay.transform.x - 700) < 1, 'the dark did not follow the pilot');
        assert.ok(Math.abs(overlay.transform.y + 400) < 1, 'the dark did not follow the pilot');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// Endless -- there is no wave at which the run is over
// ---------------------------------------------------------------------------

test('bosses cycle for ever and none of them is a victory', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(5);
        const d = g.director();

        // Every fifth wave is a boss, at wave 5 and at wave 500 alike.
        for (const w of [5, 10, 25, 30, 200, 500]) {
            d.invoke('forceWave', w);
            await g.step(2);          // Scene.createActor is pending until a flush
            assert.ok(g.find('Boss'), `wave ${w} is a multiple of five and produced no boss`);
            const boss = g.find('Boss');
            boss.destroy();
            await g.step(2);
        }

        d.invoke('forceWave', 300);
        assert.notEqual(d.invoke('getPhase'), 4, 'a high wave ended the run by itself');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// Enemies whose behaviour is a rule, not a number
// ---------------------------------------------------------------------------

test('a phased ghost cannot be shot, and can be again when it returns', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');

        g.swarm().invoke('spawnKind', 10, 900, 500, 1, 1);   // a Ghost   // far from the pilot, inside the arena
        await g.step(2);
        assert.equal(g.swarm().invoke('alive'), 1);

        // nearestX skips a phased ghost, which is how aiming knows to ignore it.
        let sawTargetable = false;
        let sawUntargetable = false;
        for (let i = 0; i < 60 * 8; i++) {
            await g.step(1);
            const x = g.swarm().invoke('nearestX', 900, 500, 600);
            if (x > -900000) { sawTargetable = true; } else { sawUntargetable = true; }
            if (sawTargetable && sawUntargetable) { break; }
        }

        assert.ok(sawTargetable, 'the ghost was never targetable');
        assert.ok(sawUntargetable, 'the ghost never phased out -- it is just a grunt');
    } finally { g.restore(); }
});

test('area damage does not reach a phased ghost', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        g.swarm().invoke('spawnKind', 10, 900, 500, 1, 1);   // a Ghost

        // Wait for the phase-out, then try to nuke it.
        for (let i = 0; i < 60 * 8; i++) {
            await g.step(1);
            if (g.swarm().invoke('nearestX', 900, 500, 600) <= -900000) { break; }
        }
        const hits = g.swarm().invoke('damageCircle', 900, 500, 400, 99999, 1);
        assert.equal(hits, 0, 'a phased ghost was hit by area damage it should have been immune to');
        assert.equal(g.swarm().invoke('alive'), 1);
    } finally { g.restore(); }
});

test('an enemy that dies to a burn does not take the swarm down with it', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');

        // Burn is applied by a projectile carrying code bit 2, and it ticks on a
        // half-second cadence inside the swarm's own update. An enemy dying on
        // one of those ticks removes its row mid-frame, and everything after it
        // in step() then reads a transform off nothing.
        for (let i = 0; i < 12; i++) {
            g.swarm().invoke('spawnKind', i % 12, 300 + i * 30, 200, 0.02, 1);
        }
        await g.step(2);

        for (let i = 0; i < 40; i++) {
            g.bullets().invoke('fire', 0, 200, 0, 1200, 1, 0, 6, 2, 99, 2);
            await g.step(6);
        }
        await g.step(240);

        assert.equal(g.errors.length, 0, g.errors.slice(0, 3).join('\n'));
    } finally { g.restore(); }
});

test('a stacked shockwave chain does not blow the stack', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');

        // SHOCKWAVE makes a kill explode, and that explosion kills. Taken
        // directly it is kill -> damageCircle -> applyDamage -> kill, one stack
        // frame per link, and a dense pack of weak enemies overflows it. What
        // that looks like is not a crash: it is every script hook in the frame
        // failing afterwards, so damage silently stops landing and a boss never
        // dies. It reads exactly like a balance problem.
        for (let i = 0; i < 6; i++) { g.pilot().invoke('addMod', 24); }   // Volatile

        for (let i = 0; i < 80; i++) {
            g.swarm().invoke('spawnKind', 1, 200 + (i % 10) * 12, 200 + Math.floor(i / 10) * 12, 0.02, 0.001);   // Mites
        }
        await g.step(4);
        assert.ok(g.swarm().invoke('alive') > 40, 'the test pack did not spawn');

        g.swarm().invoke('damageCircle', 240, 240, 60, 9999, 1);
        await g.step(30);

        assert.equal(g.errors.length, 0, g.errors.slice(0, 3).join('\n'));
        assert.ok(g.swarm().invoke('alive') < 40, 'the chain reaction did not propagate at all');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// Pools -- the reason the game can afford what it does
// ---------------------------------------------------------------------------

test('the projectile pool is reused rather than grown', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);

        for (let round = 0; round < 6; round++) {
            for (let i = 0; i < 40; i++) {
                g.bullets().invoke('fire', 0, 0, i, 600, 1, 0, 4, 0.1, 0, 0);
            }
            await g.step(30);
        }

        const shots = g.byTag('Shot').length;
        assert.ok(shots <= 90, `two hundred and forty shots left ${shots} actors behind`);
        assert.equal(g.bullets().invoke('count'), 0, 'projectiles outlived their lifetime');
    } finally { g.restore(); }
});

test('a cleared run leaves no enemies behind', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(180);
        g.director().invoke('forceBudget', 0);
        assert.ok(g.byTag('Enemy').length > 0, 'nothing to clear');

        g.swarm().invoke('clearAll');
        await g.step(3);

        assert.equal(g.swarm().invoke('alive'), 0);
        assert.equal(g.byTag('Enemy').length, 0, 'enemy actors survived clearAll');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// The pilot
// ---------------------------------------------------------------------------

test('every one of the eight pilots flies and fires', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);

        for (let p = 0; p < 8; p++) {
            // A fresh wave 1 each time: killing the last pilot's target ends the
            // wave and opens the draft, and a pilot in the draft is frozen.
            g.director().invoke('forceWave', 1);
            g.director().invoke('forceBudget', 0);
            g.pilot().invoke('setPilot', p);
            g.swarm().invoke('clearAll');

            // Park the pilot: it drifts between iterations, and a target that
            // lands outside auto-aim range makes this test fail on whichever
            // pilot happened to be next.
            const ship = g.find('Player');
            ship.transform.x = 0;
            ship.transform.y = 0;

            g.swarm().invoke('spawnKind', 0, 130, 0, 2, 0.001);   // a Drone, held still
            await g.step(2);

            const before = g.swarm().invoke('alive');
            assert.equal(before, 1, 'the test target did not spawn');

            g.mouse(0, true);
            let killed = false;
            for (let f = 0; f < 30 && !killed; f++) {
                // Topped up every ten frames: this test is about whether the
                // weapon deals damage, and HAWK has the least health in the game.
                g.pilot().invoke('heal', 999);
                await g.step(10);
                // Checked as it happens, not at the end: a pilot that kills
                // quickly clears the wave, and the intermission then starts wave
                // 2 underneath the assertion.
                if (g.swarm().invoke('alive') === 0) { killed = true; }
            }
            g.mouse(0, false);

            assert.ok(killed,
                `pilot ${p} (${g.pilot().invoke('getPilotName')}) could not kill one grunt in five seconds`);
            assert.equal(g.errors.length, 0, g.errors.join('\n'));
        }
    } finally { g.restore(); }
});

test('the pilot dies, the run ends, and R starts another one', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);

        for (let i = 0; i < 30; i++) { g.pilot().invoke('hurt', 999); await g.step(40); }
        assert.equal(g.pilot().invoke('isAlive'), 0, 'the pilot survived thirty lethal hits');
        assert.equal(g.director().invoke('getPhase'), 4, 'a dead pilot did not end the run');

        g.press('R');
        await g.step(4);
        assert.equal(g.director().invoke('getPhase'), 0, 'R did not return to the hangar');
        assert.equal(g.pilot().invoke('modCount'), 0, 'a new run kept the last run\'s mods');
    } finally { g.restore(); }
});

test('being hit costs the multiplier, and kills build it back', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        const d = g.director();

        for (let i = 0; i < 20; i++) { d.invoke('onEnemyKilled', 0, 0, 0, 10, 0); }
        const built = d.invoke('getMult');
        assert.ok(built > 1.2, `twenty kills only reached x${built}`);

        d.invoke('onPlayerHit');
        assert.ok(d.invoke('getMult') < built, 'being hit did not cost the multiplier');
    } finally { g.restore(); }
});
