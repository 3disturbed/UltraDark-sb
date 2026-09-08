// -----------------------------------------------------------------------------
// The clock and the colour ramp, from the shared fixture.
//
// The mirror of SexyBiscuit.Tests/TimeOfDayTests.cs, which the fixture's own
// comment has always said existed. A day/night cycle is a clock and a colour
// ramp, and both are pure arithmetic that two independent implementations agree
// on only where something forces them to. A sun elevation off by a degree moves
// dawn; a channel off by a byte makes the desktop dusk warm and the web dusk
// blue. Neither would fail any other test in this repository.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { TimeOfDay, DayPhase } from '../src/index.js';

const TOLERANCE = 0.001;

const fixture = JSON.parse(fs.readFileSync(
    path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'day-night-cases.json'),
    'utf8'));

/** A clock parked at one moment, configured as the fixture says. */
function clockAt(time) {
    const clock = new TimeOfDay();
    clock.sunTilt = fixture.sunTilt;
    clock.sunAzimuth = fixture.sunAzimuth;
    clock.time01 = time;
    return clock;
}

function close(expected, actual, what) {
    assert.ok(Math.abs(expected - actual) <= TOLERANCE,
        `${what}: expected ${expected}, got ${actual}`);
}

test('every day/night sample matches what the C# engine makes of the same moment', () => {
    for (const sample of fixture.samples) {
        const clock = clockAt(sample.time);
        const at = (what) => `t=${sample.time} ${what}`;

        assert.equal(clock.clock, sample.clock, at('clock'));
        assert.equal(clock.phase, sample.phase, at('phase'));
        assert.equal(clock.isNight, sample.isNight, at('isNight'));

        close(sample.sunElevation, clock.sunElevation, at('sunElevation'));
        close(sample.darkness, clock.darkness, at('darkness'));

        const sun = clock.sunDirection;
        close(sample.sunDirection[0], sun.x, at('sunDirection.x'));
        close(sample.sunDirection[1], sun.y, at('sunDirection.y'));
        close(sample.sunDirection[2], sun.z, at('sunDirection.z'));

        const sky = clock.sky;
        for (const channel of ['zenith', 'horizon', 'sun', 'ambientSky', 'ambientGround', 'fog']) {
            assert.equal(String(sky[channel]).toLowerCase(), sample.sky[channel].toLowerCase(),
                at(`sky.${channel}`));
        }
        close(sample.sky.sunIntensity, sky.sunIntensity, at('sky.sunIntensity'));
        close(sample.sky.fogDensity, sky.fogDensity, at('sky.fogDensity'));
    }
});

test('the clock rounds to the minute rather than flooring twice', () => {
    // 0.3 of a day is exactly 07:12. Flooring the hour and then the minute printed
    // 07:11 here and 07:12 under C#, because a double lands a hair below the
    // boundary and a float a hair above -- nine of the seventeen fixture samples
    // were a minute short, and only the two the engines disagreed on ever failed.
    assert.equal(clockAt(0.3).clock, '07:12');
    assert.equal(clockAt(0.85).clock, '20:24');
    assert.equal(clockAt(0.5).clock, '12:00');

    // Midnight from either side, since the rounding wraps.
    assert.equal(clockAt(0).clock, '00:00');
    assert.equal(clockAt(0.9999).clock, '00:00');
});

test('a phase is read off the sun rather than off a fraction of the day', () => {
    // The point of the design: raising the tilt has to move dawn to where the light
    // actually changes, not leave it at a magic time.
    assert.equal(clockAt(0.5).phase, DayPhase.Day);
    assert.equal(clockAt(0).phase, DayPhase.Night);
    assert.ok(clockAt(0).isNight);
    assert.ok(!clockAt(0.5).isNight);
});
