#!/usr/bin/env node
// -----------------------------------------------------------------------------
// export — stages a project as a web build, with nothing but node.
//
// The engine is already JavaScript and the project's scenes, scripts and assets
// are already in the formats it reads, so a web build is the two put together
// with a page that boots them. This is the same staging the C# export pipeline
// does for `BuildPlatform.Web`; the page, manifest and service worker come from
// the templates under `runtime/export/`, which both sides fill, so the two
// exports are one build. It exists separately so the prototype phase — an agent
// working on a machine with no .NET — can publish a playtest build.
//
//     node html5/tools/export.js <projectDir> [--out <dir>] [--pwa] [--icon <png>]
//                                [--version <v>] [--config Release|Development|Debug]
//                                [--no-zip] [--quiet]
//
// Output: <projectDir>/dist/Web/ (or --out) and, beside it, <slug>-<version>-web.zip.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { EngineConfig } from '../src/index.js';
import { zipDirectory } from './lib/zip.js';
import { solidPng, parseHexColour } from './lib/png.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const html5Root = path.join(here, '..');
const templatesDir = path.join(html5Root, 'runtime', 'export');

const THEME_COLOUR = '#12141a';

/**
 * Stages the build.
 *
 * @param {object} options
 * @param {string} options.projectDir
 * @param {string} [options.out] Output folder; default `<projectDir>/dist/web`.
 * @param {boolean} [options.pwa=false] Write the manifest, icons and service worker.
 * @param {string} [options.icon] A PNG to use for the icons instead of a flat square.
 * @param {string} [options.version] Default: ProjectSettings version or "1.0.0".
 * @param {string} [options.configuration="Release"]
 * @param {boolean} [options.zip=true]
 * @param {(line: string) => void} [options.log]
 * @returns {{ outDir: string, files: string[], archive: string|null, archiveBytes: number, appName: string, version: string, report: string }}
 */
export function exportWeb({ projectDir, out, pwa = false, icon, version, configuration = 'Release', zip = true, log = () => {} }) {
    const project = path.resolve(projectDir);
    const settingsFile = path.join(project, 'ProjectSettings.json');
    if (!fs.existsSync(settingsFile)) throw new Error(`${projectDir} has no ProjectSettings.json`);

    const settingsText = fs.readFileSync(settingsFile, 'utf8');
    const config = EngineConfig.fromProjectSettings(settingsText);
    const settings = JSON.parse(settingsText.replace(/^﻿/, ''));

    const appName = config.windowTitle || path.basename(project);

    // BuildSettings.json first, because that is the file `sbengine` versions a
    // build from. Reading only ProjectSettings meant the same project exported as
    // 1.0.1 natively and 1.0.0 on the web — two builds wearing one version, which
    // is exactly what this file's header promises cannot happen.
    version = version ?? buildSettingsVersion(project) ?? settings.version ?? settings.Version ?? '1.0.0';
    const slug = slugify(appName);
    const outDir = path.resolve(out ?? path.join(project, 'dist', 'Web'));

    if (fs.existsSync(outDir)) fs.rmSync(outDir, { recursive: true, force: true });
    fs.mkdirSync(outDir, { recursive: true });

    // ---- Engine ---------------------------------------------------------------
    // Only the runtime is needed to play a game; the editor, the tests, the
    // examples and these templates are not part of a shipped build.
    copyDir(path.join(html5Root, 'src'), path.join(outDir, 'engine', 'src'));
    copyDir(path.join(html5Root, 'runtime'), path.join(outDir, 'engine', 'runtime'), (rel) => !rel.startsWith('export'));
    log('  Staged: engine/src, engine/runtime');

    // ---- Project ----------------------------------------------------------------
    for (const folder of ['Assets', 'Scripts', 'Scenes']) {
        const from = path.join(project, folder);
        if (fs.existsSync(from)) {
            copyDir(from, path.join(outDir, folder));
            log(`  Staged: ${folder}/`);
        }
    }
    fs.copyFileSync(settingsFile, path.join(outDir, 'ProjectSettings.json'));

    fs.writeFileSync(path.join(outDir, 'PlatformDefines.json'),
        JSON.stringify(['PLATFORM_WEB', 'ARCH_WASM', `BUILD_${configuration.toUpperCase()}`], null, 2) + '\n');

    // ---- Page, manifest, worker --------------------------------------------------
    const scene = config.startScene ?? '';
    const stats = configuration === 'Release' ? 'false' : 'true';
    const gitSha = readGitSha(project);

    // One string that identifies this build, used as the service worker's cache
    // name AND stamped into the page. A console paste from a player then says
    // which build they are running, which is otherwise unanswerable: a stale
    // service worker serves old files whose line numbers look exactly like the
    // new ones.
    const buildId = `${slug}-${version}-${gitSha ? gitSha.slice(0, 8) : 'local'}`;

    let pwaHead = '';
    let pwaBoot = '';
    if (pwa) {
        const colour = parseHexColour(THEME_COLOUR);
        for (const size of [192, 512]) {
            const target = path.join(outDir, `icon-${size}.png`);
            if (icon) fs.copyFileSync(icon, target);
            else fs.writeFileSync(target, solidPng(size, colour));
        }
        fs.writeFileSync(path.join(outDir, 'manifest.webmanifest'), fill(template('manifest.webmanifest.tmpl'), {
            name: JSON.stringify(appName),
            shortName: JSON.stringify(appName.length > 12 ? appName.slice(0, 12) : appName),
            themeColor: THEME_COLOUR,
        }));
        pwaHead = '<link rel="manifest" href="manifest.webmanifest">\n<link rel="apple-touch-icon" href="icon-192.png">\n';
        pwaBoot = "\nif ('serviceWorker' in navigator) navigator.serviceWorker.register('./sw.js').catch(() => {});\n";
        log('  Written: manifest.webmanifest, icon-192.png, icon-512.png');
    }

    fs.writeFileSync(path.join(outDir, 'index.html'), fill(template('index.html.tmpl'), {
        title: escapeHtml(appName),
        scene: escapeHtml(scene),
        stats,
        themeColor: THEME_COLOUR,
        build: escapeHtml(buildId),
        pwaHead,
        pwaBoot,
    }));

    fs.writeFileSync(path.join(outDir, 'HOW-TO-RUN.txt'), [
        `${appName} ${version} — web build`,
        '',
        'Serve this folder over HTTP and open index.html.',
        'Opening it directly from disk will not work: browsers refuse to load',
        'ES modules from a file:// path, and a service worker needs HTTPS or localhost.',
        '',
        'Any static host will do. To try it locally:',
        '    npx serve .',
        'or, from an engine checkout:',
        '    node html5/tools/serve.js 8080 .',
        '',
    ].join('\n'));

    // The worker is written last: its precache list is every other file.
    if (pwa) {
        const precache = listFiles(outDir).filter((f) => f !== 'sw.js' && f !== 'HOW-TO-RUN.txt');
        fs.writeFileSync(path.join(outDir, 'sw.js'), fill(template('sw.js.tmpl'), {
            cacheName: buildId,
            precache: JSON.stringify(precache.map((f) => `./${f}`), null, 2),
        }));
        log('  Written: sw.js');
    }

    const files = listFiles(outDir);
    log(`  Written: index.html (${files.length} files)`);

    // ---- Archive ---------------------------------------------------------------
    let archive = null;
    let archiveBytes = 0;
    if (zip) {
        archive = path.join(path.dirname(outDir), `${slug}-${version}-web.zip`);
        zipDirectory(outDir, archive);
        archiveBytes = fs.statSync(archive).size;
    }

    const report = {
        appName, version, configuration, gitSha, builtUtc: new Date().toISOString(),
        targets: [{ platform: 'Web', success: true, outputPath: outDir, archive, archiveBytes, files: files.length }],
    };
    fs.writeFileSync(path.join(path.dirname(outDir), 'build-report.json'), JSON.stringify(report, null, 2) + '\n');

    const summary = `web  ok  ${String(files.length).padStart(4)} files  ${archive ? `${path.basename(archive)} (${formatBytes(archiveBytes)})` : outDir}`;
    return { outDir, files, archive, archiveBytes, appName, version, report: summary };
}

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

function template(name) {
    return fs.readFileSync(path.join(templatesDir, name), 'utf8');
}

/** Replaces every {{key}} with its value. A key with no value is an error, not a blank. */
export function fill(text, values) {
    return text.replace(/\{\{(\w+)\}\}/g, (_, key) => {
        if (!(key in values)) throw new Error(`template placeholder {{${key}}} has no value`);
        return values[key];
    });
}

function copyDir(from, to, keep = () => true) {
    for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
        const rel = path.relative(from, path.join(from, entry.name));
        if (!keep(rel)) continue;
        const source = path.join(from, entry.name);
        const target = path.join(to, entry.name);
        if (entry.isDirectory()) {
            copyDir(source, target, (inner) => keep(path.join(rel, inner)));
        } else if (entry.isFile()) {
            fs.mkdirSync(path.dirname(target), { recursive: true });
            fs.copyFileSync(source, target);
        }
    }
}

/** Every file under dir, relative, forward slashes, sorted. */
export function listFiles(dir) {
    const found = [];
    const visit = (current) => {
        for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
            const full = path.join(current, entry.name);
            if (entry.isDirectory()) visit(full);
            else found.push(path.relative(dir, full).split(path.sep).join('/'));
        }
    };
    visit(dir);
    return found.sort();
}

/** The HEAD commit of the repository containing dir, read from .git without spawning git. */
/** The version in BuildSettings.json, which is the one a native build carries. */
export function buildSettingsVersion(projectDir) {
    const file = path.join(projectDir, 'BuildSettings.json');
    if (!fs.existsSync(file)) return null;
    try {
        const settings = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\ufeff/, ''));
        const version = settings.version ?? settings.Version;
        return typeof version === 'string' && version.length > 0 ? version : null;
    } catch {
        return null;   // a malformed file falls back rather than failing the export
    }
}

export function readGitSha(dir) {
    let current = path.resolve(dir);
    while (current) {
        const dotGit = path.join(current, '.git');
        if (fs.existsSync(dotGit)) {
            let gitDir = dotGit;
            if (fs.statSync(dotGit).isFile()) {
                const pointer = /^gitdir:\s*(.+)$/m.exec(fs.readFileSync(dotGit, 'utf8'));
                if (!pointer) return null;
                gitDir = path.resolve(current, pointer[1].trim());
            }
            try {
                const head = fs.readFileSync(path.join(gitDir, 'HEAD'), 'utf8').trim();
                const ref = /^ref:\s*(.+)$/.exec(head);
                if (!ref) return head;
                // A worktree's gitdir holds HEAD but no refs: those live in the
                // common dir it points at. Missing this returned null, the cache
                // name fell back to "local", and every export from a worktree
                // produced a service worker whose name never changed — so a
                // redeploy left every existing player on the build they already
                // had, which is the worst possible way for this to fail.
                const commonFile = path.join(gitDir, 'commondir');
                const dirs = [gitDir];
                if (fs.existsSync(commonFile)) {
                    dirs.push(path.resolve(gitDir, fs.readFileSync(commonFile, 'utf8').trim()));
                }

                for (const dir of dirs) {
                    const refFile = path.join(dir, ref[1]);
                    if (fs.existsSync(refFile)) return fs.readFileSync(refFile, 'utf8').trim();

                    const packed = path.join(dir, 'packed-refs');
                    if (fs.existsSync(packed)) {
                        const line = fs.readFileSync(packed, 'utf8').split('\n').find((l) => l.endsWith(' ' + ref[1]));
                        if (line) return line.split(' ')[0];
                    }
                }
            } catch {
                return null;
            }
            return null;
        }
        const parent = path.dirname(current);
        if (parent === current) return null;
        current = parent;
    }
    return null;
}

export function slugify(name) {
    return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'game';
}

function escapeHtml(text) {
    return String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

function formatBytes(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

// -----------------------------------------------------------------------------
// CLI
// -----------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    const take = (flag) => { const i = args.indexOf(flag); return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined; };
    const projectDir = args.find((a, i) => !a.startsWith('--') && !['--out', '--icon', '--version', '--config'].includes(args[i - 1]));

    if (!projectDir) {
        console.error('usage: node html5/tools/export.js <projectDir> [--out <dir>] [--pwa] [--icon <png>] [--version <v>] [--config <c>] [--no-zip] [--quiet]');
        process.exit(2);
    }

    const quiet = args.includes('--quiet');
    try {
        const result = exportWeb({
            projectDir,
            out: take('--out'),
            pwa: args.includes('--pwa'),
            icon: take('--icon'),
            version: take('--version'),
            configuration: take('--config') ?? 'Release',
            zip: !args.includes('--no-zip'),
            log: quiet ? () => {} : console.log,
        });
        console.log(result.report);
    } catch (err) {
        console.error(`export failed: ${err.message}`);
        process.exit(1);
    }
}
