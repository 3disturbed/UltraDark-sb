// -----------------------------------------------------------------------------
// AssetManager — loads and caches everything a project references by path.
//
// Paths in a scene file are project-relative with forward slashes, exactly as
// `ProjectPaths.MakeRelative` writes them, so the same file resolves in both
// engines. Resolution here means joining against a base URL rather than a
// filesystem root, and the several places the C# loader looks — the path itself,
// then under `Assets/` — are tried in the same order.
// -----------------------------------------------------------------------------

import { Texture2D } from './Texture2D.js';

/** Loads project assets, caching by resolved path. */
export class AssetManager {
    /**
     * @param {object} [options]
     * @param {string} [options.root=''] Base URL every relative path resolves against.
     * @param {typeof fetch} [options.fetch] Injectable, so tests need no network.
     */
    constructor({ root = '', fetchImpl = null } = {}) {
        this.root = root.endsWith('/') || root === '' ? root : `${root}/`;
        this._fetch = fetchImpl ?? (typeof fetch !== 'undefined' ? fetch.bind(globalThis) : null);

        this._cache = new Map();      // resolved path -> value
        this._pending = new Map();    // resolved path -> promise
        this._refCounts = new Map();

        /** Called with (path) whenever an asset is replaced by a hot reload. */
        this.onAssetReloaded = null;
    }

    /**
     * Turns a project-relative path into a URL.
     * A rooted path or an absolute URL is passed through untouched.
     */
    resolve(path) {
        if (!path) return '';
        if (/^([a-z]+:)?\/\//i.test(path) || path.startsWith('/') || path.startsWith('data:')) {
            return path;
        }
        return this.root + path.replace(/^\.\//, '');
    }

    /**
     * The candidate URLs for a path, in the order the C# loader tries them.
     * @returns {string[]}
     */
    candidates(path) {
        if (!path) return [];
        const direct = this.resolve(path);
        if (direct !== this.root + path) return [direct];

        // `Assets/foo.png` is how a scene usually names things, but a project may
        // also store them at the root; try both rather than failing on the first.
        return path.startsWith('Assets/')
            ? [direct, this.resolve(path.slice('Assets/'.length))]
            : [direct, this.resolve(`Assets/${path}`)];
    }

    /** True when a path is already loaded. */
    isLoaded(path) { return this._cache.has(this.resolve(path)); }

    /** The cached value for a path, or undefined. */
    get(path) { return this._cache.get(this.resolve(path)); }

    /** Loads an image. */
    async loadTexture(path) {
        return this._load(path, 'texture', async (url) => {
            const image = await loadImage(url);
            return new Texture2D(image, path);
        });
    }

    /** Loads a file as text — a script, a shader, an action map. */
    async loadText(path) {
        return this._load(path, 'text', async (url) => {
            const response = await this._request(url);
            return response.text();
        });
    }

    /** Loads and parses JSON. */
    async loadJson(path) {
        return this._load(path, 'json', async (url) => {
            const response = await this._request(url);
            return response.json();
        });
    }

    /** Loads an audio file and decodes it against an AudioContext. */
    async loadAudio(path, audioContext) {
        return this._load(path, 'audio', async (url) => {
            const response = await this._request(url);
            const bytes = await response.arrayBuffer();
            if (!audioContext) return bytes;   // decoded later, once a context exists
            return audioContext.decodeAudioData(bytes);
        });
    }

    /**
     * Loads a 3D model.
     *
     * Only glTF and GLB are read directly. Everything else — .fbx, .obj and the
     * rest the C# engine handles through AssimpNet — has no browser-side reader,
     * so the caller falls back to the renderer's primitive and says so, rather
     * than leaving an invisible actor in the scene.
     */
    async loadModel(path) {
        const lower = path.toLowerCase();
        if (!lower.endsWith('.gltf') && !lower.endsWith('.glb')) {
            throw new Error(
                `Unsupported model format for '${path}'. `
                + 'The web runtime reads .gltf and .glb; convert the model or use a primitive.');
        }

        return this._load(path, 'model', async (url) => {
            const { loadGltf } = await import('./GltfLoader.js');
            return loadGltf(url, this);
        });
    }

    /** Dynamically imports a project script as an ES module. */
    async loadModule(path) {
        return this._load(path, 'module', async (url) => import(/* @vite-ignore */ url));
    }

    async _load(path, kind, loader) {
        if (!path) throw new Error(`Cannot load an empty ${kind} path.`);

        const key = `${kind}:${this.resolve(path)}`;

        const cached = this._cache.get(key);
        if (cached) {
            this._refCounts.set(key, (this._refCounts.get(key) ?? 0) + 1);
            return cached;
        }

        const inFlight = this._pending.get(key);
        if (inFlight) return inFlight;

        // Try each candidate location in turn, reporting the first path so the
        // error names what the project asked for, not the last thing tried.
        const promise = (async () => {
            const urls = this.candidates(path);
            let lastError = null;

            for (const url of urls) {
                try {
                    const value = await loader(url);
                    this._cache.set(key, value);
                    this._refCounts.set(key, 1);
                    return value;
                } catch (err) {
                    lastError = err;
                }
            }

            throw new Error(`Could not load '${path}': ${lastError?.message ?? 'not found'}`);
        })();

        this._pending.set(key, promise);
        try {
            return await promise;
        } finally {
            this._pending.delete(key);
        }
    }

    async _request(url) {
        if (!this._fetch) throw new Error('No fetch implementation available.');
        const response = await this._fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status} for ${url}`);
        return response;
    }

    /** Drops one asset, releasing its GPU handle where it has one. */
    unload(path) {
        for (const key of [...this._cache.keys()]) {
            if (!key.endsWith(`:${this.resolve(path)}`)) continue;
            this._cache.get(key)?.dispose?.();
            this._cache.delete(key);
            this._refCounts.delete(key);
        }
    }

    /** Drops everything. */
    unloadAll() {
        for (const value of this._cache.values()) value?.dispose?.();
        this._cache.clear();
        this._refCounts.clear();
    }

    /** Forces a reload, replacing the cached value. Used by the editor's watcher. */
    async reload(path, kind = 'texture') {
        const key = `${kind}:${this.resolve(path)}`;
        this._cache.delete(key);

        const loaders = {
            texture: () => this.loadTexture(path),
            text: () => this.loadText(path),
            json: () => this.loadJson(path),
            model: () => this.loadModel(path),
        };

        const value = await (loaders[kind] ?? loaders.text)();
        this.onAssetReloaded?.(path);
        return value;
    }

    /** Everything currently cached, for the editor's memory view. */
    getLoadedAssets() {
        return [...this._cache.entries()].map(([key, value]) => {
            const [kind, ...rest] = key.split(':');
            return { kind, path: rest.join(':'), refCount: this._refCounts.get(key) ?? 0, value };
        });
    }
}

function loadImage(url) {
    return new Promise((resolve, reject) => {
        if (typeof Image === 'undefined') {
            reject(new Error('Image loading needs a browser environment.'));
            return;
        }
        const image = new Image();
        // Set before src: a cross-origin image without this taints the canvas and
        // cannot be read back or uploaded to WebGL.
        image.crossOrigin = 'anonymous';
        image.onload = () => resolve(image);
        image.onerror = () => reject(new Error(`Failed to decode image at ${url}`));
        image.src = url;
    });
}
