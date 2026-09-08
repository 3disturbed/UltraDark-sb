// -----------------------------------------------------------------------------
// TimeOfDay — an in-game clock that drives the sun, the sky and the ambient light.
//
// The mirror of SexyBiscuit.Engine/Rendering/TimeOfDay.cs.
//
// A Component rather than a subsystem, and deliberately: the scene serialiser writes
// components and nothing else, and a clock that could not be authored into a level and
// saved would be a clock every game had to wire up in script.
//
// This is the thing CookieJar/day-night-cycle says it is not. That cookie's own notes
// are explicit -- "No lighting. This is a flat tint over everything, not a light
// model... Real 2D lighting is engine work" -- because a script has no viewport and
// cannot reach a light. From inside the engine it can.
//
// Time runs 0 to 1 with 0 at midnight and 0.5 at noon, which is what makes `hour` and
// a sun elevation fall out of it rather than being invented.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Light3D, LightType, SkyLight } from './Light3D.js';
import { Skybox } from './Skybox.js';
import { sample as sampleSky, wrap01 } from './SkyGradient.js';

/** Where the sun is, in the only terms a game usually cares about. */
export const DayPhase = Object.freeze({
    Night: 'Night', Dawn: 'Dawn', Day: 'Day', Dusk: 'Dusk',
});

/** Twilight band, in degrees either side of the horizon. */
export const TWILIGHT_DEGREES = 6;

export class TimeOfDay extends Component {
    static schema = {
        dayLength:  { type: P.Number, default: 150, min: 0 },
        startAt:    { type: P.Number, default: 0.26, min: 0, max: 1 },
        paused:     { type: P.Bool, default: false },
        sunTilt:    { type: P.Number, default: 35, min: 0, max: 89 },
        sunAzimuth: { type: P.Number, default: 90 },
        driveSun:   { type: P.Bool, default: true },
        driveSky:   { type: P.Bool, default: true },
        drive2D:    { type: P.Bool, default: true },
        dayNumber:  { type: P.Int, default: 1, min: 1 },
    };

    /**
     * The clock currently driving the scene, or null.
     *
     * Last one to wake wins. A level that additively loads a lit interior wants that
     * interior's clock, not the one the outdoor scene registered first.
     */
    static active = null;

    constructor() {
        super();
        this.dayLength = 150;
        this.startAt = 0.26;
        this.paused = false;
        this.sunTilt = 35;
        this.sunAzimuth = 90;
        this.driveSun = true;
        this.driveSky = true;
        this.drive2D = true;
        this.dayNumber = 1;

        /** Raised once each time the phase changes, with what it was and what it is. */
        this.onPhaseChanged = null;

        /** Raised once each time the clock passes midnight, with the new day number. */
        this.onNewDay = null;

        this._time01 = 0;
        this._lastPhase = DayPhase.Night;
    }

    // -------------------------------------------------------------------------
    // Live state
    // -------------------------------------------------------------------------

    /** The moment of the current day, 0 at midnight and 0.5 at noon. */
    get time01() { return this._time01; }

    set time01(value) {
        const wrapped = wrap01(value);

        // A clock set backwards past midnight has gone back a day, and a game that
        // counts days off this must not see day 4 turn into day 5 on a rewind.
        if (wrapped < this._time01 - 0.5) this.dayNumber++;
        else if (wrapped > this._time01 + 0.5) this.dayNumber = Math.max(1, this.dayNumber - 1);

        this._time01 = wrapped;
    }

    /** The hour, 0 to 24. */
    get hour() { return this._time01 * 24; }

    /**
     * The clock as a game would print it, such as "06:42".
     *
     * Rounded to whole minutes first, rather than flooring the hour and then the
     * minute. 0.3 of a day is exactly 07:12, but it is not exactly representable:
     * this engine's double landed a hair below the boundary and C#'s float a hair
     * above, so the same moment printed 07:11 here and 07:12 there. A minute is the
     * smallest thing this returns, so it is the thing to round to.
     */
    get clock() {
        const minutes = Math.round(this._time01 * 1440) % 1440;
        const h = Math.floor(minutes / 60);
        return `${String(h).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`;
    }

    /**
     * The sun's height above the horizon, in degrees. Negative is below it.
     *
     * The real quantity behind every other answer here. Phases are read off this rather
     * than off magic fractions of a day, so changing sunTilt moves dawn and dusk to
     * where the light actually changes instead of leaving them behind.
     */
    get sunElevation() {
        // Peaks at noon, bottoms at midnight, scaled by the tilt off vertical.
        const angle = (this._time01 - 0.25) * Math.PI * 2;
        const s = Math.sin(angle) * Math.cos((this.sunTilt * Math.PI) / 180 * 0.5);
        return (Math.asin(Math.min(1, Math.max(-1, s))) * 180) / Math.PI;
    }

    /** Which way the sun is shining, pointing from the sun towards the ground. */
    get sunDirection() {
        const elevation = (this.sunElevation * Math.PI) / 180;
        const azimuth = ((this.sunAzimuth + this._time01 * 360) * Math.PI) / 180;

        // Negated because a light's direction is where its light goes, not where the sun
        // is: at noon the sun is overhead and the light travels downwards.
        const x = Math.cos(elevation) * Math.cos(azimuth);
        const y = Math.sin(elevation);
        const z = Math.cos(elevation) * Math.sin(azimuth);
        const length = Math.hypot(x, y, z) || 1;

        return { x: -x / length, y: -y / length, z: -z / length };
    }

    /** Where the sun is, in the terms a game usually cares about. */
    get phase() {
        const elevation = this.sunElevation;
        if (elevation > TWILIGHT_DEGREES) return DayPhase.Day;
        if (elevation < -TWILIGHT_DEGREES) return DayPhase.Night;

        // Inside the twilight band, which one depends on whether the sun is rising.
        return this._time01 < 0.5 ? DayPhase.Dawn : DayPhase.Dusk;
    }

    /**
     * How dark it is, 0 in full day and 1 at the deepest night.
     *
     * The curve a game reads to make night mean something -- more enemies, worse
     * accuracy, better stealth -- and named to match the cookie's getDarkness so a
     * script that already used one reads the same against the other.
     */
    get darkness() {
        const elevation = this.sunElevation;
        if (elevation >= TWILIGHT_DEGREES) return 0;
        if (elevation <= -TWILIGHT_DEGREES) return 1;
        return 1 - (elevation + TWILIGHT_DEGREES) / (TWILIGHT_DEGREES * 2);
    }

    /** Whether the sun is below the horizon. */
    get isNight() { return this.sunElevation < 0; }

    /** The sky's colours at this moment. */
    get sky() { return sampleSky(this._time01); }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    awake() {
        this._time01 = wrap01(this.startAt);
        this._lastPhase = this.phase;
        TimeOfDay.active = this;
    }

    onDestroy() {
        // Without this a destroyed clock keeps driving the renderer forever, which is
        // the leak every static registry in this engine has to guard against.
        if (TimeOfDay.active === this) TimeOfDay.active = null;
    }

    update(deltaTime) {
        this.advance(deltaTime);
        this.apply();
    }

    /**
     * Moves the clock on and raises whatever that crossed.
     *
     * Separate from apply() so a test can run a whole day in one call with no renderer,
     * no scene and no canvas anywhere.
     */
    advance(deltaSeconds) {
        if (this.paused || this.dayLength <= 0 || deltaSeconds <= 0) return;

        const advanced = this._time01 + deltaSeconds / this.dayLength;

        // A frame long enough to cross more than one midnight still counts every day it
        // crossed, so a game left paused at a breakpoint does not lose a week.
        const days = Math.floor(advanced);
        this._time01 = advanced - days;

        for (let i = 0; i < days; i++) {
            this.dayNumber++;
            this.onNewDay?.(this.dayNumber);
        }

        const phase = this.phase;
        if (phase === this._lastPhase) return;

        const was = this._lastPhase;
        this._lastPhase = phase;
        this.onPhaseChanged?.(was, phase);
    }

    /**
     * Pushes this moment onto the scene's lights, sky and ambient.
     *
     * Everything it touches already existed and was simply never driven by anything:
     * the sun is an ordinary Light3D and the sky an ordinary Skybox.
     */
    apply() {
        const sky = this.sky;
        if (!sky) return;

        if (this.driveSun) {
            const sun = Light3D.all.find((l) => l.enabled && l.type === LightType.Directional);
            if (sun) {
                sun.color = sky.sun;
                sun.intensity = sky.sunIntensity;
                const transform = sun.actor?.transform3D ?? sun.actor?.getComponent?.('Transform3D');
                if (transform?.setForward) transform.setForward(this.sunDirection);
            }
        }

        if (this.driveSky) {
            if (SkyLight.active) {
                SkyLight.active.skyColor = sky.ambientSky;
                SkyLight.active.groundColor = sky.ambientGround;
            }

            for (const box of skyboxesIn(this.actor?.scene)) {
                box.gradientTop = sky.zenith;
                box.gradientBottom = sky.horizon;
            }
        }
    }
}

function* skyboxesIn(scene) {
    if (!scene) return;
    for (const layer of scene.layers ?? []) {
        for (const actor of layer.actors ?? []) {
            const box = actor.getComponent?.(Skybox);
            if (box) yield box;
        }
    }
}

registerComponent(TimeOfDay, {
    category: 'Rendering',
    summary: 'An in-game clock that drives the sun, the sky and the ambient light.',
});
