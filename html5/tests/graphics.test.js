// -----------------------------------------------------------------------------
// Graphics settings, presets and capabilities.
//
// Part of this file is ordinary behaviour; the rest reads the C# sources, in the
// idiom interop.test.js already uses, because the preset table is shared data and a
// field that exists on one engine only is the defect this repository keeps finding.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { GraphicsSettings, ShadowQuality, TextureFiltering, Lighting2DQuality } from '../src/rendering/GraphicsSettings.js';
import { GraphicsCapabilities } from '../src/rendering/GraphicsCapabilities.js';
import { PlayerPrefs } from '../src/save/PlayerPrefs.js';
import presets from '../src/rendering/graphics-presets.json' with { type: 'json' };
import { VERSION, statusLine } from '../src/core/EngineInfo.js';
import { Skybox } from '../src/rendering/Skybox.js';
import { RenderSystem3D, uploadCubemap } from '../src/rendering/RenderSystem3D.js';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const csharp = (...parts) => readFileSync(join(repo, ...parts), 'utf8');

// -----------------------------------------------------------------------------
// The table
// -----------------------------------------------------------------------------

test('every preset states every field, so switching preset never leaves a stale value', () => {
    // A preset that omitted a field would inherit whatever the last one set, which is
    // the bug where going Ultra then Battery leaves the shadow map at 4096.
    const groups = ['shared', 'native', 'web'];
    for (const group of groups) {
        const first = Object.keys(presets.presets[0][group]).sort();
        for (const preset of presets.presets) {
            assert.deepEqual(Object.keys(preset[group]).sort(), first,
                `preset "${preset.id}" has a different set of ${group} fields`);
        }
    }
});

test('preset ids are unique and the named default is one of them', () => {
    const ids = presets.presets.map((p) => p.id);
    assert.equal(new Set(ids).size, ids.length, 'two presets share an id');
    assert.ok(ids.includes(presets.default), `the default "${presets.default}" is not a preset`);
});

test('the thresholds run downwards and the last one catches everything', () => {
    // Read top-down with the first match winning, so an out-of-order table would send
    // a fast machine to a low preset and never reach the entry meant for it.
    const mins = presets.thresholds.map((t) => t.minFps);
    for (let i = 1; i < mins.length; i++) {
        assert.ok(mins[i] < mins[i - 1], `threshold ${i} is not below the one before it`);
    }
    assert.equal(mins.at(-1), 0, 'the last threshold must accept any frame rate');

    const ids = new Set(presets.presets.map((p) => p.id));
    for (const t of presets.thresholds) assert.ok(ids.has(t.preset), `threshold names "${t.preset}"`);
});

test('a measured frame rate maps onto the preset the table names', () => {
    assert.equal(GraphicsSettings.suggest(240), 'ultra');
    assert.equal(GraphicsSettings.suggest(90), 'high');
    assert.equal(GraphicsSettings.suggest(60), 'medium');
    assert.equal(GraphicsSettings.suggest(30), 'low');
    assert.equal(GraphicsSettings.suggest(8), 'battery');
});

test('battery saver costs less than ultra on every axis that has a direction', () => {
    // The presets are a ladder. One rung that climbed the wrong way would make a
    // low-spec choice slower than the high-spec one, which no other test would notice.
    const battery = GraphicsSettings.findPreset('battery').shared;
    const ultra = GraphicsSettings.findPreset('ultra').shared;

    for (const key of ['renderScale', 'maxLightsPerObject', 'tessellationRadial',
                       'tessellationRings', 'drawDistance', 'particleDensity',
                       'anisotropy', 'streamingRadius']) {
        assert.ok(battery[key] <= ultra[key], `battery.${key} (${battery[key]}) exceeds ultra's (${ultra[key]})`);
    }
    assert.equal(battery.postProcessing, false);
    assert.equal(battery.shadows, 'Off');
});

// -----------------------------------------------------------------------------
// The settings object
// -----------------------------------------------------------------------------

test('applying a preset writes every one of its fields onto the settings', () => {
    const settings = new GraphicsSettings();
    assert.ok(settings.applyPreset('ultra'));
    assert.equal(settings.preset, 'ultra');

    const preset = GraphicsSettings.findPreset('ultra');
    for (const [key, value] of Object.entries({ ...preset.shared, ...preset.native, ...preset.web })) {
        assert.deepEqual(settings[key], value, `${key} did not come across`);
    }
});

test('an unknown preset changes nothing and says so', () => {
    const settings = new GraphicsSettings();
    settings.applyPreset('high');
    assert.equal(settings.applyPreset('nonsense'), false);
    assert.equal(settings.preset, 'high');
});

test('a settings object round-trips through PlayerPrefs', () => {
    // The gap this closes: html5/src/save/ was empty and nothing in the browser engine
    // touched localStorage, so nothing a player chose survived a reload.
    installFakeStorage();
    PlayerPrefs.reset();

    const settings = new GraphicsSettings();
    settings.applyPreset('battery');
    settings.renderScale = 0.42;

    PlayerPrefs.setString('Graphics', JSON.stringify(settings));
    PlayerPrefs.save();
    PlayerPrefs.reset();

    const back = JSON.parse(PlayerPrefs.getString('Graphics'));
    assert.equal(back.preset, 'battery');
    assert.equal(back.renderScale, 0.42);
});

test('a probe with no WebGL context still answers, and answers conservatively', () => {
    const caps = GraphicsCapabilities.probe(null);
    assert.equal(caps.webgl2, false);
    assert.equal(caps.shadows, false, 'this renderer has no shadow pass to offer');
    assert.equal(caps.postProcessing, false);
    assert.equal(caps.lighting2D, false);
    assert.equal(caps.suggestPreset(), 'low');
});

test('a machine on battery is offered the battery preset whatever else it can do', () => {
    const caps = new GraphicsCapabilities({
        webgl2: true, hardwareConcurrency: 16, deviceMemory: 32, maxTextureSize: 16384,
    });
    assert.equal(caps.suggestPreset(), 'high');
    caps.onBattery = true;
    assert.equal(caps.suggestPreset(), 'battery');
});

// -----------------------------------------------------------------------------
// Parity with the C# engine
// -----------------------------------------------------------------------------

/**
 * Fields whose C# spelling is not just the JSON key with a capital first letter.
 *
 * One entry, and it is the repository's established spelling either side of the
 * seam -- html5/src/EngineHost.js has `vsync` and EngineConfig.cs has `VSync` -- so
 * this is written down rather than renamed. A second entry appearing here is a
 * reason to argue about it, not to extend the list quietly.
 */
const CSHARP_NAMES = { vsync: 'VSync' };

test('every field in the shared preset table exists on both engines', () => {
    const source = csharp('SexyBiscuit.Engine', 'Rendering', 'GraphicsSettings.cs');
    const settings = new GraphicsSettings();

    for (const preset of presets.presets) {
        for (const key of Object.keys({ ...preset.shared, ...preset.native, ...preset.web })) {
            assert.ok(key in settings, `the browser GraphicsSettings has no "${key}"`);

            const pascal = CSHARP_NAMES[key] ?? key[0].toUpperCase() + key.slice(1);
            assert.match(source, new RegExp(`\\b${pascal}\\b\\s*\\{\\s*get`),
                `SexyBiscuit.Engine/Rendering/GraphicsSettings.cs has no "${pascal}" property`);
        }
    }
});

test('the quality enums list the same members in the same order on both engines', () => {
    const source = csharp('SexyBiscuit.Engine', 'Rendering', 'GraphicsSettings.cs');

    const pairs = [
        ['ShadowQuality', ShadowQuality],
        ['TextureFiltering', TextureFiltering],
        ['Lighting2DQuality', Lighting2DQuality],
    ];

    for (const [name, table] of pairs) {
        const match = new RegExp(`enum ${name}\\s*\\{([^}]*)\\}`).exec(source);
        assert.ok(match, `SexyBiscuit.Engine has no enum ${name}`);

        const members = match[1].split(',').map((m) => m.trim()).filter(Boolean);
        assert.deepEqual(Object.keys(table), members, `${name} differs between the engines`);
        // The string values are what the JSON carries, so they must equal ToString().
        assert.deepEqual(Object.values(table), members, `${name}'s values must equal its names`);
    }
});

test('PlayerPrefs offers the same members the C# one does', () => {
    // Named the same so a game's save code reads identically on both engines, and so
    // the graphics blob one writes is the blob the other reads.
    const source = csharp('SexyBiscuit.Engine', 'Save', 'PlayerPrefs.cs');
    const methods = [...source.matchAll(/public static [\w<>?]+ (\w+)\(/g)].map((m) => m[1]);

    for (const method of methods) {
        const camel = method[0].toLowerCase() + method.slice(1);
        assert.equal(typeof PlayerPrefs[camel], 'function',
            `the browser PlayerPrefs has no ${camel}(), which C# has as ${method}()`);
    }
});

test('the two engines report the same version, from one place each', () => {
    // Four sources used to agree by accident: the editor derived one from an assembly
    // with no <Version> at all, the CookieJar and the editor's project file each hard-
    // coded "1.0.0", and the browser exported its own constant. A drift between them
    // would make a cookie's engineVersion range refuse, or accept, for a wrong reason.
    const csproj = csharp('SexyBiscuit.Engine', 'SexyBiscuit.Engine.csproj');
    const declared = /<Version>([^<]+)<\/Version>/.exec(csproj);
    assert.ok(declared, 'SexyBiscuit.Engine.csproj states no <Version>');
    assert.equal(VERSION, declared[1],
        'html5/src/core/EngineInfo.js and SexyBiscuit.Engine.csproj disagree on the version');

    // And nothing hard-codes it any more.
    for (const file of [
        ['SexyBiscuit.Editor', 'Assistant', 'McpHost.cs'],
        ['SexyBiscuit.Editor', 'ProjectFile.cs'],
        ['SexyBiscuit.Engine', 'CookieJar', 'CookieProjectContext.cs'],
    ]) {
        assert.doesNotMatch(csharp(...file), /=\s*"\d+\.\d+\.\d+"/,
            `${file.join('/')} still hard-codes a version instead of reading EngineInfo`);
    }
});

test('the status line carries the engine version, which is what the menu shows', () => {
    const line = statusLine(null);
    assert.match(line, new RegExp(`SexyBiscuit ${VERSION.replaceAll('.', '\\.')}`));
    assert.match(line, /Canvas 2D/);
});

test('the C# engine embeds the same preset file the browser imports', () => {
    // Both engines must read one file, not two copies that drift.
    const csproj = csharp('SexyBiscuit.Engine', 'SexyBiscuit.Engine.csproj');
    assert.match(csproj, /html5\/src\/rendering\/graphics-presets\.json/,
        'the engine csproj does not embed the shared preset table');
});

// -----------------------------------------------------------------------------

function installFakeStorage() {
    const data = new Map();
    globalThis.localStorage = {
        getItem: (k) => (data.has(k) ? data.get(k) : null),
        setItem: (k, v) => data.set(k, String(v)),
        removeItem: (k) => data.delete(k),
    };
}

// -----------------------------------------------------------------------------
// Skybox — one cubemap convention, spelled the same way on both engines
// -----------------------------------------------------------------------------

test('a cubemap folder stands for the same six faces on both engines', () => {
    assert.deepEqual(Skybox.facePathsFor('Assets/Sky'), [
        'Assets/Sky/px.png', 'Assets/Sky/nx.png',
        'Assets/Sky/py.png', 'Assets/Sky/ny.png',
        'Assets/Sky/pz.png', 'Assets/Sky/nz.png',
    ]);
    assert.deepEqual(Skybox.facePathsFor('Assets/Sky/'), Skybox.facePathsFor('Assets/Sky'));

    // A scene stores one folder, so the six names it stands for are the contract. Spelled
    // differently on the two engines, a project would find a sky on one and a gradient on
    // the other, which is the exact class of defect the mirror map exists to catch.
    const source = csharp('SexyBiscuit.Engine', 'Rendering', 'Skybox.cs');
    const names = source.match(/FaceNames\s*=\s*\{([^}]*)\}/)[1]
        .split(',').map((name) => name.trim().replace(/"/g, '')).filter(Boolean);
    assert.deepEqual(names, Skybox.faceNames);
    assert.match(source, /public static string\[\] CubemapFacePaths/);
});

test('a skybox with a cubemap path hands the renderer six faces to upload', () => {
    const sky = new Skybox();
    sky.cubemapPath = 'Assets/Dusk';
    try {
        sky.awake();
        assert.deepEqual(sky.pendingFaces, Skybox.facePathsFor('Assets/Dusk'));
    } finally {
        Skybox.active = null;
    }
});

test('exposure starts at one on both engines and is drawn through, not merely stored', () => {
    assert.equal(Skybox.schema.exposure.default, 1);
    assert.equal(new Skybox().exposure, 1);

    // The browser scales the sky in the fragment shader; C# rides it on the effect's diffuse
    // colour. A property that parsed and changed nothing would be worse than no property.
    const source = csharp('SexyBiscuit.Engine', 'Rendering', 'Skybox.cs');
    assert.match(source, /public float Exposure \{ get; set; \} = 1f;/);
    assert.equal(source.match(/DiffuseColor\s*=\s*Vector3\.One\s*\*\s*MathF\.Max\(0f, Exposure\)/g)?.length, 2,
        'both the gradient and the cubemap draw should scale by Exposure');
});

/**
 * A WebGL2 stand-in that records what the cubemap path asks of it.
 *
 * Only the handful of entry points that path touches, and the constants it names by hand.
 * The face enums keep their real values because the upload adds an index to POSITIVE_X, so
 * a stub that numbered them 0..5 would agree with any order at all.
 */
function fakeGL() {
    return {
        TEXTURE_CUBE_MAP: 0x8513,
        TEXTURE_CUBE_MAP_POSITIVE_X: 0x8515,
        TEXTURE0: 0x84C0,
        RGBA: 0x1908,
        UNSIGNED_BYTE: 0x1401,
        UNPACK_FLIP_Y_WEBGL: 0x9240,
        TEXTURE_MIN_FILTER: 0x2801,
        TEXTURE_MAG_FILTER: 0x2800,
        TEXTURE_WRAP_S: 0x2802,
        TEXTURE_WRAP_T: 0x2803,
        TEXTURE_WRAP_R: 0x8072,
        LINEAR: 0x2601,
        CLAMP_TO_EDGE: 0x812F,

        uploads: [],
        params: {},
        flipY: null,
        bound: null,

        createTexture() { return { id: 'cube' }; },
        bindTexture(target, texture) { this.bound = { target, texture }; },
        pixelStorei(name, value) { if (name === this.UNPACK_FLIP_Y_WEBGL) this.flipY = value; },
        texImage2D(target, level, internalFormat, format, type, source) {
            this.uploads.push({ target, source });
        },
        texParameteri(target, name, value) { this.params[name] = value; },
    };
}

/** Six faces that resolve at once, and a record of what was asked for. */
function fakeAssets(requested) {
    return { loadTexture: (path) => { requested.push(path); return Promise.resolve({ source: { path } }); } };
}

test('a cubemap uploads its faces in the order the face names list them', () => {
    const gl = fakeGL();
    const sources = Skybox.faceNames.map((name) => ({ name }));

    assert.ok(uploadCubemap(gl, sources));
    assert.deepEqual(gl.uploads.map((upload) => upload.source.name), Skybox.faceNames);

    // POSITIVE_X + i, and the GL enums run +X, -X, +Y, -Y, +Z, -Z from there — the order
    // Skybox.faceNames and C#'s Skybox.FaceOrder both use.
    assert.deepEqual(gl.uploads.map((upload) => upload.target - gl.TEXTURE_CUBE_MAP_POSITIVE_X),
        [0, 1, 2, 3, 4, 5]);

    // A cube is sampled by direction, not by a texture coordinate: flipping turns the sky
    // over, and Texture2D leaves the flag on after every upload of its own.
    assert.equal(gl.flipY, false);
    assert.equal(gl.params[gl.TEXTURE_WRAP_R], gl.CLAMP_TO_EDGE, 'the third axis clamps too');
});

test('a skybox waiting on faces is loaded once, however many frames go by', async () => {
    const requested = [];
    const sky = new Skybox();
    sky.cubemapPath = 'Assets/Dusk';
    sky.actor = { scene: { engine: { assets: fakeAssets(requested) } } };

    const renderer = new RenderSystem3D(fakeGL());
    try {
        sky.awake();
        assert.deepEqual(sky.pendingFaces, Skybox.facePathsFor('Assets/Dusk'));

        // Three frames while six images are in flight: the guard is the whole point, since
        // pendingFaces stays set until the upload lands.
        renderer._resolveCubemap(sky);
        renderer._resolveCubemap(sky);
        renderer._resolveCubemap(sky);
        await new Promise((done) => setTimeout(done, 0));

        assert.deepEqual(requested, Skybox.facePathsFor('Assets/Dusk'), 'six faces, asked for once');
        assert.ok(sky.cubemapTexture, 'the renderer handed the texture back to the skybox');
        assert.equal(sky.pendingFaces, null, 'and the skybox stopped asking');
    } finally {
        Skybox.active = null;
    }
});

test('a skybox with no asset manager behind it draws the gradient rather than throwing', () => {
    const sky = new Skybox();
    sky.cubemapPath = 'Assets/Dusk';

    const renderer = new RenderSystem3D(fakeGL());
    try {
        sky.awake();
        renderer._resolveCubemap(sky);          // no actor, so no scene, so no assets
        assert.equal(sky.cubemapTexture, null);
    } finally {
        Skybox.active = null;
    }
});
