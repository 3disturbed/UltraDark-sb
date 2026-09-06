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
import { fileURLToPath } from 'node:url';

import { validateProject, templateProjects, report, checkScript } from '../tools/validate.js';
import { exportWeb, fill, readGitSha, slugify } from '../tools/export.js';
import { zipDirectory, listZip } from '../tools/lib/zip.js';
import { solidPng, parseHexColour } from '../tools/lib/png.js';
import { upload, UploadConfigError } from '../tools/upload.js';
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

    assert.deepEqual(placeholders('index.html.tmpl'), ['pwaBoot', 'pwaHead', 'scene', 'stats', 'themeColor', 'title']);
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

test('the uploader posts multipart with a bearer token and reads the location back', async () => {
    const received = [];
    let failFirst = true;
    const server = http.createServer((request, response) => {
        let body = '';
        request.on('data', (chunk) => { body += chunk.toString('latin1'); });
        request.on('end', () => {
            received.push({ headers: request.headers, body });
            if (failFirst) {
                failFirst = false;
                response.writeHead(503).end('busy');
                return;
            }
            response.writeHead(201, { 'content-type': 'application/json' }).end(JSON.stringify({ url: 'https://darksgames.app/builds/42' }));
        });
    });
    await new Promise((resolve) => server.listen(0, resolve));
    const url = `http://127.0.0.1:${server.address().port}/upload`;

    const dir = tempDir('upload');
    const file = path.join(dir, 'hello-world-1.0.0-web.zip');
    fs.writeFileSync(file, 'not really a zip');

    try {
        const result = await upload({
            file, url, token: 'secret', metadata: { game: 'Hello World', version: '1.0.0' }, fields: { game: 'title' },
        });
        assert.equal(result.ok, true);
        assert.equal(result.status, 201);
        assert.equal(result.url, 'https://darksgames.app/builds/42');
        assert.equal(result.platform, 'web');
        assert.equal(received.length, 2, 'a 5xx is retried once');

        const last = received.at(-1);
        assert.equal(last.headers.authorization, 'Bearer secret');
        assert.ok(last.headers['content-type'].startsWith('multipart/form-data'));
        assert.ok(last.body.includes('name="title"'), 'the field map renamed game to title');
        assert.ok(last.body.includes('Hello World'));
        assert.ok(last.body.includes('name="sha256"'));
        assert.ok(last.body.includes('filename="hello-world-1.0.0-web.zip"'));

        await assert.rejects(() => upload({ file, url, token: undefined, env: {} }), UploadConfigError);
        await assert.rejects(() => upload({ file, url: undefined, token: 't', env: {} }), UploadConfigError);
    } finally {
        server.close();
        fs.rmSync(dir, { recursive: true, force: true });
    }
});

// -----------------------------------------------------------------------------
// serve --watch
// -----------------------------------------------------------------------------

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
