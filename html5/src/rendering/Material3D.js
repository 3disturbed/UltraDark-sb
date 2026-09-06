// -----------------------------------------------------------------------------
// Material3D — metallic/roughness surface parameters.
//
// Serialises to the same nested object `Material3DJsonConverter` writes in C#:
// camelCase keys, `albedoColor` as a hex string, and texture paths written only
// when set. Textures themselves are resolved later, once an asset manager and a
// GL context exist.
// -----------------------------------------------------------------------------

import { Color } from '../math/index.js';

/** A surface description. Not a component: a MeshRenderer holds a list of them. */
export class Material3D {
    constructor(init = {}) {
        this.albedoColor = Color.from(init.albedoColor ?? '#FFFFFFFF');
        this.metallic = init.metallic ?? 0;
        this.roughness = init.roughness ?? 0.5;
        this.emissiveIntensity = init.emissiveIntensity ?? 0;

        // Paths are what a scene file stores; the textures are loaded from them.
        this.albedoMapPath = init.albedoMap ?? null;
        this.normalMapPath = init.normalMap ?? null;
        this.metallicMapPath = init.metallicMap ?? null;
        this.roughnessMapPath = init.roughnessMap ?? null;
        this.emissiveMapPath = init.emissiveMap ?? null;
        this.shaderPath = init.shader ?? null;

        /** @type {?import('../assets/Texture2D.js').Texture2D} */
        this.albedoMap = null;
        this.normalMap = null;
        this.metallicMap = null;
        this.roughnessMap = null;
        this.emissiveMap = null;

        /** Alpha below one puts the renderer's transparent pass in charge. */
        this._resolved = false;
    }

    /** True when this material needs the sorted, blended pass. */
    get isTransparent() { return this.albedoColor.a < 255; }

    clone() {
        const copy = new Material3D();
        Object.assign(copy, this, { albedoColor: this.albedoColor.clone(), _resolved: false });
        return copy;
    }

    /** Loads every texture path through an asset manager, once. */
    async resolveTextures(assets) {
        if (this._resolved || !assets) return this;
        this._resolved = true;

        const slots = [
            ['albedoMapPath', 'albedoMap'],
            ['normalMapPath', 'normalMap'],
            ['metallicMapPath', 'metallicMap'],
            ['roughnessMapPath', 'roughnessMap'],
            ['emissiveMapPath', 'emissiveMap'],
        ];

        await Promise.all(slots.map(async ([pathKey, textureKey]) => {
            const path = this[pathKey];
            if (!path) return;
            try {
                this[textureKey] = await assets.loadTexture(path);
            } catch (err) {
                console.warn(`[Material3D] ${path}: ${err.message}`);
            }
        }));

        return this;
    }

    /** The scene-file shape, matching `Material3DJsonConverter` exactly. */
    toJSON() {
        const out = {
            albedoColor: this.albedoColor.toHex(),
            metallic: this.metallic,
            roughness: this.roughness,
            emissiveIntensity: this.emissiveIntensity,
        };

        // Paths are written only when set, as the C# converter does.
        const paths = {
            albedoMap: this.albedoMapPath, normalMap: this.normalMapPath,
            metallicMap: this.metallicMapPath, roughnessMap: this.roughnessMapPath,
            emissiveMap: this.emissiveMapPath, shader: this.shaderPath,
        };
        for (const [key, value] of Object.entries(paths)) {
            if (value) out[key] = value;
        }

        return out;
    }

    /** Reads the object form a scene file stores. */
    static from(value) {
        if (value instanceof Material3D) return value;
        if (!value || typeof value !== 'object') return new Material3D();
        return new Material3D(value);
    }

    /** A plain white surface, used when a renderer has no material of its own. */
    static get default() { return new Material3D(); }
}
