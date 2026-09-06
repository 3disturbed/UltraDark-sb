// -----------------------------------------------------------------------------
// Time — the static clock, mirroring SexyBiscuit.Engine.Core.Time.
//
// `advance` reproduces the C# arithmetic exactly, including the spike clamp and
// the half-second FPS averaging window, so a game that reads Time behaves the
// same under both runtimes.
// -----------------------------------------------------------------------------

let _timeScale = 1;
let _fpsAccum = 0;
let _fpsFrames = 0;

/** Frame timing. Every field is read-only to game code; the host advances it. */
export const Time = {
    /** Seconds since the last frame, already multiplied by {@link Time.timeScale}. */
    deltaTime: 0,

    /** Seconds since the last frame, ignoring time scale. Menus should use this. */
    unscaledDeltaTime: 0,

    /** Scaled seconds accumulated since start-up. */
    timeSinceStartup: 0,

    /** Wall-clock seconds since start-up. */
    realtimeSinceStartup: 0,

    /** The fixed step, scaled. Set from `EngineConfig.fixedTimestep` times the scale. */
    fixedDeltaTime: 1 / 60,

    /** Frames rendered since start-up. */
    frameCount: 0,

    /** Frames per second, averaged over roughly half a second. */
    fps: 0,

    /**
     * The longest frame the engine will admit to, in seconds. A frame that took
     * longer — a breakpoint, a backgrounded tab — is reported as this, so
     * physics does not explode on the frame after a stall.
     */
    maximumDeltaTime: 0.1,

    /** Multiplier on {@link Time.deltaTime}. Clamped at zero; 0 freezes the game. */
    get timeScale() { return _timeScale; },
    set timeScale(value) { _timeScale = Math.max(0, value); },

    /**
     * Advances the clock by one frame. Called by the host, not by game code.
     * @param {number} rawDeltaSeconds Wall-clock seconds since the previous frame.
     */
    advance(rawDeltaSeconds) {
        Time.unscaledDeltaTime = Math.min(rawDeltaSeconds, Time.maximumDeltaTime);
        Time.deltaTime = Time.unscaledDeltaTime * _timeScale;
        Time.realtimeSinceStartup += Time.unscaledDeltaTime;
        Time.timeSinceStartup += Time.deltaTime;
        Time.frameCount++;

        _fpsAccum += Time.unscaledDeltaTime;
        _fpsFrames++;
        if (_fpsAccum >= 0.5) {
            Time.fps = _fpsFrames / _fpsAccum;
            _fpsAccum = 0;
            _fpsFrames = 0;
        }
    },

    /** Returns the clock to its start-up state. */
    reset() {
        Time.deltaTime = 0;
        Time.unscaledDeltaTime = 0;
        Time.timeSinceStartup = 0;
        Time.realtimeSinceStartup = 0;
        Time.frameCount = 0;
        Time.fps = 0;
        _timeScale = 1;
        _fpsAccum = 0;
        _fpsFrames = 0;
    },
};
