// -----------------------------------------------------------------------------
// GraphicsBenchmark — measures what a machine can actually do, and maps the answer
// onto a preset.
//
// The mirror of SexyBiscuit.Engine/Debug/GraphicsBenchmark.cs.
//
// A pure accumulator: it is handed a frame time each frame and asked for a result
// when it has enough, so it runs identically on a renderer it knows nothing about and
// a test can drive it with a list of numbers.
//
// The low percentiles matter more than the average, which is why they are reported. A
// game averaging 90 fps with a 1% low of 18 stutters visibly and is a worse experience
// than a steady 60; an average alone cannot tell those apart, so a preset chosen on
// the average alone would be chosen wrong.
// -----------------------------------------------------------------------------

import { GraphicsSettings } from '../rendering/GraphicsSettings.js';

/** Frames thrown away at the start, while the new preset settles. */
export const WARM_UP_FRAMES = 30;

export class GraphicsBenchmark {
    constructor() {
        /** How long a run lasts, in seconds of real time after the warm-up. */
        this.duration = 8;

        /** Whether a run is in progress. */
        this.isRunning = false;

        /** The last completed run, or null. */
        this.result = null;

        /** The settings in force before the run, to be put back afterwards. */
        this.restore = null;

        this._frameTimes = [];
        this._elapsed = 0;
        this._warmedUp = 0;
    }

    /** 0..1 through the current run, for a progress bar. */
    get progress() {
        if (!this.isRunning || this.duration <= 0) return 0;
        return Math.min(1, Math.max(0, this._elapsed / this.duration));
    }

    /** Starts a run, discarding anything a previous one collected. */
    start() {
        this._frameTimes = [];
        this._elapsed = 0;
        this._warmedUp = 0;
        this.result = null;
        this.isRunning = true;
    }

    /** Abandons a run without producing a result. */
    cancel() {
        this.isRunning = false;
        this._frameTimes = [];
    }

    /**
     * Feeds one frame in. Returns the result on the frame the run finishes, else null.
     *
     * Real seconds, never scaled ones: a game paused behind the menu has a time scale
     * of zero, and a benchmark measured in scaled time would never advance.
     */
    tick(unscaledDeltaSeconds, stats = null) {
        if (!this.isRunning) return null;

        // The first frames of a run are discarded. Starting a benchmark means switching
        // preset, which reallocates buffers and drops the primitive cache, and timing
        // that would measure the switch rather than the hardware.
        if (this._warmedUp < WARM_UP_FRAMES) { this._warmedUp++; return null; }

        // A frame long enough to be a stall -- a hidden tab, a garbage collection the
        // size of a level load -- is not this machine's frame rate.
        if (unscaledDeltaSeconds > 0 && unscaledDeltaSeconds < 1) {
            this._frameTimes.push(unscaledDeltaSeconds);
            this._elapsed += unscaledDeltaSeconds;
        }

        if (this._elapsed < this.duration) return null;

        this.isRunning = false;
        this.result = summarise(this._frameTimes, stats);
        return this.result;
    }
}

// -----------------------------------------------------------------------------
// The arithmetic, which both engines do identically
// -----------------------------------------------------------------------------

/** Turns a list of frame times into a result. */
export function summarise(frameTimes, stats = null) {
    if (frameTimes.length === 0) {
        return {
            averageFps: 0, onePercentLowFps: 0, pointOnePercentLowFps: 0,
            frameTimeStdDevMs: 0, frames: 0, seconds: 0,
            drawCalls: 0, triangles: 0,
            suggestedPreset: GraphicsSettings.suggestFor(0, 0),
        };
    }

    const sorted = [...frameTimes].sort((a, b) => a - b);   // Slowest frames last.
    const total = frameTimes.reduce((sum, t) => sum + t, 0);
    const average = total / frameTimes.length;
    const averageFps = average > 0 ? 1 / average : 0;

    const variance = frameTimes.reduce((sum, t) => sum + (t - average) ** 2, 0) / frameTimes.length;

    const onePercentLowFps = lowFps(sorted, 0.01);

    return {
        averageFps,
        onePercentLowFps,
        pointOnePercentLowFps: lowFps(sorted, 0.001),
        frameTimeStdDevMs: Math.sqrt(variance) * 1000,
        frames: frameTimes.length,
        seconds: total,
        drawCalls: stats?.drawCalls ?? 0,
        triangles: stats?.triangles ?? 0,
        suggestedPreset: GraphicsSettings.suggestFor(averageFps, onePercentLowFps),
    };
}

/** The one line the menu shows when a run finishes. */
export function describe(result) {
    return `${Math.round(result.averageFps)} fps average · ${Math.round(result.onePercentLowFps)} fps 1% low · `
         + `${Math.round(result.pointOnePercentLowFps)} fps 0.1% low · ±${result.frameTimeStdDevMs.toFixed(1)} ms · `
         + `${result.triangles.toLocaleString('en-GB')} tris, ${result.drawCalls} draws · `
         + `suggests ${result.suggestedPreset}`;
}

/**
 * The average frame rate over the slowest fraction of frames.
 *
 * The average of the tail, not the single worst frame at that percentile. One frame is
 * noise -- a scheduler hiccup lands there as readily as a real stall -- and the average
 * over the tail is what a player experiences as the game hitching.
 */
function lowFps(ascending, fraction) {
    const count = Math.max(1, Math.ceil(ascending.length * fraction));
    const tail = ascending.slice(ascending.length - count);
    const mean = tail.reduce((sum, t) => sum + t, 0) / count;
    return mean > 0 ? 1 / mean : 0;
}
