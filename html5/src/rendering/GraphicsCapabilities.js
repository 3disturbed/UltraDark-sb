// -----------------------------------------------------------------------------
// GraphicsCapabilities — what the browser the game is actually running in can do.
//
// The mirror of SexyBiscuit.Engine/Rendering/GraphicsCapabilities.cs.
//
// The settings menu builds its Advanced tab from this rather than from a fixed list,
// so a knob the running platform cannot honour is never shown. Half the knobs in
// GraphicsSettings exist on one engine only -- this renderer has no shadow pass, no
// post-processing and no 2D light map; the desktop has no device pixel ratio -- and a
// menu offering a shadow slider that does nothing is worse than one with no slider.
// -----------------------------------------------------------------------------

export class GraphicsCapabilities {
    constructor(values = {}) {
        this.renderer = 'WebGL2';
        this.adapter = 'Unknown';
        this.platform = 'Web';
        this.maxTextureSize = 2048;

        /** No shadow pass exists in this renderer at all. */
        this.shadows = false;

        /** No post-processing chain exists in this renderer at all. */
        this.postProcessing = false;

        /** No 2D light map exists in this renderer at all. */
        this.lighting2D = false;

        this.anisotropy = false;
        this.maxAnisotropy = 1;

        /** A page cannot resize the display or go fullscreen on its own terms. */
        this.displayControl = false;

        /** There is a device pixel ratio here, and capping it is the main perf knob. */
        this.pixelRatio = true;
        this.devicePixelRatio = 1;

        this.webgl2 = false;
        this.hardwareConcurrency = 0;
        this.deviceMemory = 0;

        /** Null until the Battery Status API answers, which not every browser has. */
        this.onBattery = null;

        Object.assign(this, values);
    }

    // -------------------------------------------------------------------------
    // Probing
    // -------------------------------------------------------------------------

    /**
     * Asks the browser what it can do. Safe with no `gl` and outside a browser
     * entirely, which is what the test runner has.
     */
    static probe(gl = null) {
        const nav = globalThis.navigator ?? {};
        const caps = new GraphicsCapabilities({
            platform: nav.platform ? `Web (${nav.platform})` : 'Web',
            devicePixelRatio: globalThis.devicePixelRatio ?? 1,
            hardwareConcurrency: nav.hardwareConcurrency ?? 0,
            deviceMemory: nav.deviceMemory ?? 0,
            webgl2: Boolean(gl),
        });

        if (!gl) {
            caps.renderer = 'Canvas 2D';
            return caps;
        }

        caps.maxTextureSize = gl.getParameter(gl.MAX_TEXTURE_SIZE) ?? 2048;

        // The unmasked strings need an extension, and a privacy-hardened browser
        // withholds it. An unknown GPU is a fact to report, not an error.
        const debugInfo = gl.getExtension?.('WEBGL_debug_renderer_info');
        if (debugInfo) {
            caps.adapter = gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) ?? 'Unknown';
        }

        const aniso = gl.getExtension?.('EXT_texture_filter_anisotropic');
        if (aniso) {
            caps.anisotropy = true;
            caps.maxAnisotropy = gl.getParameter(aniso.MAX_TEXTURE_MAX_ANISOTROPY_EXT) ?? 1;
        }

        return caps;
    }

    /**
     * Asks the Battery Status API whether the machine is on battery, and records it.
     *
     * Separate from `probe` because it is the one capability that is asynchronous and
     * the one that changes while the game is running: a laptop unplugged mid-session
     * is exactly when a battery-saver preset earns its place.
     */
    async probeBattery() {
        try {
            const battery = await globalThis.navigator?.getBattery?.();
            if (battery) this.onBattery = !battery.charging;
        } catch {
            this.onBattery = null;   // Denied or unsupported. Not knowing is fine.
        }
        return this.onBattery;
    }

    /**
     * A preset for a machine nothing is known about yet.
     *
     * Deliberately conservative. A first launch that stutters is the one a player
     * remembers, and turning quality up is a thing they will happily do themselves;
     * turning it down is a thing they do by closing the tab.
     */
    suggestPreset() {
        if (this.onBattery === true) return 'battery';
        if (!this.webgl2) return 'low';

        const cores = this.hardwareConcurrency;
        const memory = this.deviceMemory;

        // A phone reports a high pixel ratio and few cores; a desktop the reverse.
        const phoneish = this.devicePixelRatio >= 2.5 && cores > 0 && cores <= 6;
        if (phoneish) return 'low';

        if (cores >= 8 && memory >= 8 && this.maxTextureSize >= 8192) return 'high';
        if (cores >= 4 && this.maxTextureSize >= 4096) return 'medium';
        return 'low';
    }
}
