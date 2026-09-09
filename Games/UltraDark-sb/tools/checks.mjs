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

test('the SECOND boss dies to gunfire too', async () => {
    // Reported from play: "bosses sometimes don't take damage", noticed only
    // once bosses grew health bars. "Sometimes" was every boss after the first.
    //
    // The projectile pool caches the boss's script and refreshes it with
    // `if (!bossActor || bossActor.active !== true)`. A DESTROYED actor kept
    // reporting active === true, so the guard never fired and every shot went on
    // being delivered to the previous, dead boss. The check above only ever
    // spawned one boss, so it passed throughout.
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);

        const killBossWithGunfire = async () => {
            const boss = g.find('Boss');
            assert.ok(boss, 'no boss actor on a boss wave');
            const before = g.said('BOSS DOWN');
            for (let i = 0; i < 500; i++) {
                boss.transform.x = 0;
                boss.transform.y = 0;
                g.bullets().invoke('fire', -60, 0, 0, 900, 40, 0, 6, 0.5, 0, 0);
                await g.step(4);
                if (g.said('BOSS DOWN') > before) { return true; }
            }
            return false;
        };

        g.director().invoke('forceWave', 5);
        await g.step(90);
        assert.ok(await killBossWithGunfire(), 'the FIRST boss would not die to gunfire');

        // A second boss, fought exactly the same way.
        g.director().invoke('forceWave', 10);
        await g.step(90);
        assert.ok(await killBossWithGunfire(),
            'the SECOND boss took five hundred shots and did not die -- the gun is ' +
            'still firing at the first one');
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

            // The shots the LAST pilot left in the air. Releasing the mouse
            // stops new ones; it does not recall the ones already flying, and
            // they are travelling rightwards along y = 0, which is exactly
            // where the next target is about to be put.
            g.bullets().invoke('clearAll');

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

// Every word on screen. A tree has to be walked and a node is only visible when
// every ancestor is, which is the harness's job rather than each test's.
const uiTexts = (g) => g.ui.nodes().filter((n) => n.text).map((n) => n.text);

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

        // R goes back to the FRONT END now, not to a HUD overlay saying HANGAR:
        // the main menu owns that screen, and two things offering to start a run
        // is two things that can disagree about which pilot is selected.
        g.press('R');
        await g.step(6);
        assert.equal(g.scriptOn('Menus').invoke('getScreen'), 1,
            'R did not return to the main menu');
        assert.ok(uiTexts(g).includes('PLAY'), 'the main menu is not on screen');
    } finally { g.restore(); }
});

test('pausing is refused where it would trap the player', async () => {
    const g = boot();
    try {
        await g.step(10);
        // The front end and the game-over screen already own the whole screen;
        // pausing one of them would replace the only thing telling the player
        // what to do with a PAUSED heading they cannot leave.
        assert.ok(uiTexts(g).includes('PLAY'), 'the main menu is not up at boot');
        g.press('Escape');
        await g.step(3);
        assert.ok(uiTexts(g).includes('PLAY'), 'Escape replaced the main menu');
        assert.equal(g.scriptOn('Menus').invoke('getScreen'), 1);
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

            // Clear, let the field settle, then clear again.
            //
            // A kill is not over when the enemy is. The shots the last pilot
            // fired are still flying rightwards along y = 0 -- exactly where the
            // next target is about to be put -- and the mods granted each
            // intermission add shockwaves, shrapnel and burns that go off in the
            // frames after a kill. Both were landing on the next pilot's target
            // before it had been shot at once, which is what made this test fail
            // about one run in four. It moved around because which mods a run is
            // holding by the eighth pilot is random.
            g.swarm().invoke('clearAll');
            g.bullets().invoke('clearAll');
            await g.step(6);
            g.swarm().invoke('clearAll');
            g.bullets().invoke('clearAll');

            // The settling frames may have ended the wave and opened the draft,
            // and a pilot in the draft is frozen. Put it back in a wave.
            g.director().invoke('forceWave', 1);
            g.director().invoke('forceBudget', 0);

            // Park the pilot: it drifts between iterations, and a target that
            // lands outside auto-aim range makes this test fail on whichever
            // pilot happened to be next.
            const ship = g.find('Player');
            ship.transform.x = 0;
            ship.transform.y = 0;

            const spawned = g.swarm().invoke('spawnKind', 0, 130, 0, 2, 0.001);  // a Drone, held still
            await g.step(2);

            const before = g.swarm().invoke('alive');
            assert.equal(before, 1,
                `the test target did not survive spawning: alive=${before} `
                + `spawnKind=${spawned} phase=${g.director().invoke('getPhase')} `
                + `wave=${g.director().invoke('getWave')} pilot=${p} `
                + `enemyActors=${g.byTag('Enemy').length} `
                + `shots=${g.byTag('Shot').length}/${g.byTag('ShotEnemy').length} `
                + `pilotAlive=${g.pilot().invoke('isAlive')} `
                + `| ${g.logs.slice(-4).join(' // ')}`);

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

// ---------------------------------------------------------------------------
// The boss bar
// ---------------------------------------------------------------------------
//
// Two readouts of one number: a marquee across the top of the screen, and a bar
// riding over the boss's head. Both can fail silently -- a bar that never
// appears, one that appears and never moves, one that stays on screen over an
// empty arena because it cached a proxy to a destroyed actor. None of that
// stops the game, and none of it shows up in a log.

/**
 * The tracked bar and its heading.
 *
 * By NAME now, not by shape. The flat UI had no names, so this had to pick the
 * bar out by "kind bar, anchored top", which would have matched any other bar
 * that happened to be anchored the same way. hud-kit names its nodes, and a
 * name is what the game's own code reaches them by too.
 */
function marquee(g) {
    const shown = g.ui.nodes();
    const named = (name) => shown.find((n) => n.name === name) ?? null;
    return { bar: named('trackerBar0'), title: named('trackerName0') };
}

test('the boss bar arrives with the boss and leaves with it', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);

        assert.equal(marquee(g).bar, null, 'a boss bar is up before there is a boss');
        assert.equal(g.find('BossBarFill'), undefined, 'a floating bar exists with no boss under it');

        g.director().invoke('forceWave', 5);
        await g.step(90);

        const up = marquee(g);
        assert.ok(up.bar, 'the boss is up and the marquee bar is not');
        assert.ok(up.title && up.title.text.indexOf('BRUTE PRIME') === 0,
            `the marquee names "${up.title ? up.title.text : ''}" rather than the boss`);
        assert.ok(up.bar.value > 0.99, 'the boss bar did not start full');
        assert.ok(g.find('BossBar') && g.find('BossBarFill'), 'no floating bar over the boss');

        // Half its health, and both readouts should say so.
        const boss = g.find('Boss');
        const script = boss.getComponents((await import('./harness.mjs')).ScriptComponent)[0];
        // Down to half, a point at a time -- the boss's health is a wave-scaled
        // number this test has no business knowing.
        for (let i = 0; i < 500 && script.invoke('getHealth01') > 0.5; i++) {
            script.invoke('takeDamage', 1, 0);
        }
        assert.ok(script.invoke('getHealth01') <= 0.5, 'could not get the boss to half health');
        await g.step(2);

        const half = marquee(g);
        assert.ok(half.bar.value < 0.75 && half.bar.value > 0.25,
            `the marquee reads ${half.bar.value} after roughly half the boss's health`);

        const fill = g.find('BossBarFill');
        const track = g.find('BossBar');
        const fillW = fill.getAllComponents().find((c) => c.constructor.name === 'SpriteRenderer').size.x;
        const trackW = track.getAllComponents().find((c) => c.constructor.name === 'SpriteRenderer').size.x;
        assert.ok(fillW < trackW * 0.75, 'the floating bar did not shrink with the boss');

        // It empties from the RIGHT, so the fill's left edge stays put. Without
        // this a sprite drawn from its centre shrinks towards the middle and the
        // bar reads as full-but-narrow rather than half gone.
        assert.ok(Math.abs((fill.transform.x - fillW / 2) - (track.transform.x - trackW / 2)) < 4,
            'the floating bar shrank towards its centre instead of emptying from the right');

        // And it goes when the boss goes -- including the sprites, which are
        // separate actors and are nobody else's to clean up.
        script.invoke('takeDamage', 999999, 0);
        await g.step(10);

        assert.equal(g.find('BossBarFill'), undefined, "the boss died and its bar stayed in the arena");
        assert.equal(g.find('BossBar'), undefined, "the boss died and its bar's track stayed in the arena");
        assert.equal(marquee(g).bar, null, 'the boss died and the marquee stayed up');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test("FOUNDRY's shut doors say so on the bar", async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 15);          // FOUNDRY is the third boss
        await g.step(90);

        const boss = g.find('Boss');
        assert.ok(boss, 'no boss on wave 15');
        const script = boss.getComponents((await import('./harness.mjs')).ScriptComponent)[0];
        assert.equal(script.invoke('getName'), 'FOUNDRY', 'wave 15 is not the FOUNDRY fight');

        // It starts with the doors shut, which is the whole reason the word has
        // to be on screen: an unshielded boss and a shielded one look identical
        // and one of them ignores every shot you land.
        assert.equal(script.invoke('isInvuln'), 1, 'FOUNDRY did not start shielded');
        await g.step(2);

        const shut = marquee(g);
        assert.ok(shut.title.text.indexOf('SHIELDED') > 0,
            `the bar says "${shut.title.text}" while the doors are shut`);

        // Wait the doors open, and the word goes.
        for (let i = 0; i < 700 && script.invoke('isInvuln') === 1; i++) { await g.step(1); }
        assert.equal(script.invoke('isInvuln'), 0, 'the doors never opened');
        await g.step(2);

        assert.ok(marquee(g).title.text.indexOf('SHIELDED') < 0,
            'the doors opened and the bar still says SHIELDED');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// Darks Games
// ---------------------------------------------------------------------------
//
// The first of these is the one that matters. Everything the hub offers is
// optional at runtime -- no slug, no network, no account -- and the failure mode
// of getting that wrong is a game that will not start for a player who is simply
// not signed in. Every other test in this file already boots with no runtime at
// all, so they are that check too; this one says so on purpose.

test('the whole game runs with no Darks Games hub at all', async () => {
    const g = boot();                       // no `dg`: signed out, nothing on the page
    try {
        assert.equal(g.social().invoke('isAvailable'), 0, 'a hub appeared out of nowhere');
        assert.equal(g.social().invoke('isSignedIn'), 0);
        assert.equal(g.social().invoke('getName'), 'Player', 'displayName must never be null');

        g.director().invoke('forceLaunch');
        await g.step(240);
        g.director().invoke('forceWave', 5);
        await g.step(120);

        assert.ok(g.director().invoke('getWave') >= 5, 'the run did not progress');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('presence says which run you are in, and stops repeating itself', async () => {
    const g = boot({ dg: true });
    try {
        await g.step(60);
        const first = g.dg.presences[0];
        assert.ok(first, 'nothing was published while sitting in the hangar');
        assert.equal(first.state, 'hangar');

        g.director().invoke('forceLaunch');
        await g.step(120);
        assert.ok(g.dg.presences.some((p) => p && /^wave \d+$/.test(p.state)),
            `never published a wave: ${JSON.stringify(g.dg.presences)}`);

        // The same line over and over is the thing a friends list cannot use.
        const idle = g.dg.presences.length;
        await g.step(60);
        assert.equal(g.dg.presences.length, idle,
            'an unchanged run published presence again');
    } finally { g.restore(); }
});

test('a counting achievement sends the difference, never the running total', async () => {
    const g = boot({ dg: true });
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);

        // Twelve kills in two batches, with a poll in between: the hub ADDS what
        // it is sent, so reporting the total each time counts the first batch
        // twice and "1000 kills" arrives hundreds of kills early.
        for (let i = 0; i < 5; i++) { g.director().invoke('onEnemyKilled', 0, 0, 0, 10, 1, 0); }
        await g.step(60);
        for (let i = 0; i < 7; i++) { g.director().invoke('onEnemyKilled', 0, 0, 0, 10, 1, 0); }
        await g.step(60);

        assert.equal(g.director().invoke('getKills'), 12);
        assert.equal(g.dg.totalFor('exterminator'), 12,
            `the hub was told ${g.dg.totalFor('exterminator')} kills for 12 actual`);

        // And a one-shot is sent once however long the run goes on.
        await g.step(300);
        assert.equal(g.dg.countOf('first_blood'), 1,
            'first blood was reported more than once');
    } finally { g.restore(); }
});

test('a restored cloud save merges with this device instead of replacing it', async () => {
    const g = boot({ dg: true });
    try {
        await g.step(30);
        assert.ok(g.dg.saveRequests > 0, 'signing in did not ask for the cloud save');

        // Fly one pilot here, then have the hub hand back a save from a device
        // that flew two different ones.
        g.director().invoke('forceLaunch');
        g.pilot().invoke('setPilot', 0);
        await g.step(60);
        assert.equal(g.social().invoke('getFlownCount'), 1);

        g.director().invoke('setBest', 500);
        g.dg.emit('save', { bestScore: 9000, flownMask: (1 << 3) | (1 << 5), kills: 40, bosses: 2 });
        await g.step(30);

        assert.equal(g.social().invoke('getFlownCount'), 3,
            'the other device\'s pilots replaced this one\'s instead of joining them');
        assert.equal(g.director().invoke('getBest'), 9000, 'a better cloud best was not adopted');

        // ...and a worse one never overwrites a better one, whichever arrives first.
        g.dg.emit('save', { bestScore: 10, flownMask: 0 });
        await g.step(30);
        assert.equal(g.director().invoke('getBest'), 9000, 'a worse cloud best overwrote a better one');
        assert.equal(g.social().invoke('getFlownCount'), 3, 'an empty mask cleared the flown pilots');

        // The counters resume from the cloud, so the next report is a difference
        // against what the hub already holds rather than against zero.
        for (let i = 0; i < 3; i++) { g.director().invoke('onEnemyKilled', 0, 0, 0, 10, 1, 0); }
        await g.step(60);
        assert.equal(g.dg.totalFor('exterminator'), 0,
            'kills already counted on the hub were sent again after a restore');
    } finally { g.restore(); }
});

test('the run banks itself to the cloud when it ends', async () => {
    const g = boot({ dg: true });
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);
        assert.equal(g.dg.saves.length, 0, 'a live run wrote a save');

        for (let i = 0; i < 30; i++) { g.pilot().invoke('hurt', 999); await g.step(40); }
        assert.equal(g.director().invoke('getPhase'), 4, 'the pilot survived thirty lethal hits');
        await g.step(60);

        assert.equal(g.dg.saves.length, 1, `the end of a run wrote ${g.dg.saves.length} saves`);
        assert.ok(Number.isFinite(g.dg.saves[0].data.bestScore), 'the save has no best score in it');
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// The 3D stage
// ---------------------------------------------------------------------------
//
// The game stays 2D underneath -- physics, AI, waves and collision are all
// untouched -- and this is the presentation over it. So the checks are about the
// mirror holding: everything drawn has a mesh, nothing drawn still has a live
// sprite, and the two characters arrive and leave with what they represent.

/** Every live component of one type in the scene. */
function componentsOfType(g, name) {
    const out = [];
    for (const actor of g.scene.allActors) {
        if (actor.isDestroyed) continue;
        for (const c of actor.getAllComponents()) if (c.constructor.name === name) out.push(c);
    }
    return out;
}

test('the stage builds a camera the engine will actually use', async () => {
    const g = boot();
    try {
        await g.step(10);

        const camera = g.byTag('MainCamera3D')[0];
        assert.ok(camera, 'no camera tagged MainCamera3D');
        // The tag is the whole contract natively: Camera3D.Main accepts that one
        // and no other, so a camera tagged anything else renders a black screen
        // and says nothing about why.
        assert.ok(camera.getAllComponents().some((c) => c.constructor.name === 'Camera3D'),
            'the tagged actor has no Camera3D on it');

        assert.ok(componentsOfType(g, 'Light3D').length >= 2, 'no lights');
        assert.ok(g.find('Ground3D'), 'no ground under the arena');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('everything drawn becomes a mesh, and nothing drawn stays a sprite', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(120);
        g.director().invoke('forceWave', 5);
        await g.step(120);
        for (let k = 0; k < 12; k++) { g.swarm().invoke('spawnKind', k, 200 + k * 40, 300, 1, 1); }
        g.bullets().invoke('fire', 0, 0, 0, 300, 1, 0, 4, 5, 0, 0);
        await g.step(30);

        assert.ok(g.scriptOn('Stage3D').invoke('adoptedCount') > 20,
            'the stage adopted almost nothing');

        // The 2D pass paints OVER the 3D one, so a sprite left enabled is a flat
        // coloured rectangle sitting on top of the world it was meant to become.
        const live = componentsOfType(g, 'SpriteRenderer').filter((s) => s.enabled);
        assert.equal(live.length, 0,
            `${live.length} sprites are still drawing over the 3D world`);

        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('the pilot is a character, and it walks when the ship does', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(60);

        // One enemy, far away and held still. The clip follows the ship's SPEED,
        // which is the right rule -- a pilot shoved by a Brute is moving -- so
        // nothing may be near enough to shove it. But the arena cannot be EMPTY
        // either: an empty arena with no budget left ends the wave, and a pilot
        // in the draft that opens is frozen and cannot fly at all.
        g.director().invoke('forceWave', 1);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        g.swarm().invoke('spawnKind', 0, 900, 500, 1, 0.001);
        await g.step(20);

        const stage = g.scriptOn('Stage3D');
        assert.equal(stage.invoke('hasPilotChibi'), 1, 'the pilot has no character');
        assert.equal(stage.invoke('getClip'), 'idle', 'a parked ship is not idling');

        // Fly it, and the clip should follow the speed rather than the input:
        // a pilot pushed by a knockback is moving too.
        g.hold('W', true);
        await g.step(90);
        const moving = stage.invoke('getClip');
        g.hold('W', false);
        assert.ok(moving === 'walk' || moving === 'run',
            `a ship at speed is playing "${moving}"`);

        await g.step(120);
        assert.equal(stage.invoke('getClip'), 'idle', 'a stopped ship is still running');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('the boss is a character, and it leaves when the boss does', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(10);
        const stage = g.scriptOn('Stage3D');
        assert.equal(stage.invoke('hasBossChibi'), 0, 'a boss character with no boss');

        g.director().invoke('forceWave', 5);
        await g.step(120);
        assert.equal(stage.invoke('hasBossChibi'), 1, 'the boss arrived without a character');

        const boss = g.find('Boss');
        const script = boss.getComponents((await import('./harness.mjs')).ScriptComponent)[0];
        for (let i = 0; i < 500 && script.invoke('isDead') === 0; i++) { script.invoke('takeDamage', 1, 0); }
        await g.step(60);

        assert.equal(stage.invoke('hasBossChibi'), 0,
            'the boss died and its character stayed in the arena');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('the dark is a screen panel in 3D, not a slab over the world', async () => {
    const g = boot();
    try {
        g.director().invoke('forceLaunch');
        await g.step(30);
        const stage = g.scriptOn('Stage3D');
        assert.ok(stage.invoke('getDark01') < 0.05, 'wave 1 is already dark');

        // A world-space quad over a perspective camera is the wrong shape: you
        // can see its edge, and it dims what is nearest the camera hardest. The
        // dark is a property of the view.
        g.director().invoke('forceWave', 20);
        await g.step(120);
        assert.ok(stage.invoke('getDark01') > 0.5,
            `wave 20 is only ${stage.invoke('getDark01')} dark`);
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('an adopted mesh is the size and colour of the sprite it replaced', async () => {
    const g = boot();
    try {
        await g.step(30);

        // The check that "it has a mesh" cannot make. A component's size reads
        // back as an ARRAY in the browser and an object under Jint, and its tint
        // as a hex STRING in the browser and {r,g,b,a} under Jint -- lower case,
        // where a script WRITES {R,G,B}. Reading one shape gets undefined on the
        // other engine: undefined size falls through to a 1x1 default, and
        // undefined colour converts to #000000. The whole world rendered as
        // one-unit black cubes on a black background, with no error anywhere,
        // and every other check here passed.
        const floor = g.find('Floor');
        assert.ok(floor, 'no floor');

        const t3d = floor.getAllComponents().find((c) => c.constructor.name === 'Transform3D');
        const mesh = floor.getAllComponents().find((c) => c.constructor.name === 'MeshRenderer');
        assert.ok(t3d && mesh, 'the floor was never adopted');

        const scale = t3d.localScale;
        assert.ok(scale.x > 100 && scale.z > 100,
            `the floor is ${scale.x}x${scale.z} units -- it should be the size of the arena`);

        const colour = String(mesh.albedoColor ?? '').toLowerCase();
        assert.notEqual(colour, '#000000ff', 'the floor was painted pure black');
        assert.notEqual(colour, '#000000', 'the floor was painted pure black');
        assert.ok(colour.length > 0, 'the floor has no colour at all');

        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

// ---------------------------------------------------------------------------
// The front end
// ---------------------------------------------------------------------------
//
// A main menu, a character select, options and a dialog, all on the UI tree.
// Every one of these can fail without stopping the game: a menu nothing can
// focus, a select screen that shows one pilot and launches another, an option
// that moves a number nothing reads, a dialog you can click straight through.

const S_NONE = 0, S_MAIN = 1, S_SELECT = 2, S_OPTIONS = 3;

test('the game opens on a menu a pad can use, and walks to the pilots', async () => {
    const g = boot();
    try {
        await g.step(10);
        const menus = g.scriptOn('Menus');
        assert.equal(menus.invoke('getScreen'), S_MAIN);

        // Focus is what makes a menu work without a mouse, and something has to
        // hold it the moment the screen opens -- a menu that waits for a click
        // to give itself focus is a menu a pad cannot open at all.
        assert.ok(g.ui.find('mPlay'), 'no PLAY button');
        assert.equal(g.ui.nodes().some((n) => n.node.focused), true,
            'the menu opened with nothing focused');

        menus.invoke('openSelect');
        await g.step(5);
        assert.equal(menus.invoke('getScreen'), S_SELECT);

        // The eight cards read the pilot script's own roster, so there is one
        // list rather than two that drift.
        const texts = uiTexts(g);
        assert.ok(texts.includes('BINK') && texts.includes('HAWK'),
            `the select screen is missing pilots: ${texts.join(', ')}`);
        assert.ok(texts.some((t) => t.includes('BLINK VOLLEY')),
            'the cards do not name the abilities');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('the pilot the select screen shows is the pilot that launches', async () => {
    const g = boot();
    try {
        await g.step(10);
        const menus = g.scriptOn('Menus');
        menus.invoke('openSelect');
        await g.step(5);

        // Walk right twice with the arrows: the focus system does the moving,
        // and what has focus IS what is chosen -- one state, so the highlight and
        // the pilot cannot disagree.
        g.press('ArrowRight'); await g.step(2);
        g.press('ArrowRight'); await g.step(2);

        const chosen = menus.invoke('getChosen');
        assert.ok(chosen > 0, 'the arrows moved nothing');
        assert.equal(g.pilot().invoke('getPilot'), chosen,
            'the highlighted card is not the pilot that would fly');

        const name = g.pilot().invoke('getPilotName');
        g.press('Enter');
        await g.step(10);

        assert.equal(menus.invoke('getScreen'), S_NONE, 'ENTER did not leave the menu');
        assert.equal(g.director().invoke('getPhase'), 1, 'ENTER did not start the run');
        assert.equal(g.pilot().invoke('getPilotName'), name, 'a different pilot launched');
    } finally { g.restore(); }
});

test('an option moves a number something actually reads', async () => {
    const g = boot();
    try {
        await g.step(10);
        const menus = g.scriptOn('Menus');
        assert.equal(g.scriptOn('Effects').invoke('getShakeScale'), 1,
            'the shake scale did not start at 1');

        // Driven the way a player drives it: click OPTIONS, then click the
        // option's own button. An option nothing reads is a number in a menu.
        clickNode(g, 'mOptions');
        await g.step(4);
        assert.equal(menus.invoke('getScreen'), S_OPTIONS, 'OPTIONS did not open');

        for (let i = 0; i < 6; i++) { clickNode(g, 'optShakeDown'); await g.step(3); }
        assert.equal(menus.invoke('getShake'), 0, 'the shake option did not reach zero');
        assert.equal(g.scriptOn('Effects').invoke('getShakeScale'), 0,
            'the option moved but the effects script never heard about it');

        // And the dark, which is the accessibility one.
        for (let i = 0; i < 3; i++) { clickNode(g, 'optDarkDown'); await g.step(3); }
        assert.ok(g.scriptOn('Stage3D').invoke('getDarkLimit') < 1,
            'the darkness option did not reach the stage');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

test('a dialog traps the screen behind it', async () => {
    const g = boot();
    try {
        await g.step(10);
        const menus = g.scriptOn('Menus');

        clickNode(g, 'mHow');
        await g.step(4);
        assert.equal(menus.invoke('isDialogOpen'), 1, 'HOW TO PLAY opened nothing');
        assert.ok(uiTexts(g).includes('HOW TO PLAY'), 'the dialog has no heading');

        // The pointer cannot reach the menu behind it -- though that is the
        // full-screen sheet doing the work, not `modal`.
        const behind = g.ui.nodes().find((n) => n.name === 'mOptions');
        g.pointAt(behind.screen.x + behind.screen.width / 2,
                  behind.screen.y + behind.screen.height / 2);
        await g.step(3);
        assert.equal(g.ui.find('mOptions').hovered, false,
            'the pointer reached a button behind the dialog');

        // FOCUS is what `modal` guarantees, and it is the half a mouse never
        // shows you: without it a pad walks straight out of the dialog into the
        // menu behind, and the player is pressing buttons they cannot see.
        //
        // Driven through the UI's own navigation rather than through the game's
        // keys, because that is what a pad does -- and because the script guards
        // itself while a dialog is up, so pressing a key proves only the guard.
        for (const way of ['down', 'down', 'up', 'left', 'right', 'down']) {
            g.ui.navigate(way);
            await g.step(2);
        }
        const focused = g.ui.nodes().find((n) => n.node.focused);
        assert.ok(focused, 'nothing has focus inside an open dialog');
        assert.ok(focused.name === 'dlgOk' || focused.name === 'dlgCancel',
            `focus walked out of the dialog to "${focused.name}"`);

        const screenBefore = menus.invoke('getScreen');
        clickNode(g, 'mOptions');
        await g.step(4);
        assert.equal(menus.invoke('getScreen'), screenBefore,
            'a click went through the dialog to the menu behind it');
        assert.equal(menus.invoke('isDialogOpen'), 1, 'the dialog closed itself');

        g.press('Enter');
        await g.step(4);
        assert.equal(menus.invoke('isDialogOpen'), 0, 'ENTER did not close the dialog');
        assert.equal(g.errors.length, 0, g.errors.join('\n'));
    } finally { g.restore(); }
});

/** Presses a node by name, the way a player does: down inside, then release. */
function clickNode(g, name) {
    const node = g.ui.nodes().find((n) => n.name === name);
    if (!node) { throw new Error(`no node called "${name}" on screen`); }
    const r = node.screen;
    g.pointAt(r.x + r.width / 2, r.y + r.height / 2);
    g.pointerDown(true);
    return g.step(2).then(() => { g.pointerDown(false); return g.step(2); });
}
