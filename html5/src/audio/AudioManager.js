// -----------------------------------------------------------------------------
// AudioManager — WebAudio playback with the bus mixing the C# manager has.
//
// One browser-specific rule shapes this whole file: an AudioContext starts
// suspended until a user gesture, and on iOS it stays suspended until a gesture
// touches it directly. So the context is created lazily, `unlock` is wired to
// the first pointer or key event, and anything played before then is queued
// rather than dropped.
// -----------------------------------------------------------------------------

/** A named volume group. */
export class AudioBus {
    constructor(name, context, destination) {
        this.name = name;
        this._gain = context.createGain();
        this._gain.connect(destination);
        this._volume = 1;
        this._muted = false;
    }

    get node() { return this._gain; }

    get volume() { return this._volume; }
    set volume(value) {
        this._volume = Math.max(0, value);
        this._gain.gain.value = this._muted ? 0 : this._volume;
    }

    get muted() { return this._muted; }
    set muted(value) {
        this._muted = Boolean(value);
        this._gain.gain.value = this._muted ? 0 : this._volume;
    }
}

/** A reference to one playing sound. */
export class AudioHandle {
    constructor(id, source, gain) {
        this.id = id;
        this.source = source;
        this.gain = gain;
        this.isPlaying = true;
    }
}

let _nextHandleId = 1;

/** Plays sounds and music. One instance lives on the engine host. */
export class AudioManager {
    /**
     * @param {import('../assets/AssetManager.js').AssetManager} assets
     * @param {object} [options]
     */
    constructor(assets, { autoUnlock = true } = {}) {
        this.assets = assets;

        /** @type {?AudioContext} Created on first use. */
        this.context = null;

        /** @type {Map<number, AudioHandle>} */
        this._playing = new Map();
        this._queued = [];
        this._unlocked = false;

        this.buses = {};

        if (autoUnlock) this._installUnlockHandlers();
    }

    /** True once the browser has let audio start. */
    get isUnlocked() { return this._unlocked; }

    /** Creates the AudioContext and its buses. Safe to call repeatedly. */
    ensureContext() {
        if (this.context) return this.context;

        const Ctor = globalThis.AudioContext ?? globalThis.webkitAudioContext;
        if (!Ctor) return null;

        this.context = new Ctor();

        // Master feeds the destination; the rest feed master, so one volume
        // control affects everything, exactly as the C# bus tree does.
        this.buses.master = new AudioBus('master', this.context, this.context.destination);
        for (const name of ['music', 'sfx', 'voice']) {
            this.buses[name] = new AudioBus(name, this.context, this.buses.master.node);
        }

        return this.context;
    }

    /**
     * Resumes the context. Must be called from inside a user gesture the first
     * time; the installed handlers do that automatically.
     */
    async unlock() {
        const context = this.ensureContext();
        if (!context) return false;

        if (context.state === 'suspended') {
            try { await context.resume(); } catch { return false; }
        }

        this._unlocked = context.state === 'running';
        if (this._unlocked && this._queued.length > 0) {
            const queued = this._queued.splice(0);
            for (const play of queued) play();
        }
        return this._unlocked;
    }

    _installUnlockHandlers() {
        if (typeof window === 'undefined') return;

        const handler = () => {
            this.unlock().then((ok) => {
                if (!ok) return;
                for (const event of ['pointerdown', 'touchend', 'keydown']) {
                    window.removeEventListener(event, handler);
                }
            });
        };

        for (const event of ['pointerdown', 'touchend', 'keydown']) {
            window.addEventListener(event, handler);
        }
    }

    /**
     * Plays a sound and returns a handle.
     *
     * @param {string} path
     * @param {object} [options]
     * @param {boolean} [options.loop=false]
     * @param {number} [options.volume=1]
     * @param {number} [options.pitch=1] Playback rate.
     * @param {string} [options.bus='sfx']
     */
    play(path, { loop = false, volume = 1, pitch = 1, bus = 'sfx' } = {}) {
        const handle = new AudioHandle(_nextHandleId++, null, null);

        const start = async () => {
            const context = this.ensureContext();
            if (!context) return;

            try {
                const buffer = await this.assets.loadAudio(path, context);
                if (!(buffer instanceof AudioBuffer)) return;
                if (!handle.isPlaying) return;      // stopped while the file was loading

                const source = context.createBufferSource();
                source.buffer = buffer;
                source.loop = loop;
                source.playbackRate.value = pitch;

                const gain = context.createGain();
                gain.gain.value = volume;

                source.connect(gain);
                gain.connect((this.buses[bus] ?? this.buses.sfx).node);
                source.start();

                handle.source = source;
                handle.gain = gain;
                source.onended = () => {
                    handle.isPlaying = false;
                    this._playing.delete(handle.id);
                };

                this._playing.set(handle.id, handle);
            } catch (err) {
                console.warn(`[AudioManager] ${path}: ${err.message}`);
            }
        };

        if (this._unlocked) start();
        else this._queued.push(start);

        return handle;
    }

    /** Plays a sound once, with no handle to keep. */
    playOneShot(path, options = {}) { this.play(path, { ...options, loop: false }); }

    /** Stops one sound. */
    stop(handle) {
        if (!handle) return;
        handle.isPlaying = false;
        try { handle.source?.stop(); } catch { /* already ended */ }
        this._playing.delete(handle.id);
    }

    /** Stops a sound by its numeric id — what the script bridge passes. */
    stopById(id) {
        const handle = this._playing.get(id);
        if (handle) this.stop(handle);
    }

    /** Stops everything. */
    stopAll() {
        for (const handle of [...this._playing.values()]) this.stop(handle);
        this._queued.length = 0;
    }

    /** Fades a sound in from silence over `duration` seconds. */
    fadeIn(handle, duration = 1, target = 1) {
        if (!handle?.gain || !this.context) return;
        const now = this.context.currentTime;
        handle.gain.gain.cancelScheduledValues(now);
        handle.gain.gain.setValueAtTime(0, now);
        handle.gain.gain.linearRampToValueAtTime(target, now + duration);
    }

    /** Fades a sound out and stops it. */
    fadeOut(handle, duration = 1) {
        if (!handle?.gain || !this.context) { this.stop(handle); return; }

        const now = this.context.currentTime;
        handle.gain.gain.cancelScheduledValues(now);
        handle.gain.gain.setValueAtTime(handle.gain.gain.value, now);
        handle.gain.gain.linearRampToValueAtTime(0, now + duration);

        setTimeout(() => this.stop(handle), duration * 1000);
    }

    /** Master volume, 0..1. */
    setMasterVolume(volume) {
        this.ensureContext();
        if (this.buses.master) this.buses.master.volume = volume;
    }

    /** Volume for one bus: 'music', 'sfx' or 'voice'. */
    setBusVolume(bus, volume) {
        this.ensureContext();
        if (this.buses[bus]) this.buses[bus].volume = volume;
    }

    /** How many sounds are playing. */
    get playingCount() { return this._playing.size; }

    /** Called once per frame by the engine host. */
    update(_dt) {
        // Ended sources clean themselves up through onended; nothing to poll.
    }

    /** Releases the context. */
    dispose() {
        this.stopAll();
        this.context?.close?.();
        this.context = null;
    }
}
