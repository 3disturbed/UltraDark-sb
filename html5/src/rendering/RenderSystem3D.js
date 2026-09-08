// -----------------------------------------------------------------------------
// RenderSystem3D — the WebGL2 forward renderer.
//
// One pass over the opaque meshes front to back, then the transparent ones back
// to front, with the sky drawn first. Lights are gathered per object: the four
// (by default) that contribute most, which is what the HLSL original does too.
// -----------------------------------------------------------------------------

import { Vector3, Matrix4, Color, DEG2RAD } from '../math/index.js';
import { MeshRenderer } from './MeshRenderer.js';
import { Light3D, LightType, SkyLight } from './Light3D.js';
import { Camera3D } from './Camera3D.js';
import { Skybox } from './Skybox.js';
import { Geometry, getPrimitive, MeshPrimitive } from './PrimitiveMesh.js';
import { quadTransform } from '../ui/UiWorld.js';
import {
    STANDARD_VERTEX, STANDARD_FRAGMENT, SKYBOX_VERTEX, SKYBOX_FRAGMENT, MAX_LIGHTS,
    UNLIT_TEXTURED_VERTEX, UNLIT_TEXTURED_FRAGMENT,
} from './Shaders.js';

/** What one frame cost. Shown by the editor's render-stats panel. */
export class RenderStats {
    constructor() {
        this.renderersTotal = 0;
        this.renderersCulled = 0;
        this.renderersDrawn = 0;
        this.drawCalls = 0;
        this.triangles = 0;
        this.lightsActive = 0;
    }

    reset() {
        this.renderersTotal = 0;
        this.renderersCulled = 0;
        this.renderersDrawn = 0;
        this.drawCalls = 0;
        this.triangles = 0;
        this.lightsActive = 0;
    }
}

/** Draws the 3D half of a scene. */
export class RenderSystem3D {
    /**
     * @param {WebGL2RenderingContext} gl
     * @param {object} [options]
     */
    constructor(gl, options = {}) {
        this.gl = gl;
        this.enabled = true;

        /** Overrides `Camera3D.main`. The editor points this at its own camera. */
        this.overrideCamera = null;

        this.ambientLight = Color.from('#26262C');
        this.enableFrustumCulling = options.enableFrustumCulling ?? true;
        this.maxLightsPerObject = Math.min(options.maxLightsPerObject ?? 4, MAX_LIGHTS);
        this.sortOpaqueFrontToBack = true;
        this.renderSkybox = true;

        this.stats = new RenderStats();

        this._programs = {};
        this._uniformCache = new WeakMap();
        this._whitePixel = null;
        this._initialised = false;

        // Skybox -> the faces we last started loading for it, joined. Keyed by the paths
        // rather than by the skybox alone so a load that failed is not retried every frame
        // for the life of the scene, while a later loadCubemap with different faces is.
        this._cubemapLoads = new WeakMap();
    }

    /** True when a usable WebGL2 context is present. */
    get isAvailable() { return Boolean(this.gl); }

    /** Compiles the shaders and sets the fixed GL state. Safe to call twice. */
    initialize() {
        if (this._initialised || !this.gl) return;
        const gl = this.gl;

        this._programs.standard = buildProgram(gl, STANDARD_VERTEX, STANDARD_FRAGMENT, 'standard');
        this._programs.skybox = buildProgram(gl, SKYBOX_VERTEX, SKYBOX_FRAGMENT, 'skybox');
        this._programs.unlitTextured = buildProgram(
            gl, UNLIT_TEXTURED_VERTEX, UNLIT_TEXTURED_FRAGMENT, 'unlitTextured');

        // A 1x1 white texture stands in wherever a material has no albedo map, so
        // the shader never branches on a missing sampler binding.
        this._whitePixel = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, this._whitePixel);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE,
            new Uint8Array([255, 255, 255, 255]));

        gl.enable(gl.DEPTH_TEST);
        gl.enable(gl.CULL_FACE);
        gl.cullFace(gl.BACK);
        gl.frontFace(gl.CCW);

        this._initialised = true;
    }

    /**
     * Draws one scene.
     * @param {import('../core/Scene.js').Scene} scene
     * @param {object} [options]
     * @param {number} [options.width] Viewport width; defaults to the drawing buffer.
     * @param {number} [options.height]
     */
    render(scene, options = {}) {
        if (!this.enabled || !this.gl || !scene) return;
        this.initialize();

        const gl = this.gl;
        const width = options.width ?? gl.drawingBufferWidth;
        const height = options.height ?? gl.drawingBufferHeight;
        if (width <= 0 || height <= 0) return;

        const camera = this.overrideCamera ?? Camera3D.main;
        if (!camera) return;

        this.stats.reset();
        gl.viewport(0, 0, width, height);

        const view = camera.getViewMatrix();
        const projection = camera.getProjectionMatrix(width / height);
        const viewProjection = Matrix4.multiply(view, projection);
        const cameraPosition = camera.getTransform3D().position;

        if (this.renderSkybox) this._drawSky(viewProjection, cameraPosition);

        const lights = Light3D.all.filter(
            (l) => l.enabled && l.actor?.isActive && !l.actor.isDestroyed && l.actor.scene === scene);
        this.stats.lightsActive = lights.length;

        const renderers = MeshRenderer.all.filter(
            (r) => r.enabled && r.actor?.isActive && !r.actor.isDestroyed && r.actor.scene === scene);
        this.stats.renderersTotal = renderers.length;

        const frustum = this.enableFrustumCulling ? buildFrustum(viewProjection) : null;

        const opaque = [];
        const transparent = [];

        for (const renderer of renderers) {
            if (renderer.subMeshes.length === 0) renderer._ensureGeometry();
            if (renderer.subMeshes.length === 0) continue;

            const bounds = renderer.worldBounds;
            if (frustum && !renderer.ignoreCulling && !sphereInFrustum(frustum, bounds.center, bounds.boundingRadius)) {
                this.stats.renderersCulled++;
                continue;
            }

            const distance = Vector3.distanceSquared(cameraPosition, bounds.center);
            (renderer.isTransparent ? transparent : opaque).push({ renderer, distance });
        }

        // Front to back for opaques, so the depth test rejects hidden fragments
        // early; back to front for transparents, because blending is order-dependent.
        if (this.sortOpaqueFrontToBack) opaque.sort((a, b) => a.distance - b.distance);
        transparent.sort((a, b) => b.distance - a.distance);

        const program = this._programs.standard;
        gl.useProgram(program.program);
        this._setSharedUniforms(program, viewProjection, cameraPosition);

        gl.disable(gl.BLEND);
        gl.depthMask(true);
        for (const entry of opaque) this._drawRenderer(program, entry.renderer, lights);

        if (transparent.length > 0) {
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
            // Transparent surfaces test against depth but do not write it, so two
            // of them in front of each other both survive.
            gl.depthMask(false);
            for (const entry of transparent) this._drawRenderer(program, entry.renderer, lights);
            gl.depthMask(true);
            gl.disable(gl.BLEND);
        }

        // World-space UI, after the scene it hangs in: a canvas on a wall is transparent
        // geometry like any other, and it reads depth so a pillar in front still hides it.
        this._drawWorldCanvases(options.worldCanvases ?? [], viewProjection);
    }

    /**
     * Draws every world-space UiCanvas as a textured quad.
     *
     * The canvases arrive as an argument rather than being read from UiCanvas.all here,
     * because UiCanvas already imports Camera3D and importing it back would close a cycle
     * between the UI and the renderer. The host owns the frame order, so it owns the list.
     */
    _drawWorldCanvases(canvases, viewProjection) {
        if (canvases.length === 0) return;

        const gl = this.gl;
        const geometry = getPrimitive(MeshPrimitive.Quad);
        if (!geometry) return;

        const program = this._programs.unlitTextured;
        gl.useProgram(program.program);
        gl.uniformMatrix4fv(program.uniform('uViewProjection'), false, viewProjection.m);
        gl.uniform4f(program.uniform('uColor'), 1, 1, 1, 1);
        gl.uniform1i(program.uniform('uTexture'), 0);

        gl.enable(gl.BLEND);

        // Straight alpha, matching what UiPainter writes into the texture: a panel authored
        // #161920e6 must be that colour in the world and on the screen alike. The native
        // engine swaps to BlendState.NonPremultiplied here for exactly the same reason.
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        gl.depthMask(false);

        for (const { canvas, texture } of canvases) {
            const basis = canvas.worldBasis();
            if (!basis || !texture) continue;

            if (canvas.depthTest) gl.enable(gl.DEPTH_TEST);
            else gl.disable(gl.DEPTH_TEST);

            if (canvas.doubleSided) gl.disable(gl.CULL_FACE);
            else gl.enable(gl.CULL_FACE);

            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, texture.getGLTexture(gl));

            gl.uniformMatrix4fv(program.uniform('uWorld'), false, quadTransform(basis).m);

            this._drawGeometry(program, geometry);
            this.stats.drawCalls++;
        }

        gl.enable(gl.DEPTH_TEST);
        gl.enable(gl.CULL_FACE);
        gl.depthMask(true);
        gl.disable(gl.BLEND);
    }

    _setSharedUniforms(program, viewProjection, cameraPosition) {
        const gl = this.gl;
        gl.uniformMatrix4fv(program.uniform('uViewProjection'), false, viewProjection.toArray());
        gl.uniform3f(program.uniform('uCameraPosition'), cameraPosition.x, cameraPosition.y, cameraPosition.z);

        const skyLight = SkyLight.active;
        const ambient = skyLight ? skyLight.getAverageAmbient() : this.ambientLight;
        const [r, g, b] = ambient.toFloatArray();
        gl.uniform3f(program.uniform('uAmbientColor'), r, g, b);
    }

    _drawRenderer(program, renderer, lights) {
        const gl = this.gl;
        const transform = renderer.actor.transform3D;
        if (!transform) return;

        const world = transform.getWorldMatrix();
        gl.uniformMatrix4fv(program.uniform('uWorld'), false, world.toArray());
        gl.uniformMatrix4fv(program.uniform('uWorldInverseTranspose'), false,
            Matrix4.transpose(Matrix4.invert(world)).toArray());

        this._setLightUniforms(program, lights, transform.position);

        for (const subMesh of renderer.subMeshes) {
            const material = renderer.materials[subMesh.materialIndex]
                ?? renderer.materials[0]
                ?? renderer.ensureOwnMaterial(0);

            this._setMaterialUniforms(program, material);
            this._drawGeometry(program, subMesh.geometry);

            this.stats.drawCalls++;
            this.stats.triangles += subMesh.geometry.triangleCount;
        }

        this.stats.renderersDrawn++;
    }

    _setMaterialUniforms(program, material) {
        const gl = this.gl;
        const [r, g, b, a] = material.albedoColor.toFloatArray();

        gl.uniform4f(program.uniform('uAlbedoColor'), r, g, b, a);
        gl.uniform1f(program.uniform('uMetallic'), material.metallic);
        // A perfectly smooth surface produces a singular specular lobe; the floor
        // keeps the highlight finite.
        gl.uniform1f(program.uniform('uRoughness'), Math.max(material.roughness, 0.03));
        gl.uniform1f(program.uniform('uEmissiveIntensity'), material.emissiveIntensity);

        gl.activeTexture(gl.TEXTURE0);
        const albedoMap = material.albedoMap?.getGLTexture(gl);
        gl.bindTexture(gl.TEXTURE_2D, albedoMap ?? this._whitePixel);
        gl.uniform1i(program.uniform('uAlbedoMap'), 0);
        gl.uniform1i(program.uniform('uHasAlbedoMap'), albedoMap ? 1 : 0);
    }

    _setLightUniforms(program, lights, objectPosition) {
        const gl = this.gl;

        // Directional lights reach everywhere, so they always qualify; the rest
        // compete on how much they actually contribute at this object.
        const ranked = lights
            .map((light) => ({ light, weight: lightWeight(light, objectPosition) }))
            .filter((entry) => entry.weight > 0)
            .sort((a, b) => b.weight - a.weight)
            .slice(0, this.maxLightsPerObject);

        gl.uniform1i(program.uniform('uLightCount'), ranked.length);

        ranked.forEach(({ light }, i) => {
            const direction = light.getDirection();
            const position = light.getPosition();
            const [r, g, b] = light.color.toFloatArray();

            gl.uniform1i(program.uniform(`uLightType[${i}]`), LIGHT_TYPE_INDEX[light.type] ?? 0);
            gl.uniform3f(program.uniform(`uLightPosition[${i}]`), position.x, position.y, position.z);
            gl.uniform3f(program.uniform(`uLightDirection[${i}]`), direction.x, direction.y, direction.z);
            gl.uniform3f(program.uniform(`uLightColor[${i}]`), r, g, b);
            gl.uniform1f(program.uniform(`uLightIntensity[${i}]`), light.intensity);
            gl.uniform1f(program.uniform(`uLightRange[${i}]`), light.range);
            gl.uniform1f(program.uniform(`uLightSpotCos[${i}]`), Math.cos(light.spotAngle * DEG2RAD));
        });
    }

    _drawGeometry(program, geometry) {
        const gl = this.gl;

        if (!geometry.gpu || geometry.gpu.gl !== gl) this._uploadGeometry(geometry);

        gl.bindVertexArray(geometry.gpu.vao);
        gl.drawElements(gl.TRIANGLES, geometry.indices.length, geometry.gpu.indexType, 0);
        gl.bindVertexArray(null);
    }

    _uploadGeometry(geometry) {
        const gl = this.gl;

        const vao = gl.createVertexArray();
        gl.bindVertexArray(vao);

        const vbo = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
        gl.bufferData(gl.ARRAY_BUFFER, geometry.vertices, gl.STATIC_DRAW);

        const stride = Geometry.stride * 4;   // floats to bytes
        const attributes = [
            ['aPosition', 3, 0],
            ['aNormal', 3, 12],
            ['aTexCoord', 2, 24],
        ];

        // The fixed slots from ATTRIBUTE_SLOTS, not whatever one program's linker chose, so
        // this VAO is correct under every program that draws the mesh.
        const slots = new Map(ATTRIBUTE_SLOTS);

        for (const [name, size, offset] of attributes) {
            const location = slots.get(name);
            if (location === undefined) continue;
            gl.enableVertexAttribArray(location);
            gl.vertexAttribPointer(location, size, gl.FLOAT, false, stride, offset);
        }

        const ibo = gl.createBuffer();
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ibo);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, geometry.indices, gl.STATIC_DRAW);

        gl.bindVertexArray(null);

        geometry.gpu = {
            gl, vao, vbo, ibo,
            indexType: geometry.indices instanceof Uint32Array ? gl.UNSIGNED_INT : gl.UNSIGNED_SHORT,
        };
    }

    _drawSky(viewProjection, cameraPosition) {
        const gl = this.gl;
        const sky = Skybox.active;
        const program = this._programs.skybox;

        if (sky?.pendingFaces) this._resolveCubemap(sky);

        gl.useProgram(program.program);

        const top = (sky?.gradientTop ?? Color.from('#3A5CA8')).toFloatArray();
        const bottom = (sky?.gradientBottom ?? Color.from('#B2C6E0')).toFloatArray();

        gl.uniform3f(program.uniform('uTopColor'), top[0], top[1], top[2]);
        gl.uniform3f(program.uniform('uBottomColor'), bottom[0], bottom[1], bottom[2]);
        gl.uniform3f(program.uniform('uCameraPosition'), cameraPosition.x, cameraPosition.y, cameraPosition.z);
        gl.uniform1f(program.uniform('uExposure'), sky?.exposure ?? 1);
        gl.uniformMatrix4fv(program.uniform('uInverseViewProjection'), false,
            Matrix4.invert(viewProjection).toArray());

        // The sky program samples nothing else, so the cube gets unit 0. A sampler left
        // unbound reads whatever the mesh pass put on that unit last frame, which for a
        // samplerCube is undefined rather than merely wrong.
        const cubemap = sky?.cubemapTexture ?? null;
        if (cubemap) {
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_CUBE_MAP, cubemap);
            gl.uniform1i(program.uniform('uCubemap'), 0);
        }
        gl.uniform1i(program.uniform('uHasCubemap'), cubemap ? 1 : 0);

        // The sky fills the frame, so it neither tests nor writes depth; drawing
        // it first means every later fragment overwrites it.
        gl.disable(gl.DEPTH_TEST);
        gl.depthMask(false);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.depthMask(true);
        gl.enable(gl.DEPTH_TEST);

        this.stats.drawCalls++;
    }

    /**
     * Loads and uploads the six faces a Skybox is waiting on, once.
     *
     * A skybox names one folder and turns it into six paths; nothing had ever read them
     * back, so `cubemapPath` set the sky in a native build and left the browser on its
     * gradient. The load is asynchronous — six images over the network — so the draw that
     * starts it and the several that follow all see `pendingFaces` still set, and the guard
     * is what stops six requests becoming six a frame.
     *
     * @param {import('./Skybox.js').Skybox} sky
     */
    _resolveCubemap(sky) {
        const faces = sky.pendingFaces;
        if (!faces || !this.gl) return;

        const key = faces.join('|');
        if (this._cubemapLoads.get(sky) === key) return;
        this._cubemapLoads.set(sky, key);

        const assets = sky.actor?.scene?.engine?.assets;
        if (!assets) return;

        const gl = this.gl;
        Promise.all(faces.map((path) => assets.loadTexture(path)))
            .then((textures) => {
                // Six images take long enough that the scene may have moved on to another
                // sky, or none, while they were in flight.
                if (sky.pendingFaces?.join('|') !== key) return;
                sky.markCubemapUploaded(uploadCubemap(gl, textures.map((texture) => texture.source)));
            })
            .catch((err) => console.warn(`[RenderSystem3D] cubemap '${faces[0]}': ${err.message}`));
    }

    /** Releases the shaders and the fallback texture. */
    dispose() {
        if (!this.gl) return;
        for (const program of Object.values(this._programs)) {
            this.gl.deleteProgram(program.program);
        }
        if (this._whitePixel) this.gl.deleteTexture(this._whitePixel);
        this._programs = {};
        this._initialised = false;
    }
}

/**
 * Six images as one GL cubemap, in the order `Skybox.faceNames` lists them.
 *
 * The GL face enums run +X, -X, +Y, -Y, +Z, -Z from `TEXTURE_CUBE_MAP_POSITIVE_X`, which is
 * the order `Skybox.faceNames`, `Skybox.CubemapFacePaths` and C#'s `Skybox.FaceOrder` all
 * use, so the index is the offset and nothing has to be remapped.
 *
 * @param {WebGL2RenderingContext} gl
 * @param {(ImageBitmap|HTMLImageElement|HTMLCanvasElement)[]} sources Six faces.
 * @returns {WebGLTexture}
 */
export function uploadCubemap(gl, sources) {
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_CUBE_MAP, texture);

    // Not flipped, unlike Texture2D: a cube is sampled by direction rather than by a
    // texture coordinate, and flipping the faces turns the sky upside down. Texture2D
    // leaves the flag on after every upload, so this has to say so rather than assume.
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);

    for (let face = 0; face < 6; face++) {
        gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X + face, 0, gl.RGBA, gl.RGBA,
            gl.UNSIGNED_BYTE, sources[face]);
    }

    gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

    // All three axes clamp: the seam where two faces meet is the classic cubemap artefact,
    // and it is the edge texel wrapping round that draws it.
    gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_WRAP_R, gl.CLAMP_TO_EDGE);

    return texture;
}

const LIGHT_TYPE_INDEX = {
    [LightType.Directional]: 0,
    [LightType.Point]: 1,
    [LightType.Spot]: 2,
};

/**
 * How much a light matters to an object, for ranking.
 * Directional lights score above every other so they are never dropped.
 */
function lightWeight(light, objectPosition) {
    if (light.intensity <= 0) return 0;
    if (light.type === LightType.Directional) return 1e9 * light.intensity;

    const distanceSq = Vector3.distanceSquared(light.getPosition(), objectPosition);
    if (distanceSq > light.range * light.range) return 0;
    return light.intensity / Math.max(distanceSq, 1e-4);
}

// -----------------------------------------------------------------------------
// Shader plumbing
// -----------------------------------------------------------------------------

/**
 * Vertex attribute slots, fixed for every program.
 *
 * A geometry's VAO is built once, against whichever program uploaded it, and then reused by
 * all of them. Left to the linker the slots would differ per program -- an unlit shader that
 * never mentions the normal would put the texture coordinate where the standard shader puts
 * the normal -- and the same mesh would draw correctly under one program and scrambled under
 * the next. Binding them by name before linking is what makes one VAO safe everywhere.
 */
const ATTRIBUTE_SLOTS = [['aPosition', 0], ['aNormal', 1], ['aTexCoord', 2]];

function buildProgram(gl, vertexSource, fragmentSource, label) {
    const program = gl.createProgram();
    gl.attachShader(program, compileShader(gl, gl.VERTEX_SHADER, vertexSource, `${label}.vert`));
    gl.attachShader(program, compileShader(gl, gl.FRAGMENT_SHADER, fragmentSource, `${label}.frag`));

    for (const [name, slot] of ATTRIBUTE_SLOTS) gl.bindAttribLocation(program, slot, name);

    gl.linkProgram(program);

    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        throw new Error(`[RenderSystem3D] ${label} failed to link: ${gl.getProgramInfoLog(program)}`);
    }

    // Uniform locations never change for a linked program, and looking one up
    // per draw call is measurably slower than caching them.
    const locations = new Map();
    return {
        program,
        uniform(name) {
            if (!locations.has(name)) locations.set(name, gl.getUniformLocation(program, name));
            return locations.get(name);
        },
    };
}

function compileShader(gl, type, source, label) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);

    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        const log = gl.getShaderInfoLog(shader);
        gl.deleteShader(shader);
        throw new Error(`[RenderSystem3D] ${label} failed to compile: ${log}`);
    }
    return shader;
}

// -----------------------------------------------------------------------------
// Frustum culling
// -----------------------------------------------------------------------------

/**
 * The six clip planes, extracted from a view-projection matrix.
 *
 * The Gribb-Hartmann derivation combines the rows of a column-vector matrix.
 * Matrix4 stores XNA's row-vector layout, where the coefficients of a clip
 * component are a *column* of the stored array rather than a row — clip.x is
 * `m[0] x + m[4] y + m[8] z + m[12]`. Reading rows here instead of columns
 * yields planes that look plausible and cull the wrong half of the world.
 */
function buildFrustum(viewProjection) {
    const m = viewProjection.m;

    // Column i of the stored array: the coefficients of clip component i.
    const column = (i) => [m[i], m[4 + i], m[8 + i], m[12 + i]];
    const [clipX, clipY, clipZ, clipW] = [column(0), column(1), column(2), column(3)];

    return [
        combine(clipW, clipX, 1),    // left:   w + x >= 0
        combine(clipW, clipX, -1),   // right:  w - x >= 0
        combine(clipW, clipY, 1),    // bottom: w + y >= 0
        combine(clipW, clipY, -1),   // top:    w - y >= 0
        combine(clipW, clipZ, 1),    // near:   w + z >= 0
        combine(clipW, clipZ, -1),   // far:    w - z >= 0
    ].map(normalizePlane);

    function combine(a, b, sign) {
        return [
            a[0] + sign * b[0], a[1] + sign * b[1],
            a[2] + sign * b[2], a[3] + sign * b[3],
        ];
    }
}

function normalizePlane(plane) {
    const length = Math.hypot(plane[0], plane[1], plane[2]);
    if (length < 1e-9) return plane;
    return [plane[0] / length, plane[1] / length, plane[2] / length, plane[3] / length];
}

function sphereInFrustum(planes, center, radius) {
    for (const [a, b, c, d] of planes) {
        if (a * center.x + b * center.y + c * center.z + d < -radius) return false;
    }
    return true;
}

export { buildFrustum, sphereInFrustum };
