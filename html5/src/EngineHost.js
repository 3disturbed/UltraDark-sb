// -----------------------------------------------------------------------------
// EngineHost — the engine itself: services, the frame loop, and the surfaces.
//
// The frame order is the C# host's, step for step, including two details worth
// stating: `SceneManager.fixedUpdate` receives the *raw* fixed step while
// `Time.fixedDeltaTime` reports the scaled one, and time scale changes how many
// fixed steps run rather than how long each is.
//
// Rendering uses two stacked canvases. A canvas can hold either a 2D context or
// a WebGL one, never both, so the 3D scene is drawn to a WebGL canvas and the
// sprites, UI and gizmos to a transparent 2D canvas over it — the same order the
// C# host draws in, where RenderSystem3D runs and then the SpriteBatch pass.
// -----------------------------------------------------------------------------

import { Time } from './core/Time.js';
import { SceneManager } from './core/SceneManager.js';
import { CoroutineRunner } from './core/CoroutineRunner.js';
import { TimerManager } from './core/TimerManager.js';
import { PlayMode } from './core/PlayMode.js';
import { Color } from './math/index.js';
import { InputManager } from './input/InputManager.js';
import { AssetManager } from './assets/AssetManager.js';
import { AudioManager } from './audio/AudioManager.js';
import { SpriteBatch } from './rendering/SpriteBatch.js';
import { RenderSystem3D } from './rendering/RenderSystem3D.js';
import { Camera2D } from './rendering/Camera2D.js';
import { UiCanvas } from './ui/UiCanvas.js';
import { paintAll as paintUiCanvases } from './ui/UiPainter.js';
import { GraphicsMenu } from './ui/GraphicsMenu.js';
import { GraphicsSettings, loadGraphicsSettings, saveGraphicsSettings } from './rendering/GraphicsSettings.js';
import { GraphicsCapabilities } from './rendering/GraphicsCapabilities.js';
import { GraphicsBenchmark, describe as describeBenchmark } from './debug/GraphicsBenchmark.js';
import { PlayerPrefs } from './save/PlayerPrefs.js';
import { PhysicsSystem2D } from './physics/PhysicsSystem2D.js';
import { PhysicsSystem3D } from './physics/PhysicsSystem3D.js';
import { GameInstance } from './gameplay/GameInstance.js';
import { Tween } from './animation/Tween.js';

/** Settings a project's `ProjectSettings.json` maps onto. */
export class EngineConfig {
    constructor(init = {}) {
        this.windowTitle = init.windowTitle ?? 'SexyBiscuit';
        this.windowWidth = init.windowWidth ?? 1280;
        this.windowHeight = init.windowHeight ?? 720;
        this.fullscreen = init.fullscreen ?? false;
        this.vsync = init.vsync ?? true;

        // The quality preset a fresh install starts on, or empty to probe the machine.
        // Only a starting point either way: a player's saved choice wins.
        this.graphicsPreset = init.graphicsPreset ?? '';
        this.showCursor = init.showCursor ?? true;
        this.allowResize = init.allowResize ?? true;

        this.fixedTimestep = init.fixedTimestep ?? 1 / 60;
        this.maxFixedStepsPerFrame = init.maxFixedStepsPerFrame ?? 5;

        this.clearColour = Color.from(init.clearColour ?? '#6495ED');
        this.startScene = init.startScene ?? '';

        this.enable3D = init.enable3D ?? true;
        this.enablePhysics2D = init.enablePhysics2D ?? true;
        this.enablePhysics3D = init.enablePhysics3D ?? true;

        /** Scale the drawing buffer by the display's pixel ratio. */
        this.highDpi = init.highDpi ?? true;

        /** Cap on the pixel ratio. Phones report 3 or 4 and cannot fill it at 60fps. */
        this.maxPixelRatio = init.maxPixelRatio ?? 2;

        this.gameInstanceFactory = init.gameInstanceFactory ?? (() => new GameInstance());
    }

    /**
     * Reads a `ProjectSettings.json` document.
     *
     * Keys are matched case-insensitively, so both the PascalCase the templates
     * use and the camelCase the demo uses work — the same rule
     * `EngineConfig.FromProjectSettings` follows in C#.
     */
    static fromProjectSettings(json) {
        const dto = typeof json === 'string' ? JSON.parse(json) : (json ?? {});
        const lookup = new Map(Object.entries(dto).map(([k, v]) => [k.toLowerCase(), v]));
        const read = (...names) => {
            for (const name of names) {
                const value = lookup.get(name.toLowerCase());
                if (value !== undefined) return value;
            }
            return undefined;
        };

        return new EngineConfig({
            windowTitle: read('windowTitle', 'appName'),
            windowWidth: read('windowWidth'),
            windowHeight: read('windowHeight'),
            fullscreen: read('fullscreen'),
            vsync: read('vsync'),
            showCursor: read('showCursor'),
            allowResize: read('allowResize'),
            startScene: read('startScene'),
            graphicsPreset: read('graphicsPreset'),
            fixedTimestep: read('fixedTimestep'),
            maxFixedStepsPerFrame: read('maxFixedStepsPerFrame'),
            enable3D: read('enable3D'),
            enablePhysics2D: read('enablePhysics2D'),
            enablePhysics3D: read('enablePhysics3D'),
            clearColour: read('clearColour', 'clearColor'),
        });
    }
}

/** The engine. Create one, give it a mount element, and call `start`. */
export class EngineHost {
    /** The host the running game belongs to. Engine code reaches services here. */
    static current = null;

    /**
     * @param {object} [options]
     * @param {HTMLElement} [options.mount] Element the canvases are added to.
     * @param {EngineConfig|object} [options.config]
     * @param {string} [options.assetRoot] Base URL project-relative paths resolve against.
     */
    constructor({ mount = null, config = null, assetRoot = '' } = {}) {
        this.config = config instanceof EngineConfig ? config : new EngineConfig(config ?? {});
        this.mount = mount;

        // ---- Services ----
        this.assets = new AssetManager({ root: assetRoot });
        this.input = new InputManager();
        this.audio = new AudioManager(this.assets);
        this.timers = new TimerManager();
        this.coroutines = new CoroutineRunner();
        this.sceneManager = new SceneManager({ assets: this.assets, engine: this });
        this.gameInstance = this.config.gameInstanceFactory();

        // The managers actors reach through statics must be this host's, or a
        // coroutine started by an actor would be driven by a different runner.
        CoroutineRunner.instance = this.coroutines;
        TimerManager.instance = this.timers;

        // ---- Surfaces, created on `attach` ----
        this.canvas3D = null;
        this.canvas2D = null;
        this.gl = null;
        this.ctx = null;
        this.spriteBatch = null;
        this.renderer3D = null;

        this.isRunning = false;

        /**
         * Whether a tick advances the world.
         *
         * The loop keeps running when this is false — the view still renders and
         * the host stays responsive — but physics, updates, tweens, timers and
         * coroutines are all skipped. The editor turns it off while editing, so
         * opening a scene does not immediately drop its actors through the floor.
         */
        this.simulate = true;

        this._fixedAccumulator = 0;
        this._lastFrameTime = 0;
        this._rafHandle = null;
        this._resizeObserver = null;

        /** Called after every frame, with the frame's delta. */
        this.onFrame = null;

        EngineHost.current = this;
        this.gameInstance.internalInit();
    }

    /** The scene currently being updated and drawn. */
    get scene() { return this.sceneManager.activeScene; }

    /** The drawing buffer's size in device pixels. */
    get viewportWidth() { return this.canvas2D?.width ?? this.config.windowWidth; }
    get viewportHeight() { return this.canvas2D?.height ?? this.config.windowHeight; }

    // -------------------------------------------------------------------------
    // Surfaces
    // -------------------------------------------------------------------------

    /**
     * Builds the canvases and wires input to them.
     * @param {HTMLElement} [mount] Defaults to the element given to the constructor.
     */
    attach(mount = this.mount) {
        if (typeof document === 'undefined') return this;   // headless
        this.mount = mount ?? document.body;

        const style = 'position:absolute; inset:0; width:100%; height:100%; display:block;';

        this.canvas3D = document.createElement('canvas');
        this.canvas3D.className = 'sb-canvas-3d';
        this.canvas3D.style.cssText = style;

        this.canvas2D = document.createElement('canvas');
        this.canvas2D.className = 'sb-canvas-2d';
        // The 2D layer sits over the 3D one and must let it show through, so it
        // is never cleared to an opaque colour.
        this.canvas2D.style.cssText = `${style} background:transparent;`;

        // `touch-action: none` is what stops iOS scrolling and zooming the page
        // out from under a drag; preventDefault alone is not enough on Safari.
        for (const canvas of [this.canvas3D, this.canvas2D]) {
            canvas.style.touchAction = 'none';
            canvas.style.userSelect = 'none';
        }

        if (getComputedStyle(this.mount).position === 'static') {
            this.mount.style.position = 'relative';
        }
        this.mount.append(this.canvas3D, this.canvas2D);

        if (this.config.enable3D) {
            this.gl = this.canvas3D.getContext('webgl2', {
                alpha: false,
                antialias: true,
                depth: true,
                // Phones default to the low-power GPU; a 3D scene wants the other one.
                powerPreference: 'high-performance',
            });

            if (this.gl) {
                this.renderer3D = new RenderSystem3D(this.gl);
                this.renderer3D.initialize();
            } else {
                console.warn('[EngineHost] WebGL2 is unavailable; running 2D only.');
                this.canvas3D.style.display = 'none';
            }
        } else {
            this.canvas3D.style.display = 'none';
        }

        this.ctx = this.canvas2D.getContext('2d');
        this.spriteBatch = new SpriteBatch(this.ctx);

        // Screen-space UI, drawn after the world and outside the camera
        // transform. Scripts reach it through the `UI` global.
        // Sized from the config rather than from the canvas: this runs BEFORE the
        // first resize(), and a freshly created <canvas> with no width/height
        // attributes is 300x150. Taking that would leave UI.width at 300 for the
        // life of the page. resize() is what keeps it right from here on.

        // Input listens on the top canvas: it is the one the pointer actually hits.
        this.input.attach(this.canvas2D);
        if (!this.config.showCursor) this.input.hideCursor();

        // After the WebGL context, because the probe asks it what it can do.
        this._graphicsMenu = null;
        this._benchmark = null;

        /** The key that opens the settings screen, or null to disable it. */
        this.graphicsMenuKey = 'F10';

        this.initialiseGraphics();

        this.resize();
        this._watchResize();

        return this;
    }

    /** Matches the drawing buffers to the mount's size and the display's density. */
    resize() {
        if (!this.canvas2D) return;

        const rect = this.mount.getBoundingClientRect();
        const cssWidth = Math.max(1, Math.round(rect.width || this.config.windowWidth));
        const cssHeight = Math.max(1, Math.round(rect.height || this.config.windowHeight));

        const ratio = this.config.highDpi
            ? Math.min(globalThis.devicePixelRatio || 1, this.config.maxPixelRatio)
            : 1;

        const width = Math.round(cssWidth * ratio);
        const height = Math.round(cssHeight * ratio);

        for (const canvas of [this.canvas2D, this.canvas3D]) {
            if (!canvas) continue;
            if (canvas.width === width && canvas.height === height) continue;
            canvas.width = width;
            canvas.height = height;
        }

        this.input.touch.setScreenSize(width, height);
        for (const camera of Camera2D.all) camera.setViewport(width, height);

        // The UI is laid out against its own viewport, so it has to be told too.
        // Without this every anchor is measured against whatever size the canvas
        // happened to be when the host was built -- so `center` resolves near the
        // top-left corner and a panel sized UI.width x UI.height covers a
        // fraction of the screen. Nothing throws; it just looks wrong, and only
        // once a game puts something on screen.
    }

    _watchResize() {
        if (typeof ResizeObserver === 'undefined') {
            globalThis.addEventListener?.('resize', () => this.resize());
            return;
        }
        this._resizeObserver = new ResizeObserver(() => this.resize());
        this._resizeObserver.observe(this.mount);
    }

    // -------------------------------------------------------------------------
    // Physics factories, called by the SceneManager for each scene
    // -------------------------------------------------------------------------

    createPhysics2D(scene) {
        return this.config.enablePhysics2D ? new PhysicsSystem2D({ scene }) : null;
    }

    createPhysics3D(scene) {
        return this.config.enablePhysics3D ? new PhysicsSystem3D({ scene }) : null;
    }

    // -------------------------------------------------------------------------
    // Frame
    // -------------------------------------------------------------------------

    /** Starts the requestAnimationFrame loop. */
    start() {
        if (this.isRunning) return this;
        this.isRunning = true;
        this._lastFrameTime = now();

        const loop = () => {
            if (!this.isRunning) return;
            this._rafHandle = requestAnimationFrame(loop);

            const time = now();
            const rawDelta = (time - this._lastFrameTime) / 1000;
            this._lastFrameTime = time;

            this.tick(rawDelta);
            this.render();
            this.onFrame?.(rawDelta);
        };

        this._rafHandle = requestAnimationFrame(loop);
        return this;
    }

    /** Stops the loop. The scene is left intact. */
    stop() {
        this.isRunning = false;
        if (this._rafHandle != null) cancelAnimationFrame(this._rafHandle);
        this._rafHandle = null;
    }

    /**
     * Advances the engine by one frame.
     *
     * @param {number} rawDeltaSeconds Wall-clock seconds since the last frame.
     * @param {boolean} [pumpInput=true] False when stepping a paused editor.
     */
    tick(rawDeltaSeconds, pumpInput = true) {
        Time.advance(rawDeltaSeconds);

        const dt = Time.deltaTime;
        const unscaledDt = Time.unscaledDeltaTime;

        // Input runs on unscaled time so menus stay live while the game is paused.
        if (pumpInput) this.input.update(unscaledDt);

        // Before the game ticks, so a script reading node.clicked in update() sees this
        // frame's click rather than the previous one's. Ahead of the simulate check as
        // well: a paused editor still has to be able to drive a menu.
        if (pumpInput) this._updateUiCanvases(unscaledDt);

        if (pumpInput && this.graphicsMenuKey && this.input?.isKeyPressed?.(this.graphicsMenuKey)) {
            this.toggleGraphicsMenu();
        }

        // Unscaled, so a game paused behind the menu still drives it.
        this._graphicsMenu?.tick();
        this._tickBenchmark(unscaledDt);

        // Not simulating: the clock and input still advance, so the editor's own
        // camera and its panels keep working, but the world is left alone.
        if (!this.simulate) {
            this._fixedAccumulator = 0;
            return;
        }

        this.gameInstance.internalTick(dt);

        const step = this.config.fixedTimestep;
        Time.fixedDeltaTime = step * Time.timeScale;

        // Time scale feeds the accumulator, so slowing time runs fewer fixed
        // steps rather than shortening each one.
        this._fixedAccumulator += dt;

        let steps = 0;
        while (this._fixedAccumulator >= step && steps < this.config.maxFixedStepsPerFrame) {
            const scene = this.scene;
            scene?.physics2D?.fixedStep(step);
            scene?.physics3D?.fixedStep(step);
            this.sceneManager.fixedUpdate(step);

            this._fixedAccumulator -= step;
            steps++;
        }

        // A frame so long that the accumulator cannot be caught up is dropped
        // rather than replayed: replaying it makes the next frame longer still.
        if (this._fixedAccumulator > step * this.config.maxFixedStepsPerFrame) {
            this._fixedAccumulator = 0;
        }

        this.sceneManager.update(dt);
        Tween.updateAll(dt, unscaledDt);
        this.timers.tick(dt, unscaledDt);
        this.coroutines.tick(dt, unscaledDt);
        this.sceneManager.lateUpdate(dt);
        this.audio.update(dt);
    }

    /** Draws the 3D pass, then the 2D pass over it. */
    render() {
        const scene = this.scene;
        if (!scene) return;

        if (this.gl && this.renderer3D && this.config.enable3D) {
            const [r, g, b] = this.config.clearColour.toFloatArray();
            this.gl.clearColor(r, g, b, 1);
            this.gl.clear(this.gl.COLOR_BUFFER_BIT | this.gl.DEPTH_BUFFER_BIT);
            this.renderer3D.render(scene, {
                width: this.canvas3D.width, height: this.canvas3D.height,
            });
        }

        if (!this.ctx) return;

        this.ctx.setTransform(1, 0, 0, 1, 0, 0);
        this.ctx.clearRect(0, 0, this.canvas2D.width, this.canvas2D.height);

        // Without a 3D pass there is nothing behind the 2D layer, so it paints
        // the clear colour itself rather than leaving the page showing through.
        if (!this.gl || !this.config.enable3D) {
            this.ctx.fillStyle = this.config.clearColour.toCss();
            this.ctx.fillRect(0, 0, this.canvas2D.width, this.canvas2D.height);
        }

        const camera = Camera2D.main;
        camera?.setViewport(this.canvas2D.width, this.canvas2D.height);

        this.spriteBatch.begin({ transform: camera?.getViewMatrix() ?? null });
        this.sceneManager.draw(this.spriteBatch);
        this.spriteBatch.end();

        // UI last and untransformed: it is screen space, so a camera that has
        // panned, zoomed or shaken must not take the HUD with it.
        this._drawUiCanvases();
    }

    // -------------------------------------------------------------------------
    // Graphics settings
    // -------------------------------------------------------------------------

    /**
     * Picks the settings this session starts on, and applies them.
     *
     * A saved choice always wins. Failing that the project may pin a preset -- a
     * pixel-art 2D game runs at Ultra on a netbook, and probing it down would turn its
     * own post-processing off for no reason -- and only when neither exists is the
     * machine measured. Probing on every launch would silently undo the choice of
     * anybody who had deliberately turned something down.
     */
    initialiseGraphics() {
        this.capabilities = GraphicsCapabilities.probe(this.gl ?? null);

        let settings = loadGraphicsSettings(PlayerPrefs);
        if (!settings) {
            settings = new GraphicsSettings();
            settings.applyPreset(this.config.graphicsPreset || this.capabilities.suggestPreset());
        }

        this.applyGraphics(settings);

        // A laptop unplugged mid-session is exactly when a battery preset earns its
        // place, so the answer is asked for even though it arrives late.
        this.capabilities.probeBattery?.();
        return settings;
    }

    /** Adopts a settings object and pushes every value to its real owner. */
    applyGraphics(settings) {
        this.graphics = settings;
        settings.apply(this);
    }

    /**
     * The engine's graphics settings screen, built on first use.
     *
     * Every game gets this without asking for it, which is the point: a settings screen
     * each game writes for itself is a settings screen most games never write.
     */
    get graphicsMenu() {
        if (!this._graphicsMenu) this._graphicsMenu = this._buildGraphicsMenu();
        return this._graphicsMenu;
    }

    /** Whether the settings screen is currently up. */
    get isGraphicsMenuOpen() { return this._graphicsMenu?.isOpen ?? false; }

    /** Opens the settings screen. */
    openGraphicsMenu() { this.graphicsMenu.open(); }

    /** Opens the settings screen, or closes it if it is already up. */
    toggleGraphicsMenu() {
        if (this.isGraphicsMenuOpen) this._graphicsMenu.close();
        else this.graphicsMenu.open();
    }

    _buildGraphicsMenu() {
        if (!this.graphics) this.initialiseGraphics();

        this._benchmark = new GraphicsBenchmark();

        const menu = new GraphicsMenu(this.graphics, this.capabilities);
        menu.onChanged = (settings) => {
            this.applyGraphics(settings);
            saveGraphicsSettings(settings, PlayerPrefs);
        };
        menu.onBenchmark = () => this._startBenchmark();
        return menu;
    }

    /**
     * Switches to the benchmark preset and starts timing.
     *
     * The player's own settings are kept and put back when the run ends. A benchmark
     * that left the machine on Ultra afterwards would be a benchmark that made every
     * laptop unplayable to run once.
     */
    _startBenchmark() {
        if (!this._benchmark || !this._graphicsMenu) return;

        this._benchmark.restore = this.graphics.clone();

        const running = this.graphics.clone();
        running.applyPreset('benchmark');
        this.applyGraphics(running);

        this._graphicsMenu.adopt(running, this.capabilities);
        this._graphicsMenu.note = 'Running…';
        this._graphicsMenu.refresh();
        this._benchmark.start();
    }

    _tickBenchmark(unscaledDt) {
        if (!this._benchmark?.isRunning) return;

        if (this._graphicsMenu) {
            this._graphicsMenu.note = `Running… ${Math.round(this._benchmark.progress * 100)}%`;
            this._graphicsMenu.refresh();
        }

        const result = this._benchmark.tick(unscaledDt, this.renderer3D?.stats ?? null);
        if (!result) return;

        if (this._benchmark.restore) this.applyGraphics(this._benchmark.restore);

        if (this._graphicsMenu) {
            this._graphicsMenu.adopt(this.graphics, this.capabilities);
            this._graphicsMenu.note = describeBenchmark(result);
            this._graphicsMenu.refresh();
        }
    }

    // -------------------------------------------------------------------------
    // The retained UI
    // -------------------------------------------------------------------------

    /**
     * Lays out every UiCanvas and feeds it this frame's input.
     *
     * Layout happens here rather than in the paint pass because hit-testing needs
     * this frame's rectangles, and the paint pass runs after the game has had its
     * turn. A clean tree costs the dirty-flag check and nothing more.
     */
    _updateUiCanvases(unscaledDt) {
        if (UiCanvas.all.length === 0) return;

        const frame = this._buildUiInputFrame(unscaledDt);
        const width = this.canvas2D?.width ?? this.config.windowWidth;
        const height = this.canvas2D?.height ?? this.config.windowHeight;

        // A copy, because a script reacting to a click may destroy a canvas.
        for (const canvas of [...UiCanvas.all]) {
            canvas.setViewport(width, height);
            canvas.layout();
            canvas.input.update(frame);
        }
    }

    _buildUiInputFrame(unscaledDt) {
        const input = this.input;
        const pad = input?.isGamepadConnected?.() ? input.getGamepad(0) : null;
        const stick = pad?.leftStick ?? { x: 0, y: 0 };
        const typed = input?.typedText ?? '';

        // The stick's y is up-positive and the UI's is down-positive, so the axis is
        // flipped once here rather than in every direction test downstream.
        return {
            deltaTime: unscaledDt,
            pointer: { x: input?.mousePosition?.x ?? -1, y: input?.mousePosition?.y ?? -1 },
            pointerDelta: { x: input?.mouseDelta?.x ?? 0, y: input?.mouseDelta?.y ?? 0 },
            pointerDown: input?.isMouseButtonDown?.() ?? false,
            pointerIsTouch: (input?.touch?.touches?.length ?? 0) > 0,
            wheel: input?.scrollDelta ?? 0,
            navAxis: { x: stick.x, y: -stick.y },
            navUp: input?.isKeyPressed?.('ArrowUp') || (pad?.isButtonPressed?.('DPadUp') ?? false),
            navDown: input?.isKeyPressed?.('ArrowDown') || (pad?.isButtonPressed?.('DPadDown') ?? false),
            navLeft: input?.isKeyPressed?.('ArrowLeft') || (pad?.isButtonPressed?.('DPadLeft') ?? false),
            navRight: input?.isKeyPressed?.('ArrowRight') || (pad?.isButtonPressed?.('DPadRight') ?? false),
            confirm: input?.isKeyPressed?.('Enter') || input?.isKeyPressed?.('Space')
                || (pad?.isButtonPressed?.('A') ?? false),
            cancel: input?.isKeyPressed?.('Escape') || (pad?.isButtonPressed?.('B') ?? false),
            typed: typed.replaceAll('\b', ''),
            backspace: typed.includes('\b'),
        };
    }

    /**
     * Paints every canvas over the world, outside the camera transform.
     *
     * The UI this replaces drew through Component.draw, inside the camera-transformed
     * world batch, so a HUD panned, zoomed and shook with the camera. Painting here is
     * the whole reason UiCanvas is not a drawing component.
     */
    _drawUiCanvases() {
        if (!this.ctx || UiCanvas.all.length === 0) return;

        const ring = UiCanvas.all[0].input.focus.modes.showFocusRing;
        paintUiCanvases(this.ctx, this.canvas2D.width, this.canvas2D.height, {
            showFocusRing: ring,
            texture: (path) => this.assets?.get(path) ?? null,
            time: Time.realtimeSinceStartup,
        });
    }

    // -------------------------------------------------------------------------
    // Project loading
    // -------------------------------------------------------------------------

    /**
     * Opens a project: its settings, then its start scene.
     *
     * @param {object} [options]
     * @param {string} [options.settingsPath='ProjectSettings.json']
     * @param {string} [options.scene] Overrides the settings' start scene.
     */
    async loadProject({ settingsPath = 'ProjectSettings.json', scene = null } = {}) {
        try {
            const settings = await this.assets.loadJson(settingsPath);
            const config = EngineConfig.fromProjectSettings(settings);
            // Keep the surfaces the host was built with; only the game-facing
            // settings come from the file.
            Object.assign(this.config, {
                windowTitle: config.windowTitle,
                startScene: config.startScene,
                fixedTimestep: config.fixedTimestep,
                maxFixedStepsPerFrame: config.maxFixedStepsPerFrame,
                enable3D: config.enable3D && this.config.enable3D,
                enablePhysics2D: config.enablePhysics2D,
                enablePhysics3D: config.enablePhysics3D,
            });
            if (typeof document !== 'undefined') document.title = config.windowTitle;
        } catch {
            // A project without settings is fine; the defaults are usable.
        }

        const startScene = scene ?? this.config.startScene;
        if (startScene) return this.sceneManager.loadSceneAsync(startScene);

        return this.sceneManager.adoptScene(this.sceneManager.createScene('Untitled'));
    }

    /** Spawns a prefab into the active scene. */
    async instantiate(prefabPath, position = null) {
        const { buildActor } = await import('./scene/SceneSerializer.js');
        const dto = await this.assets.loadJson(prefabPath);
        const actor = buildActor(dto);
        if (position) actor.transform.localPosition = position;
        this.scene?.addActor(actor);
        return actor;
    }

    /** Enters or leaves play mode. */
    setPlayMode(active) { PlayMode.isActive = Boolean(active); }

    /** Tears everything down. */
    dispose() {
        this.stop();
        this.input.detach();
        this.gameInstance.internalShutdown();
        this.sceneManager.dispose();
        this.renderer3D?.dispose();
        this.audio.dispose();
        this.assets.unloadAll();
        this._resizeObserver?.disconnect();

        this.canvas2D?.remove();
        this.canvas3D?.remove();

        if (EngineHost.current === this) EngineHost.current = null;
    }
}

function now() {
    return typeof performance !== 'undefined' ? performance.now() : Date.now();
}
