// -----------------------------------------------------------------------------
// Light3D — directional, point and spot lights.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Color, Vector3 } from '../math/index.js';

/** How a Light3D casts. */
export const LightType = Object.freeze({
    Directional: 'Directional',
    Point: 'Point',
    Spot: 'Spot',
});

/** A light source. Its transform's forward axis is the direction it points. */
export class Light3D extends Component {
    static schema = {
        type:         { type: P.Enum, values: ['Directional', 'Point', 'Spot'], default: 'Directional' },
        color:        { type: P.Color, default: '#FFFFFFFF' },
        intensity:    { type: P.Number, default: 1, min: 0 },
        range:        { type: P.Number, default: 10, min: 0 },
        spotAngle:    { type: P.Number, default: 30, min: 1, max: 179 },
        castsShadows: { type: P.Bool, default: false },
        shadowMapSize:{ type: P.Int, default: 1024 },
    };

    /** Every live light, so the renderer can gather them without a scene walk. */
    static all = [];

    constructor() {
        super();
        this.type = LightType.Directional;
        this.color = Color.white;
        this.intensity = 1;
        this.range = 10;
        this.spotAngle = 30;
        this.castsShadows = false;
        this.shadowMapSize = 1024;
    }

    awake() { Light3D.all.push(this); }

    onDestroy() {
        const i = Light3D.all.indexOf(this);
        if (i >= 0) Light3D.all.splice(i, 1);
    }

    /**
     * The direction the light shines, in world space.
     *
     * A light with no Transform3D — one placed by a 2D-only scene — points
     * straight down, so it still lights something rather than nothing.
     */
    getDirection() {
        const t = this.actor?.transform3D;
        return t ? t.forward : new Vector3(0, -1, 0);
    }

    /** World position. Meaningless for a directional light, which the shader ignores. */
    getPosition() {
        const t = this.actor?.transform3D;
        return t ? t.position : new Vector3(0, 0, 0);
    }
}
registerComponent(Light3D, { category: 'Rendering', summary: 'A directional, point or spot light.' });

/** Ambient light from the sky and the ground, used when no skybox is present. */
export class SkyLight extends Component {
    static schema = {
        skyColor:    { type: P.Color, default: '#7091C8FF' },
        groundColor: { type: P.Color, default: '#4A4438FF' },
        // Ambient is a fill, not a light source. At 1 the average of the sky and
        // ground colours lands near 0.4, which washes out every direct light in
        // the scene; a third of that matches the renderer's own ambient default.
        intensity:   { type: P.Number, default: 0.35, min: 0 },
    };

    /** The sky light the renderer uses. The last one to wake wins. */
    static active = null;

    constructor() {
        super();
        this.skyColor = Color.from('#7091C8');
        this.groundColor = Color.from('#4A4438');
        this.intensity = 0.35;
    }

    awake() { SkyLight.active = this; }
    onDestroy() { if (SkyLight.active === this) SkyLight.active = null; }

    /** The flat ambient term, the average of sky and ground. */
    getAverageAmbient() {
        return new Color(
            (this.skyColor.r + this.groundColor.r) / 2 * this.intensity,
            (this.skyColor.g + this.groundColor.g) / 2 * this.intensity,
            (this.skyColor.b + this.groundColor.b) / 2 * this.intensity,
            255);
    }
}
registerComponent(SkyLight, { category: 'Rendering', summary: 'Sky and ground ambient light.' });
