// -----------------------------------------------------------------------------
// MeshRenderer — draws a mesh with a list of materials.
//
// Geometry comes from one of two places: a built-in primitive named by
// `meshType`, or a model file named by `modelPath`. The primitive path needs no
// assets at all, which is what makes a fresh 3D scene render immediately.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3, Bounds, Color } from '../math/index.js';
import { Material3D } from './Material3D.js';
import { MeshPrimitive, getPrimitive, getPrimitiveBounds } from './PrimitiveMesh.js';

/** Draws 3D geometry at its actor's 3D transform. */
export class MeshRenderer extends Component {
    static schema = {
        meshType:      { type: P.Enum, values: Object.keys(MeshPrimitive), default: 'Cube' },
        modelPath:     { type: P.Asset, assetKind: 'model', default: '' },
        castShadows:   { type: P.Bool, default: true },
        ignoreCulling: { type: P.Bool, default: false },
        materials:     { type: P.List, of: { type: P.Material }, default: () => [] },
    };

    /** Every live mesh renderer, gathered by the 3D renderer each frame. */
    static all = [];

    constructor() {
        super();
        this.meshType = MeshPrimitive.Cube;
        this.modelPath = '';
        this.castShadows = true;
        this.ignoreCulling = false;

        /** @type {Material3D[]} One per sub-mesh; the first is used when there is only one. */
        this.materials = [];

        /** @type {Array<{geometry: import('./PrimitiveMesh.js').Geometry, materialIndex: number}>} */
        this.subMeshes = [];

        this._loading = false;
        this._resolvedPath = null;
    }

    awake() {
        MeshRenderer.all.push(this);
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    onDestroy() {
        const i = MeshRenderer.all.indexOf(this);
        if (i >= 0) MeshRenderer.all.splice(i, 1);
    }

    /** The material actually used, creating a default one on demand. */
    ensureOwnMaterial(index = 0) {
        while (this.materials.length <= index) this.materials.push(new Material3D());
        return this.materials[index];
    }

    // ---- Convenience passthroughs to the first material ----------------------
    // The C# component exposes the same shortcuts, marked [SceneIgnore] so they
    // are not written twice; `transient` does the same job here.

    get albedoColor() { return this.ensureOwnMaterial(0).albedoColor; }
    set albedoColor(value) { this.ensureOwnMaterial(0).albedoColor = Color.from(value); }

    get metallic() { return this.ensureOwnMaterial(0).metallic; }
    set metallic(value) { this.ensureOwnMaterial(0).metallic = value; }

    get roughness() { return this.ensureOwnMaterial(0).roughness; }
    set roughness(value) { this.ensureOwnMaterial(0).roughness = value; }

    get emissiveIntensity() { return this.ensureOwnMaterial(0).emissiveIntensity; }
    set emissiveIntensity(value) { this.ensureOwnMaterial(0).emissiveIntensity = value; }

    get albedoTexturePath() { return this.ensureOwnMaterial(0).albedoMapPath; }
    set albedoTexturePath(value) { this.ensureOwnMaterial(0).albedoMapPath = value; }

    /** True when anything about this renderer needs the sorted, blended pass. */
    get isTransparent() { return this.materials.some((m) => m.isTransparent); }

    /** Triangles across every sub-mesh. */
    get triangleCount() {
        return this.subMeshes.reduce((total, sub) => total + sub.geometry.triangleCount, 0);
    }

    /** The mesh's extent in local space. */
    get localBounds() {
        if (this.subMeshes.length === 0) return getPrimitiveBounds(this.meshType);

        let bounds = this.subMeshes[0].geometry.bounds.clone();
        for (let i = 1; i < this.subMeshes.length; i++) {
            const other = this.subMeshes[i].geometry.bounds;
            bounds = bounds.encapsulate(other.min).encapsulate(other.max);
        }
        return bounds;
    }

    /** The mesh's extent in world space, for culling. */
    get worldBounds() {
        const t = this.actor?.transform3D;
        const local = this.localBounds;
        if (!t) return local;

        const scale = t.scale;
        // A conservative box: scale the extents and re-centre, without trying to
        // rotate the box tightly. Culling only needs an over-estimate.
        const size = new Vector3(
            Math.abs(local.size.x * scale.x),
            Math.abs(local.size.y * scale.y),
            Math.abs(local.size.z * scale.z));

        // Rotation can grow the box by up to the diagonal, so use the radius.
        const radius = size.length / 2;
        return new Bounds(
            Vector3.add(t.position, Vector3.transform(Vector3.multiply(local.center, scale), t.rotation)),
            new Vector3(radius * 2, radius * 2, radius * 2));
    }

    start() { this._ensureGeometry(); }

    /** Builds or loads geometry, whichever the configuration calls for. */
    _ensureGeometry() {
        if (this.modelPath && this._resolvedPath !== this.modelPath) {
            this._loadModel(this.modelPath);
            return;
        }
        if (this.subMeshes.length > 0) return;

        const geometry = getPrimitive(this.meshType);
        if (geometry) this.subMeshes = [{ geometry, materialIndex: 0 }];
        if (this.materials.length === 0) this.materials.push(new Material3D());
    }

    _loadModel(path) {
        const assets = this.actor?.scene?.engine?.assets;
        if (!assets || this._loading) return;

        this._loading = true;
        this._resolvedPath = path;

        assets.loadModel(path)
            .then((model) => {
                this._loading = false;
                if (!model) return;
                this.subMeshes = model.subMeshes;
                if (model.materials?.length && this.materials.length === 0) {
                    this.materials = model.materials;
                }
                if (this.materials.length === 0) this.materials.push(new Material3D());
            })
            .catch((err) => {
                this._loading = false;
                console.warn(`[MeshRenderer] ${path}: ${err.message}`);
                // Fall back to the primitive so the actor is still visible and
                // selectable rather than silently absent from the scene.
                const geometry = getPrimitive(this.meshType);
                if (geometry) this.subMeshes = [{ geometry, materialIndex: 0 }];
            });
    }

    /** Replaces the geometry with a built-in primitive. */
    setPrimitive(type) {
        this.meshType = type;
        this.modelPath = '';
        this._resolvedPath = null;
        this.subMeshes = [];
        this._ensureGeometry();
    }
}
registerComponent(MeshRenderer, { category: 'Rendering', summary: 'Draws 3D geometry.' });

// The passthrough shortcuts are not saved: the material they proxy is already
// written in full under `Materials`, and writing both means the file disagrees
// with itself the moment either is edited.
for (const key of ['albedoColor', 'metallic', 'roughness', 'emissiveIntensity', 'albedoTexturePath']) {
    MeshRenderer.schema[key] = { type: P.Number, transient: true };
}
