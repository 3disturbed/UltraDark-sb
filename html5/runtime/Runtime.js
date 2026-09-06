// -----------------------------------------------------------------------------
// Runtime — the standalone player. Boots a project and runs it full-screen.
//
// Everything specific to shipping a game in a browser lives here rather than in
// the engine: the loading screen, the audio-unlock tap, pausing when the tab is
// hidden, the fullscreen and orientation controls, and the on-screen sticks a
// phone needs.
// -----------------------------------------------------------------------------

import { EngineHost, EngineConfig } from '../src/EngineHost.js';
import { TouchControls } from '../src/input/TouchControls.js';
import { PlayMode } from '../src/core/PlayMode.js';
import { Time } from '../src/core/Time.js';

/** Runs a SexyBiscuit project in a browser. */
export class Runtime {
    /**
     * @param {object} [options]
     * @param {HTMLElement} [options.mount] Defaults to `#game` or the body.
     * @param {string} [options.projectRoot=''] Base URL the project's files sit under.
     * @param {string} [options.scene] Overrides the project's start scene.
     * @param {boolean} [options.touchControls=true] Show sticks on a touch device.
     * @param {boolean} [options.pauseWhenHidden=true] Stop the loop in a background tab.
     */
    constructor(options = {}) {
        this.options = {
            projectRoot: '',
            touchControls: true,
            pauseWhenHidden: true,
            showStats: false,
            ...options,
        };

        this.mount = null;
        /** @type {?EngineHost} */
        this.engine = null;
        /** @type {?TouchControls} */
        this.controls = null;

        this._overlay = null;
        this._statsElement = null;
        this._pausedByVisibility = false;
    }

    /** Boots the engine, loads the project and starts the loop. */
    async start() {
        this.mount = this.options.mount
            ?? document.getElementById('game')
            ?? document.body;

        this._buildOverlay();
        this._setStatus('Starting the engine…');

        // Play mode is always on in the player; only the editor turns it off.
        PlayMode.isActive = true;

        this.engine = new EngineHost({
            mount: this.mount,
            assetRoot: this.options.projectRoot,
            config: new EngineConfig(this.options.config ?? {}),
        });
        this.engine.attach();

        this._setStatus('Loading the project…');

        try {
            await this.engine.loadProject({ scene: this.options.scene });
        } catch (err) {
            this._fail(err);
            return this;
        }

        if (this.options.touchControls) {
            this.controls = new TouchControls(this.engine.input, {
                mount: this.mount,
                leftStick: true,
                rightStick: this.engine.config.enable3D,
                buttons: [{ action: 'Jump', label: '▲' }, { action: 'Attack', label: '●' }],
            }).attach();
        }

        if (this.options.showStats) this._buildStats();

        this.engine.onFrame = () => {
            this.controls?.update();
            if (this._statsElement) this._updateStats();
        };

        this._installLifecycleHandlers();
        this._hideOverlay();
        this.engine.start();

        return this;
    }

    /** Pauses the loop. The scene is left where it is. */
    pause() { this.engine?.stop(); }

    /** Resumes after a pause, without counting the gap as one enormous frame. */
    resume() {
        if (!this.engine || this.engine.isRunning) return;
        this.engine._lastFrameTime = performance.now();
        this.engine.start();
    }

    /** Stops the loop and releases everything. */
    dispose() {
        this.engine?.dispose();
        this.controls?.detach();
        this._overlay?.remove();
        this._statsElement?.remove();
    }

    // -------------------------------------------------------------------------
    // Browser lifecycle
    // -------------------------------------------------------------------------

    _installLifecycleHandlers() {
        if (this.options.pauseWhenHidden) {
            document.addEventListener('visibilitychange', () => {
                if (document.hidden) {
                    this._pausedByVisibility = this.engine.isRunning;
                    this.pause();
                } else if (this._pausedByVisibility) {
                    this._pausedByVisibility = false;
                    this.resume();
                }
            });
        }

        // A tap anywhere is the gesture the browser needs before audio may start.
        const unlock = () => this.engine.audio.unlock();
        window.addEventListener('pointerdown', unlock, { once: true });
        window.addEventListener('keydown', unlock, { once: true });
    }

    /** Goes full-screen, and locks to landscape where the browser allows it. */
    async enterFullscreen({ orientation = null } = {}) {
        const element = this.mount;
        try {
            // Safari on iPhone exposes only the prefixed form, and only on some
            // elements; a failure here is not worth interrupting the game for.
            await (element.requestFullscreen?.() ?? element.webkitRequestFullscreen?.());
        } catch { /* the user can play windowed */ }

        if (orientation && screen.orientation?.lock) {
            try { await screen.orientation.lock(orientation); } catch { /* desktop, or refused */ }
        }
    }

    exitFullscreen() {
        document.exitFullscreen?.() ?? document.webkitExitFullscreen?.();
    }

    // -------------------------------------------------------------------------
    // Overlay
    // -------------------------------------------------------------------------

    _buildOverlay() {
        this._overlay = document.createElement('div');
        this._overlay.className = 'sb-loading';
        this._overlay.innerHTML = `
            <div class="sb-loading-inner">
                <div class="sb-loading-mark">SexyBiscuit</div>
                <div class="sb-loading-status">Starting…</div>
                <div class="sb-loading-bar"><i></i></div>
            </div>`;
        this.mount.appendChild(this._overlay);
    }

    _setStatus(text) {
        const status = this._overlay?.querySelector('.sb-loading-status');
        if (status) status.textContent = text;
    }

    _hideOverlay() {
        if (!this._overlay) return;
        this._overlay.classList.add('is-done');
        setTimeout(() => this._overlay?.remove(), 400);
    }

    _fail(error) {
        console.error('[Runtime]', error);
        this._setStatus('');

        const inner = this._overlay?.querySelector('.sb-loading-inner');
        if (!inner) return;

        inner.innerHTML = `
            <div class="sb-loading-mark">Could not start</div>
            <pre class="sb-loading-error"></pre>`;
        inner.querySelector('.sb-loading-error').textContent = String(error?.message ?? error);
    }

    _buildStats() {
        this._statsElement = document.createElement('div');
        this._statsElement.className = 'sb-stats';
        this.mount.appendChild(this._statsElement);
    }

    _updateStats() {
        const stats = this.engine.renderer3D?.stats;
        this._statsElement.textContent = [
            `${Time.fps.toFixed(0)} fps`,
            stats ? `${stats.renderersDrawn}/${stats.renderersTotal} drawn` : null,
            stats ? `${stats.triangles} tris` : null,
            `${this.engine.spriteBatch?.drawCalls ?? 0} sprites`,
        ].filter(Boolean).join('  ·  ');
    }
}

/**
 * Boots a runtime from the page's own configuration.
 *
 * Reads `data-` attributes off the mount element, so a project ships an
 * `index.html` with no JavaScript of its own:
 *
 *     <div id="game" data-project="." data-scene="Scenes/Level1"></div>
 */
export async function boot(overrides = {}) {
    const mount = overrides.mount
        ?? document.getElementById('game')
        ?? document.body;

    const runtime = new Runtime({
        mount,
        projectRoot: mount.dataset?.project ?? '',
        scene: mount.dataset?.scene || null,
        showStats: mount.dataset?.stats === 'true',
        touchControls: mount.dataset?.touchControls !== 'false',
        ...overrides,
    });

    await runtime.start();
    globalThis.sexybiscuit = runtime;
    return runtime;
}
