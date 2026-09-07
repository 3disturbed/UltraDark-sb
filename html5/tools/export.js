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
//     node html5/tools/export.js <projectDir> [--out <dir>] [--pwa] [--icon <png>] [--dg <slug>] [--gated]
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
export function exportWeb({ projectDir, out, pwa = false, icon, version, configuration = 'Release', zip = true, dg, gated = false, log = () => {} }) {
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

    // ---- Darks Games ------------------------------------------------------------
    // The slug comes from the command line or from ProjectSettings, and its presence
    // is the whole switch: a build with one carries the account and social layer, and
    // a build without one is unchanged.
    const dgSlug = dg ?? readDarksGamesSlug(settings);
    const { head: dgHead, boot: dgBoot } = darksGamesTags(dgSlug);
    if (dgSlug) log(`  Staged: Darks Games SDKs for '${dgSlug}'`);

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
        dgHead,
        dgBoot,
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

    // ---- Closed testing ----------------------------------------------------------
    if (gated) {
        if (!dgSlug) throw new Error('--gated needs a Darks Games slug: pass --dg <slug>.');
        writeGatedHost(outDir, { slug: dgSlug, title: appName, log });
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

/**
 * The catalogue slug, from ProjectSettings.
 *
 * Accepted as `darksGames: "slug"` or `darksGames: { slug }`, and case-insensitively,
 * because ProjectSettings is hand-edited as often as it is written by a tool.
 */
export function readDarksGamesSlug(settings) {
    const lookup = new Map(Object.entries(settings ?? {}).map(([k, v]) => [k.toLowerCase(), v]));
    const value = lookup.get('darksgames') ?? lookup.get('dg');

    if (typeof value === 'string') return value.trim().toLowerCase() || null;
    if (value && typeof value === 'object') {
        const slug = value.slug ?? value.Slug ?? value.app ?? value.App;
        if (typeof slug === 'string') return slug.trim().toLowerCase() || null;
    }
    return null;
}

/**
 * The two script tags and the boot line a Darks Games build carries.
 *
 * Order is not cosmetic. `dg-overlay.v1.js` strips `?dg_party` and `?dg_launch` out
 * of the URL the moment it executes, so it has to run before any game code reads
 * `location` — which, with `defer`, means before the module script. The account SDK
 * precedes it because the overlay asks it for a token.
 *
 * A page with no slug gets neither tag, so a game that never ships to DarksGames
 * loads nothing from it.
 */
export function darksGamesTags(slug, { origin = 'https://darksgames.app' } = {}) {
    if (!slug) return { head: '', boot: '' };

    const head = [
        `<script src="${origin}/sdk/dg-account.v1.js" crossorigin="anonymous" defer></script>`,
        `<script src="${origin}/sdk/dg-overlay.v1.js" crossorigin="anonymous" defer></script>`,
        '',
    ].join('\n');

    // The join handler is the one piece a game must own, so the export wires the
    // default: put the code in the URL and reload, which re-runs whatever deep-link
    // path the game already has. A game that can join in place overrides
    // `window.sbJoinRoom` and returns true.
    const boot = `
// ---- Darks Games ----
// Identity, friends, presence and Join, wired to this build. A player who is signed
// out, offline or blocked from the hub sees none of it and the game is unaffected.
import { DarksGames } from './engine/src/dg/index.js';

const dg = new DarksGames();
window.DG = dg;
await dg.init({
    game: ${JSON.stringify(slug)},
    onJoin: (code) => (window.sbJoinRoom ? window.sbJoinRoom(code) : false),
}).catch((err) => console.warn('[DarksGames]', err.message));
`;

    return { head, boot };
}

/**
 * Rearranges the build into the layout a gated Darks Games host needs, and writes
 * the host.
 *
 * The build moves into `game/` and `public/` holds only the gate. That is not
 * tidiness: nginx's `try_files $uri $uri/index.html @node` serves anything under
 * `public/` straight off disk without touching Node, so a build left there would be
 * ungated and the lock would be decoration. `/play/*` exists nowhere on disk, so
 * every request for it falls through to the host, which is where the session is
 * checked.
 */
function writeGatedHost(outDir, { slug, title, log }) {
    const gameDir = path.join(outDir, 'game');
    fs.mkdirSync(gameDir, { recursive: true });

    for (const entry of fs.readdirSync(outDir)) {
        if (entry === 'game') continue;
        fs.renameSync(path.join(outDir, entry), path.join(gameDir, entry));
    }

    const publicDir = path.join(outDir, 'public');
    const authDir = path.join(outDir, 'server', 'auth');
    fs.mkdirSync(publicDir, { recursive: true });
    fs.mkdirSync(authDir, { recursive: true });

    const values = { slug, slugJson: JSON.stringify(slug), title: escapeHtml(title) };

    fs.writeFileSync(path.join(publicDir, 'index.html'), fill(gatedTemplate('gate.html.tmpl'), values));
    fs.writeFileSync(path.join(authDir, 'dgVerify.js'), fill(gatedTemplate('dgVerify.js.tmpl'), values));
    fs.writeFileSync(path.join(outDir, 'server', 'social.js'), fill(gatedTemplate('social.js.tmpl'), values));
    fs.writeFileSync(path.join(outDir, 'server.js'), fill(gatedTemplate('server.js.tmpl'), values));

    // node needs to be told these are modules, and the host is one.
    fs.writeFileSync(path.join(outDir, 'package.json'),
        JSON.stringify({ name: slug, private: true, type: 'module', main: 'server.js' }, null, 2) + '\n');

    fs.writeFileSync(path.join(outDir, 'DEPLOY.txt'), [
        `${title} — closed testing on DarksGames`,
        '',
        'This is a gated build. The game is in game/, not public/, on purpose:',
        "nginx serves public/ off disk without touching Node, so a build there would",
        'be ungated. /play/* falls through to server.js, which checks the session.',
        '',
        'On the server, as root:',
        `    mkdir -p /srv/darksgames/games/${slug}`,
        `    rsync -a --delete ./ /srv/darksgames/games/${slug}/`,
        `    printf 'SESSION_SECRET=%s\\n' "$(openssl rand -hex 32)" > /srv/darksgames/games/${slug}/.env`,
        `    chmod 600 /srv/darksgames/games/${slug}/.env`,
        `    chown -R darks:darks /srv/darksgames/games/${slug}`,
        `    add-game ${slug}.darksgames.app ${slug}`,
        '',
        'Then, in dg-accounts:',
        '  * seed the apps row      sudo -u darks node scripts/seed-apps.js',
        `  * add the catalogue entry for "${slug}" in social/catalog.json`,
        '  * grant testers the flag  Admin -> Users -> Grant playtester',
        '',
        'The listing flag hides the tile; this host is the lock. Ship both.',
        '',
    ].join('\n'));

    log('  Written: server.js, public/index.html, server/auth/dgVerify.js, server/social.js');
}

function gatedTemplate(name) {
    return fs.readFileSync(path.join(html5Root, 'runtime', 'export', 'gated', name), 'utf8');
}

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
    const projectDir = args.find((a, i) => !a.startsWith('--') && !['--out', '--icon', '--version', '--config', '--dg'].includes(args[i - 1]));

    if (!projectDir) {
        console.error('usage: node html5/tools/export.js <projectDir> [--out <dir>] [--pwa] [--icon <png>] [--dg <slug>] [--gated] [--version <v>] [--config <c>] [--no-zip] [--quiet]');
        process.exit(2);
    }

    const quiet = args.includes('--quiet');
    try {
        const result = exportWeb({
            projectDir,
            out: take('--out'),
            pwa: args.includes('--pwa'),
            icon: take('--icon'),
            dg: take('--dg'),
            gated: args.includes('--gated'),
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
