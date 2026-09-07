// -----------------------------------------------------------------------------
// The node tools the prototype phase runs on: validate, export, zip, upload.
// They are what an agent working with no editor and no .NET has instead of a
// screenshot, a build button and a publish dialog, so they get the same
// end-to-end treatment the engine does.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

import { validateProject, templateProjects, report, checkScript } from '../tools/validate.js';
import { exportWeb, fill, readGitSha, slugify } from '../tools/export.js';
import { zipDirectory, listZip } from '../tools/lib/zip.js';
import { solidPng, parseHexColour } from '../tools/lib/png.js';
import { publish, encodeMeta, platformFor, channelFor, UploadConfigError } from '../tools/upload.js';
import { serve } from '../tools/serve.js';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const helloWorld = path.join(repoRoot, 'Templates', 'Hello World');

function tempDir(prefix) {
    return fs.mkdtempSync(path.join(os.tmpdir(), `sb-${prefix}-`));
}

// -----------------------------------------------------------------------------
// validate
// -----------------------------------------------------------------------------

test('every bundled template validates with no errors', () => {
    const projects = templateProjects();
    assert.ok(projects.length >= 15, 'the templates were not found');

    for (const project of projects) {
        const result = validateProject(project);
        assert.deepEqual(result.errors, [], `${path.basename(project)} has validation errors`);
        if (path.basename(project) !== 'Empty') assert.ok(result.scripts > 0);
    }
});

test('the validator names the file and line of every problem', () => {
    const dir = tempDir('validate');
    fs.mkdirSync(path.join(dir, 'Scenes'));
    fs.mkdirSync(path.join(dir, 'Scripts'));
    fs.writeFileSync(path.join(dir, 'ProjectSettings.json'),
        JSON.stringify({ WindowTitle: 'Broken', StartScene: 'Scenes/Missing' }));
    fs.writeFileSync(path.join(dir, 'Scenes', 'Main.scene'), JSON.stringify({
        name: 'Main',
        layers: [{ name: 'default', actors: [
            { name: 'Hero', components: [{ type: 'ScriptComponent', properties: { ScriptPath: 'Scripts/Hero.js' } }] },
            { name: 'Ghost', components: [{ type: 'ScriptComponent', properties: { ScriptPath: 'Scripts/Nope.js' } }] },
        ] }],
    }));
    fs.writeFileSync(path.join(dir, 'Scripts', 'Hero.js'), [
        'var speed = 1;',
        'function onUpdte(dt) {',                       // 2: misspelt hook
        '    if (Input.isMouseButtonPressed(0)) {}',    // 3: not in the contract
        '    var t = Scene.findByTag("Player").transform;', // 4: array used as an actor
        '    actor.hp = 5;',                           // 5: expando — a warning
        '    var enemies = Scene.findByTag("Enemy");',  // 6: an array …
        '    var ex = enemies.transform.x;',            // 7: … used as an actor two lines later
        '}',
    ].join('\n'));
    fs.writeFileSync(path.join(dir, 'Scripts', 'Bad.js'), 'function onStart( {');   // does not compile

    const result = validateProject(dir);
    const messages = (list) => list.map((p) => `${p.file}:${p.line} ${p.message}`);

    assert.ok(messages(result.errors).some((m) => m.startsWith('ProjectSettings.json:0 StartScene')));
    assert.ok(messages(result.errors).some((m) => m.startsWith('Scenes/Main.scene:0') && m.includes('Scripts/Nope.js')));
    assert.ok(messages(result.errors).some((m) => m === 'Scripts/Hero.js:3 Input.isMouseButtonPressed is not in the shared scripting contract'));
    assert.ok(messages(result.errors).some((m) => m.startsWith('Scripts/Bad.js:1 does not compile')));
    assert.ok(messages(result.warnings).some((m) => m.startsWith('Scripts/Hero.js:2') && m.includes("'onUpdate'")));
    assert.ok(messages(result.warnings).some((m) => m.startsWith('Scripts/Hero.js:4') && m.includes('findFirstByTag')));
    assert.ok(messages(result.warnings).some((m) => m.startsWith('Scripts/Hero.js:5') && m.includes('actor.hp')));
    assert.ok(messages(result.warnings).some((m) => m.startsWith('Scripts/Hero.js:7') && m.includes("'enemies' holds the array")));
    assert.ok(messages(result.warnings).some((m) => m.startsWith('Scripts/Bad.js:0 not referenced')));

    const lines = [];
    assert.equal(report(result, { log: (l) => lines.push(l) }), false);
    assert.ok(lines.at(-1).startsWith('FAIL:'));

    // --strict turns warnings into a failure; a clean result says OK.
    const clean = { dir, errors: [], warnings: [{ file: 'x', line: 1, message: 'w' }], scenes: 1, scripts: 1 };
    assert.equal(report(clean, { log: () => {} }), true);
    assert.equal(report(clean, { strict: true, log: () => {} }), false);

    fs.rmSync(dir, { recursive: true, force: true });
});

test('a module-style script is left to the browser loader', () => {
    const errors = [];
    checkScript('Scripts/Mod.js', 'export default class X {}', (f, l, m) => errors.push(m), () => {});
    assert.deepEqual(errors, []);
});

// -----------------------------------------------------------------------------
// export
// -----------------------------------------------------------------------------

test('a web export stages the engine, the project, the page and, with --pwa, the installable files', () => {
    const out = path.join(tempDir('export'), 'web');
    const result = exportWeb({ projectDir: helloWorld, out, pwa: true, version: '1.2.3' });

    const has = (file) => fs.existsSync(path.join(out, file));
    for (const file of ['index.html', 'HOW-TO-RUN.txt', 'ProjectSettings.json', 'PlatformDefines.json',
        'engine/runtime/Runtime.js', 'engine/runtime/runtime.css', 'engine/src/index.js',
        'Scenes/Tutorial.scene', 'Scripts/TutorialManager.js',
        'manifest.webmanifest', 'icon-192.png', 'icon-512.png', 'sw.js']) {
        assert.ok(has(file), `${file} was not staged`);
    }
    assert.ok(!has('engine/runtime/export/index.html.tmpl'), 'the templates are not part of a build');
    assert.ok(!has('engine/editor/Editor.js'), 'the editor is not part of a build');

    const html = fs.readFileSync(path.join(out, 'index.html'), 'utf8');
    assert.ok(html.startsWith('<!doctype html>'), 'no byte order mark, no leading whitespace');
    assert.ok(html.includes('<title>Hello World — SexyBiscuit Tutorial</title>'), 'the title is the WindowTitle');
    assert.ok(html.includes('data-scene="Scenes/Tutorial"'));
    assert.ok(html.includes('data-stats="false"'), 'a Release build hides the stats overlay');
    assert.ok(html.includes('rel="manifest"'));
    assert.ok(html.includes("serviceWorker.register('./sw.js')"));
    assert.ok(html.includes('id="fullscreen"'), 'the exported page has the fullscreen button');
    assert.ok(!html.includes('{{'), 'every placeholder was filled');

    const manifest = JSON.parse(fs.readFileSync(path.join(out, 'manifest.webmanifest'), 'utf8'));
    assert.equal(manifest.name, 'Hello World — SexyBiscuit Tutorial');
    assert.ok(manifest.short_name.length <= 12);
    assert.equal(manifest.display, 'fullscreen');

    const worker = fs.readFileSync(path.join(out, 'sw.js'), 'utf8');
    const precache = JSON.parse(/const PRECACHE = (\[[\s\S]*?\]);/.exec(worker)[1]);
    assert.ok(precache.includes('./index.html'));
    assert.ok(precache.includes('./engine/runtime/Runtime.js'));
    assert.ok(!precache.includes('./sw.js'), 'the worker does not cache itself');
    assert.ok(worker.includes('hello-world-sexybiscuit-tutorial-1.2.3-'), 'the cache name carries slug and version');

    const png = fs.readFileSync(path.join(out, 'icon-192.png'));
    assert.deepEqual([...png.subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

    assert.ok(result.archive.endsWith('hello-world-sexybiscuit-tutorial-1.2.3-web.zip'));
    const entries = listZip(result.archive);
    assert.equal(entries.length, result.files.length, 'every staged file is in the archive');
    assert.ok(entries.includes('index.html'));

    const buildReport = JSON.parse(fs.readFileSync(path.join(path.dirname(out), 'build-report.json'), 'utf8'));
    assert.equal(buildReport.targets[0].platform, 'Web');
    assert.ok(result.report.startsWith('web  ok'));

    fs.rmSync(path.dirname(out), { recursive: true, force: true });
});

test('the page template and the C# exporter share their placeholders', () => {
    // The C# pipeline fills the same files; a placeholder one side does not know is a
    // build with `{{` in it. The set is pinned here and in WebExportTests.
    const templates = path.join(repoRoot, 'html5', 'runtime', 'export');
    const placeholders = (file) => [...new Set([...fs.readFileSync(path.join(templates, file), 'utf8').matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]))].sort();

    assert.deepEqual(placeholders('index.html.tmpl'),
        ['build', 'dgBoot', 'dgHead', 'pwaBoot', 'pwaHead', 'scene', 'stats', 'themeColor', 'title']);
    assert.deepEqual(placeholders('manifest.webmanifest.tmpl'), ['name', 'shortName', 'themeColor']);
    assert.deepEqual(placeholders('sw.js.tmpl'), ['cacheName', 'precache']);
    assert.throws(() => fill('{{missing}}', {}), /placeholder/);
});

test('the zip writer produces an archive the listing reads back', () => {
    const dir = tempDir('zip');
    fs.mkdirSync(path.join(dir, 'nested', 'deep'), { recursive: true });
    fs.writeFileSync(path.join(dir, 'a.txt'), 'alpha');
    fs.writeFileSync(path.join(dir, 'nested', 'deep', 'b.json'), '{"b": 2}');
    fs.writeFileSync(path.join(dir, 'nested', 'run.sh'), '#!/bin/sh\n', { mode: 0o755 });

    const zip = path.join(dir, 'out', 'test.zip');
    assert.equal(zipDirectory(dir, zip), 3);
    assert.deepEqual(listZip(zip), ['a.txt', 'nested/deep/b.json', 'nested/run.sh']);

    fs.rmSync(dir, { recursive: true, force: true });
});

test('small helpers behave', () => {
    assert.equal(slugify('My Great Game!'), 'my-great-game');
    assert.deepEqual(parseHexColour('#12141a'), [0x12, 0x14, 0x1a, 0xff]);
    assert.deepEqual(parseHexColour('nonsense', [1, 2, 3, 4]), [1, 2, 3, 4]);
    assert.ok(solidPng(4, [1, 2, 3, 4]).length > 30);
    const sha = readGitSha(repoRoot);
    assert.ok(sha === null || /^[0-9a-f]{40}$/.test(sha), `unexpected sha ${sha}`);
});

// -----------------------------------------------------------------------------
// upload
// -----------------------------------------------------------------------------

test('the uploader publishes raw bytes with a base64url metadata header, and retries a rate limit', async () => {
    const received = [];
    let limitFirst = true;
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sb-publish-'));
    const file = path.join(dir, 'hello-world-1.0.0-osx-arm64.tar.gz');
    const payload = Buffer.from('not really an archive');
    fs.writeFileSync(file, payload);
    const checksum = crypto.createHash('sha256').update(payload).digest('hex');

    const server = http.createServer((request, response) => {
        const chunks = [];
        request.on('data', (chunk) => chunks.push(chunk));
        request.on('end', () => {
            received.push({ headers: request.headers, body: Buffer.concat(chunks) });
            if (limitFirst) {
                limitFirst = false;
                response.writeHead(429, { 'content-type': 'application/json', 'retry-after': '1' })
                        .end(JSON.stringify({ error: 'rate_limited', message: 'slow down' }));
                return;
            }
            response.writeHead(201, { 'content-type': 'application/json' }).end(JSON.stringify({
                id: 'bld_1234', url: 'https://darksgames.app/api/v1/builds/bld_1234/download',
                page: 'https://darksgames.app/downloads', checksum, replaced: false, published: true, ready: true,
            }));
        });
    });
    await new Promise((resolve) => server.listen(0, resolve));
    const url = `http://127.0.0.1:${server.address().port}/publish`;

    try {
        const result = await publish({
            file, url, token: 'secret', appSlug: 'hello-world', title: 'Hello World',
            version: '1.0.0', rid: 'osx-arm64', channel: 'alpha', notes: 'First cut.', env: {},
        });

        assert.equal(result.ok, true);
        assert.equal(result.status, 201);
        assert.equal(result.id, 'bld_1234');
        assert.equal(result.url, 'https://darksgames.app/api/v1/builds/bld_1234/download');
        assert.equal(result.platform, 'macos');
        assert.equal(received.length, 2, 'a 429 is retried once');

        const last = received.at(-1);
        assert.equal(last.headers.authorization, 'Bearer secret');
        assert.equal(last.headers['content-type'], 'application/octet-stream');
        assert.ok(last.body.equals(payload), 'the body is the file itself, not a multipart wrapper');

        const meta = JSON.parse(Buffer.from(last.headers['x-build-meta'], 'base64url').toString());
        assert.equal(meta.appSlug, 'hello-world');
        assert.equal(meta.title, 'Hello World');
        assert.equal(meta.version, '1.0.0');
        assert.equal(meta.platform, 'macos');
        assert.equal(meta.channel, 'alpha');
        assert.equal(meta.fileName, 'hello-world-1.0.0-osx-arm64.tar.gz');
        assert.equal(meta.replace, true);
        assert.equal(meta.publish, true);
        assert.ok(meta.notes.startsWith('First cut.'));
        assert.ok(meta.notes.includes('osx-arm64'), 'the provenance line names the runtime');
    } finally {
        server.close();
        fs.rmSync(dir, { recursive: true, force: true });
    }
});

test('the uploader refuses what the API would reject, before sending anything', async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sb-publish-'));
    const archive = path.join(dir, 'game-1.0.0-win-x64.zip');
    fs.writeFileSync(archive, 'x');

    try {
        const base = { file: archive, url: 'http://127.0.0.1:1/none', appSlug: 'game', version: '1.0.0', env: {} };

        // No token anywhere, and a token file that does not exist.
        await assert.rejects(() => publish({ ...base, tokenFile: path.join(dir, 'nope') }), UploadConfigError);

        // A web build is not a downloadable game build.
        await assert.rejects(() => publish({ ...base, token: 't', platform: 'web' }), UploadConfigError);

        // The extension allowlist is a security control, not tidiness.
        const page = path.join(dir, 'game-1.0.0-win-x64.html');
        fs.writeFileSync(page, 'x');
        await assert.rejects(() => publish({ ...base, file: page, token: 't' }), UploadConfigError);

        // The slug is the game's identity on the site.
        await assert.rejects(() => publish({ ...base, token: 't', appSlug: 'Hello World!' }), UploadConfigError);
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
});

test('the metadata header maps platforms and channels the way the C# target does', () => {
    assert.equal(platformFor('win-x64'), 'windows');
    assert.equal(platformFor('osx-arm64'), 'macos');
    assert.equal(platformFor('linux-x64'), 'linux');
    assert.equal(platformFor('android'), 'android');
    assert.equal(platformFor('ios'), 'other');
    assert.equal(platformFor('web'), null);

    assert.equal(channelFor('alpha'), 'alpha');
    assert.equal(channelFor('dev'), 'alpha');
    assert.equal(channelFor('playtest'), 'beta');
    assert.equal(channelFor('release'), 'demo');

    // A note long enough to overflow the 6144-byte header is given back until it fits.
    const meta = { appSlug: 'game', title: 'Game', version: '1.0.0', fileName: 'g.zip', notes: '—'.repeat(3900), requirements: '' };
    const header = encodeMeta(meta);
    assert.ok(header.length <= 6144, `header was ${header.length}`);
    assert.ok(meta.notes.endsWith('…'));
});

test('the dev server injects the reload snippet only when watching', async () => {
    const root = tempDir('serve');
    fs.writeFileSync(path.join(root, 'index.html'), '<!doctype html><html><body><p>hi</p></body></html>');

    const plain = await serve({ port: 0, root, log: () => {} });
    try {
        const html = await (await fetch(`http://127.0.0.1:${plain.port}/`)).text();
        assert.ok(!html.includes('__reload'));
    } finally { await plain.close(); }

    const watching = await serve({ port: 0, root, watch: true, log: () => {} });
    try {
        const html = await (await fetch(`http://127.0.0.1:${watching.port}/index.html`)).text();
        assert.ok(html.includes('EventSource("/__reload")'));
        assert.ok(html.trimEnd().endsWith('</html>'), 'the snippet goes inside the body');

        const forbidden = await fetch(`http://127.0.0.1:${watching.port}/../package.json`);
        assert.ok([403, 404].includes(forbidden.status));
    } finally { await watching.close(); }

    fs.rmSync(root, { recursive: true, force: true });
});

// -----------------------------------------------------------------------------
// usage
// -----------------------------------------------------------------------------

test('the usage meter sums one API call once and attributes results to tools', async () => {
    const { summarise, format, transcriptDir } = await import('../tools/usage.js');
    const dir = tempDir('usage');
    const file = path.join(dir, 'session.jsonl');

    const usage = { input_tokens: 10, cache_creation_input_tokens: 100, cache_read_input_tokens: 1000, output_tokens: 50, output_tokens_details: { thinking_tokens: 20 } };
    const lines = [
        { type: 'user', message: { role: 'user', content: 'make a game' } },
        // One API call streamed as two lines that share a request id: counted once.
        { type: 'assistant', requestId: 'r1', message: { model: 'claude-opus-5', usage, content: [{ type: 'text', text: 'ok' }] } },
        { type: 'assistant', requestId: 'r1', message: { model: 'claude-opus-5', usage, content: [{ type: 'tool_use', id: 't1', name: 'Read', input: { path: 'a.js' } }] } },
        { type: 'user', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: 't1', content: 'x'.repeat(400) }] } },
        { type: 'assistant', requestId: 'r2', message: { model: 'claude-opus-5', usage, content: [{ type: 'tool_use', id: 't2', name: 'capture', input: {} }] } },
        { type: 'user', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: 't2', content: [{ type: 'image', source: {} }] }] } },
        { type: 'assistant', requestId: 'r3', isSidechain: true, message: { model: 'claude-opus-5', usage, content: [] } },   // a subagent: ignored
    ];
    fs.writeFileSync(file, lines.map((l) => JSON.stringify(l)).join('\n') + '\n');

    const summary = summarise(file);
    assert.equal(summary.calls, 2);
    assert.equal(summary.prompts, 1);
    assert.equal(summary.input, 20);
    assert.equal(summary.cacheRead, 2000);
    assert.equal(summary.output, 100);
    assert.equal(summary.thinking, 40);
    assert.equal(summary.contextPerCall, 1110);
    assert.ok(Math.abs(summary.cacheShare - 2000 / 2220) < 1e-9);
    assert.ok(summary.costUsd > 0);

    const read = summary.tools.find((t) => t.name === 'Read');
    assert.equal(read.calls, 1);
    assert.equal(read.resultChars, 400);
    assert.equal(read.resultTokens, 100);
    assert.equal(summary.tools.find((t) => t.name === 'capture').resultTokens, 800, 'an image costs a flat amount');
    assert.equal(summary.tools[0].name, 'capture', 'sorted by tokens returned');

    const text = format(summary).join('\n');
    assert.ok(text.includes('API calls 2'));
    assert.ok(text.includes('Read'));
    assert.ok(transcriptDir('/Users/x/My Game').endsWith('-Users-x-My-Game'));

    fs.rmSync(dir, { recursive: true, force: true });
});

// -----------------------------------------------------------------------------
// Two ways the web export could quietly disagree with the native one.
// -----------------------------------------------------------------------------

test('the web export takes its version from BuildSettings.json, as sbengine does', async () => {
    const { buildSettingsVersion } = await import('../tools/export.js');

    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sb-version-'));
    fs.writeFileSync(path.join(dir, 'BuildSettings.json'), JSON.stringify({ appName: 'X', version: '2.3.4' }));
    assert.equal(buildSettingsVersion(dir), '2.3.4');

    // No file, and a malformed one, both fall back rather than failing an export.
    assert.equal(buildSettingsVersion(fs.mkdtempSync(path.join(os.tmpdir(), 'sb-version-'))), null);
    const broken = fs.mkdtempSync(path.join(os.tmpdir(), 'sb-version-'));
    fs.writeFileSync(path.join(broken, 'BuildSettings.json'), '{ not json');
    assert.equal(buildSettingsVersion(broken), null);
});

test('readGitSha finds the ref from inside a worktree, not just a normal checkout', async () => {
    const { readGitSha } = await import('../tools/export.js');

    // A worktree's gitdir has HEAD but no refs/: those live in the common dir it
    // points at. Missing that returned null, the service worker's cache name fell
    // back to "local", and every export from a worktree produced a worker whose
    // name never changed — so a redeploy left existing players on the old build.
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sb-worktree-'));
    const common = path.join(root, 'common.git');
    const wt = path.join(root, 'wt.git');
    const project = path.join(root, 'project');
    fs.mkdirSync(path.join(common, 'refs', 'heads'), { recursive: true });
    fs.mkdirSync(wt, { recursive: true });
    fs.mkdirSync(project, { recursive: true });

    fs.writeFileSync(path.join(common, 'refs', 'heads', 'main'), 'a'.repeat(40) + '\n');
    fs.writeFileSync(path.join(wt, 'HEAD'), 'ref: refs/heads/main\n');
    fs.writeFileSync(path.join(wt, 'commondir'), '../common.git\n');
    fs.writeFileSync(path.join(project, '.git'), `gitdir: ${wt}\n`);

    assert.equal(readGitSha(project), 'a'.repeat(40));
});
