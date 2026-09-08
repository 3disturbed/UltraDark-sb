// -----------------------------------------------------------------------------
// GraphicsSettings — every quality knob the engine has, in one place.
//
// The mirror of SexyBiscuit.Engine/Rendering/GraphicsSettings.cs.
//
// One object rather than a scattering of properties on the renderer, the config and
// the canvas, because a settings menu has to be able to read them all, write them all
// and put them in a file. `apply` is the only place that knows where each one lives.
//
// The native-only and web-only groups both exist on both engines on purpose: a web
// export is produced on a desktop, and a preset that lost its maxPixelRatio on the way
// through would ship a phone build at three times the resolution it can fill. The
// capability probe, not this file, decides what a player is shown.
// -----------------------------------------------------------------------------

import presetTable from './graphics-presets.json' with { type: 'json' };
import { setTessellation } from './PrimitiveMesh.js';
import { Camera3D } from './Camera3D.js';

/** How much a shadow costs. The C# enum knows the same list, in order. */
export const ShadowQuality = Object.freeze({
    Off: 'Off', Low: 'Low', Medium: 'Medium', High: 'High', Ultra: 'Ultra',
});

/** How a texture is sampled. Anisotropic falls back where it is unsupported. */
export const TextureFiltering = Object.freeze({
    Point: 'Point', Bilinear: 'Bilinear', Trilinear: 'Trilinear', Anisotropic: 'Anisotropic',
});

/** How much the 2D light map costs, which is mostly its resolution. */
export const Lighting2DQuality = Object.freeze({
    Off: 'Off', Low: 'Low', Medium: 'Medium', High: 'High',
});

/** The key the whole object is stored under, as one JSON blob. */
export const PREFS_KEY = 'Graphics';

export class GraphicsSettings {
    constructor() {
        // --- shared ---
        this.preset = 'medium';
        this.renderScale = 1;
        this.vsync = true;
        this.frameCap = 0;
        this.shadows = ShadowQuality.Low;
        this.shadowDistance = 50;
        this.maxLightsPerObject = 4;
        this.tessellationRadial = 24;
        this.tessellationRings = 16;
        this.drawDistance = 1000;
        this.frustumCulling = true;
        this.sortOpaqueFrontToBack = true;
        this.skybox = true;
        this.particleDensity = 1;
        this.postProcessing = true;
        this.bloom = true;
        this.vignette = false;
        this.colourGrade = true;
        this.scanline = false;
        this.lighting2D = Lighting2DQuality.Medium;
        this.textureFiltering = TextureFiltering.Trilinear;
        this.anisotropy = 4;
        this.streamingRadius = 2;
        this.maxLoadsPerFrame = 1;

        // --- native only ---
        this.msaa = 2;
        this.shadowMapSize = 1024;
        this.fullscreen = false;
        this.resolutionWidth = 0;
        this.resolutionHeight = 0;

        // --- web only ---
        this.maxPixelRatio = 2;
        this.highDpi = true;
        this.antialias = true;
        this.powerPreference = 'high-performance';
        this.shaderPrecision = 'highp';
        this.throttleWhenHidden = true;
    }

    // -------------------------------------------------------------------------
    // Presets
    // -------------------------------------------------------------------------

    /** The preset table, shared with the C# engine as one JSON file. */
    static get presets() { return presetTable.presets; }

    /** The preset a game starts on when nothing else has decided. */
    static get defaultPreset() { return presetTable.default; }

    /** Frame rate to preset, best first. The first threshold cleared wins. */
    static get thresholds() { return presetTable.thresholds; }

    /** A preset by id, case-insensitively, or null. */
    static findPreset(id) {
        const wanted = String(id ?? '').toLowerCase();
        return presetTable.presets.find((p) => p.id.toLowerCase() === wanted) ?? null;
    }

    /** The best preset a machine hitting this frame rate should be offered. */
    static suggest(averageFps) {
        for (const threshold of presetTable.thresholds) {
            if (averageFps >= threshold.minFps) return threshold.preset;
        }
        return presetTable.presets[0]?.id ?? presetTable.default;
    }

    /**
     * How far above the low's own answer an average is allowed to reach.
     *
     * Two rungs is the allowance for a benchmark's own noise -- a single scheduler
     * hiccup should not cost a good machine two quality levels -- while still stopping
     * a badly stuttering one being offered what its average alone would earn.
     */
    static get stutterAllowance() { return 2; }

    /**
     * The preset a machine should be offered, judged on its average and its stutter.
     *
     * Stutter is what a player actually notices. A machine averaging a hundred frames a
     * second with a one-percent low of six is not a hundred-frame machine; it is one
     * that hitches for a whole second in every six, and offering it the preset its
     * average earns would be offering it the preset it just failed at.
     */
    static suggestFor(averageFps, onePercentLowFps) {
        const indexOf = (id) => Math.max(0, presetTable.presets.findIndex((p) => p.id === id));

        const byAverage = indexOf(GraphicsSettings.suggest(averageFps));
        const byLow = indexOf(GraphicsSettings.suggest(onePercentLowFps));

        const last = Math.max(0, presetTable.presets.length - 1);
        const chosen = Math.min(last, Math.max(0, Math.min(byAverage, byLow + GraphicsSettings.stutterAllowance)));

        return presetTable.presets[chosen]?.id ?? presetTable.default;
    }

    /** Overwrites every field from a preset. Unknown ids are ignored. */
    applyPreset(id) {
        const preset = GraphicsSettings.findPreset(id);
        if (!preset) return false;

        Object.assign(this, preset.shared, preset.native, preset.web);
        this.preset = preset.id;
        return true;
    }

    /** A copy, for previewing a change without committing to it. */
    clone() { return Object.assign(new GraphicsSettings(), this); }

    // -------------------------------------------------------------------------
    // Applying
    // -------------------------------------------------------------------------

    /**
     * Pushes every setting to whatever actually owns it.
     *
     * The one place that knows where each knob lives, which is the reason this class
     * exists: they are otherwise spread across the renderer, the mesh cache, the
     * camera, every emitter and the host's own canvas sizing.
     */
    apply(host) {
        applyGlobals(this);
        if (!host) return;

        const renderer = host.renderer3D;
        if (renderer) {
            renderer.enableFrustumCulling = this.frustumCulling;
            renderer.sortOpaqueFrontToBack = this.sortOpaqueFrontToBack;
            renderer.renderSkybox = this.skybox;
            renderer.maxLightsPerObject = Math.min(Math.max(1, this.maxLightsPerObject), 8);
        }

        // Resolution scale on the web is the drawing-buffer size, so it goes through
        // the same pixel-ratio ceiling that stops a phone filling a 3x buffer.
        if (host.config) {
            host.config.highDpi = this.highDpi;
            host.config.maxPixelRatio = this.maxPixelRatio * this.renderScale;
        }
        host.resize?.();
    }
}

/** The settings that live on module state, and so apply with no host at all. */
export function applyGlobals(settings) {
    // setTessellation clears the primitive cache itself, which it has to: the cache is
    // keyed on the shape and not on the segment counts, so meshes built at the old
    // numbers would otherwise survive and the change would be invisible.
    setTessellation(settings.tessellationRadial, settings.tessellationRings);

    const camera = Camera3D.main;
    if (camera) camera.farClip = settings.drawDistance;
}

// -----------------------------------------------------------------------------
// Persistence
// -----------------------------------------------------------------------------

/** Writes the settings through PlayerPrefs. */
export function saveGraphicsSettings(settings, prefs) {
    prefs.setString(PREFS_KEY, JSON.stringify(settings));
    prefs.save();
}

/**
 * Reads the settings back, or null when the player has never chosen any.
 *
 * Null rather than defaults, because "never chosen" is what triggers the capability
 * probe on a first run -- and a probe that ran every launch would silently undo the
 * choice of anybody who had deliberately turned something down.
 */
export function loadGraphicsSettings(prefs) {
    const json = prefs.getString(PREFS_KEY, '');
    if (!json) return null;

    try {
        return Object.assign(new GraphicsSettings(), JSON.parse(json));
    } catch {
        return null;   // A corrupt blob is a first run, not a crash.
    }
}
