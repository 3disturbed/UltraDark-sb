// -----------------------------------------------------------------------------
// Skybox — the background behind everything else.
//
// A gradient by default, so a fresh 3D scene has a horizon without needing six
// cubemap faces. Setting `cubemapPath` — or calling loadCubemap with six paths
// directly — switches it to a textured cube.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Color } from '../math/index.js';

/** Draws the sky. Put it on the background layer. */
export class Skybox extends Component {
    static schema = {
        gradientTop:    { type: P.Color, default: '#3A5CA8FF' },
        gradientBottom: { type: P.Color, default: '#B2C6E0FF' },
        cubemapPath:    { type: P.Asset, assetKind: 'texture', default: '' },
        exposure:       { type: P.Number, default: 1, min: 0 },
    };

    /** The skybox the renderer draws. The last one to wake wins. */
    static active = null;

    /** Face file names in the order a cubemap wants them: +X, -X, +Y, -Y, +Z, -Z. */
    static faceNames = ['px', 'nx', 'py', 'ny', 'pz', 'nz'];

    /**
     * The six face paths a `cubemapPath` folder stands for.
     *
     * A scene stores one path rather than six, because six of them in a property bag is not
     * something anyone edits by hand — so the folder stands for the convention, and both
     * engines have to spell it the same way or a project loads a sky in one and not the
     * other. C#'s Skybox.CubemapFacePaths is the twin.
     *
     * @param {string} folder
     * @returns {string[]}
     */
    static facePathsFor(folder) {
        const root = String(folder ?? '').replace(/[/\\]+$/, '');
        return Skybox.faceNames.map((name) => `${root}/${name}.png`);
    }

    constructor() {
        super();
        this.gradientTop = Color.from('#3A5CA8');
        this.gradientBottom = Color.from('#B2C6E0');
        this.cubemapPath = '';
        this.exposure = 1;

        /** @type {?WebGLTexture} */
        this.cubemapTexture = null;
        this._facePaths = null;
    }

    awake() {
        Skybox.active = this;
        if (this.cubemapPath && !this._facePaths) this.loadCubemap(Skybox.facePathsFor(this.cubemapPath));
    }
    onDestroy() { if (Skybox.active === this) Skybox.active = null; }

    /**
     * Loads six faces as a cubemap.
     * @param {string[]} facePaths +X, -X, +Y, -Y, +Z, -Z, in that order.
     */
    loadCubemap(facePaths) {
        this._facePaths = facePaths;
        this.cubemapTexture = null;   // the renderer uploads it on the next draw
    }

    /** The face paths awaiting upload, or null. */
    get pendingFaces() { return this._facePaths; }

    /** Called by the renderer once the cubemap is on the GPU. */
    markCubemapUploaded(texture) {
        this.cubemapTexture = texture;
        this._facePaths = null;
    }
}
registerComponent(Skybox, { category: 'Rendering', summary: 'The sky behind the scene.' });
