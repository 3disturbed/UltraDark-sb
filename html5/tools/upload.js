// upload — publishes a finished native build to DarksGames.
//
// One POST whose body is the archive's raw bytes and whose metadata rides in a
// base64url `X-Build-Meta` header. Not multipart, not base64, not a JSON
// wrapper: the body is the file, streamed from disk so a 600 MB archive never
// sits in memory. The C# pipeline's DarksGamesUploadTarget sends exactly the
// same request, so a build published from either side looks the same.
//
// Only native builds are published — a web build is not a downloadable game
// build, and the site's extension allowlist exists so a build served back from
// its own origin cannot be an .html.
//
//     node html5/tools/upload.js <file> [--app-slug <slug>] [--title <name>]
//                                [--version <v>] [--platform windows|macos|linux|android|other]
//                                [--channel alpha|beta|demo] [--notes <text>]
//                                [--requirements <text>] [--hidden] [--no-replace]
//                                [--url <u>] [--token-env DG_BUILD_TOKEN]
//                                [--config <BuildSettings.json>] [--dry-run]
//
// The token comes from $DG_BUILD_TOKEN, or the first line of
// ~/.sexybiscuit/dg-token — never from a file that might be committed.
//
// Prints one line: `publish <platform> <status> <url>`. Exit 1 on failure, 2 on
// a configuration problem.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { readGitSha } from './export.js';

/** The publish endpoint used when nothing overrides it. */
export const ENDPOINT = 'https://darksgames.app/api/v1/builds/publish';

/** The metadata the endpoint accepts, in the order it documents them. */
export const META_FIELDS = ['appSlug', 'title', 'version', 'fileName', 'channel', 'platform', 'notes', 'requirements', 'replace', 'publish'];

/** The five platform names the API accepts. */
export const PLATFORMS = ['windows', 'macos', 'linux', 'android', 'other'];

/** The three channels the API accepts. */
export const CHANNELS = ['alpha', 'beta', 'demo'];

/** Archives only: a build is served back from the site's own origin. */
export const ALLOWED_EXTENSIONS = ['zip', '7z', 'rar', 'tar', 'gz', 'tgz', 'bz2', 'xz', 'exe', 'msi', 'dmg', 'pkg', 'apk', 'aab', 'appimage', 'deb', 'jar', 'love', 'bin'];

export const MAX_FILE_BYTES = 1073741824;
export const MAX_META_BYTES = 6144;

/** Thrown when the request could never work, as opposed to being rejected. */
export class UploadConfigError extends Error {}

/**
 * The `upload` section of a BuildSettings.json, or {} when there is none.
 * @returns {{ url?: string, tokenVariable?: string, tokenFile?: string, appSlug?: string,
 *            channel?: string, notes?: string, requirements?: string, replace?: boolean, publish?: boolean }}
 */
export function readUploadConfig(file) {
    if (!file || !fs.existsSync(file)) return {};
    const settings = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
    return settings.upload ?? settings.Upload ?? {};
}

/** The API's platform name for a runtime identifier or platform word, or null when it is not publishable. */
export function platformFor(name) {
    const value = String(name ?? '').toLowerCase().replace(/_/g, '-');
    if (PLATFORMS.includes(value)) return value;
    if (value.startsWith('win')) return 'windows';
    if (value.startsWith('osx') || value.startsWith('macos')) return 'macos';
    if (value.startsWith('linux')) return 'linux';
    if (value === 'android') return 'android';
    if (value === 'ios') return 'other';
    return null;                      // web, and anything unrecognised
}

/** The channel to send. Older `dev` spellings are mapped rather than rejected. */
export function channelFor(name) {
    const value = String(name ?? '').toLowerCase().trim();
    if (CHANNELS.includes(value)) return value;
    if (['playtest', 'rc', 'candidate'].includes(value)) return 'beta';
    if (['release', 'public', 'stable'].includes(value)) return 'demo';
    return 'alpha';
}

/** The file name the API will accept, sanitised the way it sanitises server-side. */
export function sanitiseFileName(name) {
    const clean = path.basename(name).replace(/[^A-Za-z0-9._-]/g, '-').replace(/^-+|-+$/g, '');
    return (clean.length > 120 ? clean.slice(-120) : clean) || 'build.zip';
}

/**
 * base64url of the compact JSON, unpadded, trimming `notes` until the header
 * fits — a long release note is the only field that can overflow it.
 */
export function encodeMeta(meta) {
    const encode = () => Buffer.from(JSON.stringify(meta)).toString('base64url');
    let header = encode();
    for (const field of ['notes', 'requirements']) {
        while (header.length > MAX_META_BYTES && typeof meta[field] === 'string' && meta[field].length > 0) {
            // Trim in proportion to the overflow: a character is one byte or four, so
            // subtracting the overflow directly would empty a multi-byte note in one step.
            const keep = Math.min(meta[field].length - 1, Math.max(0, Math.floor(meta[field].length * (MAX_META_BYTES / header.length)) - 8));
            meta[field] = keep > 0 ? `${meta[field].slice(0, keep).trimEnd()}…` : '';
            header = encode();
        }
    }
    return header;
}

/** The token, from the environment or the file the user owns. Never returned to a log. */
export function findToken({ tokenVariable = 'DG_BUILD_TOKEN', tokenFile, env = process.env } = {}) {
    for (const name of [tokenVariable, 'DG_BUILD_TOKEN', 'SB_UPLOAD_TOKEN']) {
        if (name && env[name]) return env[name].trim();
    }
    const file = tokenFile || path.join(os.homedir(), '.sexybiscuit', 'dg-token');
    if (fs.existsSync(file)) {
        const first = fs.readFileSync(file, 'utf8').split('\n')[0].trim();
        if (first) return first;
    }
    return null;
}

const cap = (value, max) => (String(value ?? '').length > max ? String(value).slice(0, max) : String(value ?? ''));

/**
 * Publishes one archive.
 * @returns {Promise<{ ok: boolean, status: number, url: string|null, id: string|null,
 *                     platform: string, checksum?: string, replaced?: boolean, error?: string }>}
 */
export async function publish({
    file, url, token, tokenVariable = 'DG_BUILD_TOKEN', tokenFile,
    appSlug, title, version, platform, channel = 'alpha', notes = '', requirements = '',
    replace = true, visible = true, configuration = 'Release', gitSha, rid,
    retries = 1, fetchImpl = fetch, env = process.env,
}) {
    url ??= env.DG_BUILD_URL || env.SB_UPLOAD_URL || ENDPOINT;
    token ??= findToken({ tokenVariable, tokenFile, env });
    if (!token) throw new UploadConfigError(`no publish token: set ${tokenVariable}, or put it on the first line of ${tokenFile || path.join(os.homedir(), '.sexybiscuit', 'dg-token')}`);
    if (!file || !fs.existsSync(file)) throw new UploadConfigError(`${file} does not exist`);

    const size = fs.statSync(file).size;
    if (!size) throw new UploadConfigError(`${path.basename(file)} is empty`);
    if (size > MAX_FILE_BYTES) throw new UploadConfigError(`${path.basename(file)} is ${Math.round(size / 1048576)} MB; the limit is 1024 MB`);

    const extension = path.extname(file).replace('.', '').toLowerCase();
    if (!ALLOWED_EXTENSIONS.includes(extension)) throw new UploadConfigError(`'.${extension}' is not an accepted extension; wrap the build in a .zip`);

    const target = platformFor(platform ?? rid ?? guessPlatform(file));
    if (!target) throw new UploadConfigError(`${platform ?? rid ?? path.basename(file)} is not a downloadable build, so it is not published`);

    const slug = String(appSlug ?? '').toLowerCase().trim();
    if (!/^[a-z0-9-]+$/.test(slug)) throw new UploadConfigError(`app slug '${appSlug}' must be lowercase letters, numbers and dashes`);

    const built = new Date().toISOString().replace('T', ' ').slice(0, 16);
    const sha = gitSha ?? readGitSha(path.dirname(file));
    const line = `Build: ${rid ?? target} · ${configuration}${sha ? ` · ${sha.slice(0, 7)}` : ''} · ${built}Z`;
    const body = String(notes ?? '').trim();

    const meta = {
        appSlug: cap(slug, 64),
        title: cap(title ?? slug, 80),
        version: cap(version ?? '0.0.0', 40),
        fileName: sanitiseFileName(file),
        channel: channelFor(channel),
        platform: target,
        notes: cap(body ? `${body}\n\n${line}` : line, 4000),
        requirements: cap(requirements, 200),
        replace: replace !== false,
        publish: visible !== false,
    };
    const header = encodeMeta(meta);

    const localSha = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
    let lastError = null;
    let lastStatus = 0;

    // The API's own guidance: retry a broken stream and a rate limit, nothing else.
    for (let attempt = 0; attempt <= retries; attempt++) {
        try {
            const response = await fetchImpl(url, {
                method: 'POST',
                headers: {
                    Authorization: `Bearer ${token}`,
                    'X-Build-Meta': header,
                    'Content-Type': 'application/octet-stream',
                    'Content-Length': String(size),
                },
                body: fs.createReadStream(file),
                duplex: 'half',
            });

            lastStatus = response.status;
            const json = await response.json().catch(() => null);

            if (!response.ok) {
                const code = json?.error ?? null;
                lastError = code && json?.message ? `${code}: ${json.message}` : (json?.message ?? `HTTP ${response.status}`);
                const retryable = response.status === 429 || (response.status === 400 && code === 'upload_failed');
                if (retryable && attempt < retries) {
                    const after = Number(response.headers.get('retry-after'));
                    const seconds = Number.isFinite(after) && after > 0 ? Math.min(120, after) : (response.status === 429 ? 30 : 2);
                    await new Promise((r) => setTimeout(r, seconds * 1000));
                    continue;
                }
                return { ok: false, status: response.status, url: null, id: null, platform: target, error: lastError };
            }

            if (json?.checksum && json.checksum.toLowerCase() !== localSha) {
                lastError = 'checksum mismatch: the upload was corrupted in transit';
                if (attempt < retries) continue;
                return { ok: false, status: response.status, url: null, id: null, platform: target, error: lastError };
            }

            return {
                ok: true, status: response.status, url: json?.url ?? null, id: json?.id ?? null,
                platform: target, checksum: json?.checksum, replaced: json?.replaced === true,
            };
        } catch (err) {
            lastError = err.message;
            if (attempt >= retries) break;
        }
    }

    return { ok: false, status: lastStatus, url: null, id: null, platform: target, error: lastError ?? 'publish failed' };
}

/** The target an archive was built for, read back out of the name the packager gave it. */
export function guessPlatform(file) {
    const name = path.basename(file).toLowerCase();
    for (const rid of ['win-x64', 'win-x86', 'osx-arm64', 'osx-x64', 'linux-x64', 'android', 'ios', 'web']) {
        if (name.includes(rid)) return rid;
    }
    return 'unknown';
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    const take = (flag) => { const i = args.indexOf(flag); return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined; };
    const valueFlags = new Set(['--url', '--token-env', '--token-file', '--app-slug', '--title', '--version', '--platform', '--rid', '--channel', '--notes', '--requirements', '--configuration', '--config']);
    const file = args.find((a, i) => !a.startsWith('--') && !valueFlags.has(args[i - 1]));

    if (!file) {
        console.error('usage: node html5/tools/upload.js <file> [--app-slug <slug>] [--title <name>] [--version <v>] [--channel alpha|beta|demo] [--hidden] [--dry-run]');
        process.exit(2);
    }

    const config = readUploadConfig(take('--config') ?? path.join(process.cwd(), 'BuildSettings.json'));
    const options = {
        file,
        url: take('--url') ?? config.url,
        tokenVariable: take('--token-env') ?? config.tokenVariable ?? 'DG_BUILD_TOKEN',
        tokenFile: take('--token-file') ?? config.tokenFile,
        appSlug: take('--app-slug') ?? config.appSlug,
        title: take('--title'),
        version: take('--version'),
        platform: take('--platform'),
        rid: take('--rid'),
        channel: take('--channel') ?? config.channel ?? 'alpha',
        notes: take('--notes') ?? config.notes ?? '',
        requirements: take('--requirements') ?? config.requirements ?? '',
        configuration: take('--configuration') ?? 'Release',
        replace: !args.includes('--no-replace'),
        visible: !args.includes('--hidden'),
    };

    if (args.includes('--dry-run')) {
        const { tokenVariable, tokenFile, ...shown } = options;
        console.log(`would publish ${file} to ${options.url ?? ENDPOINT} as ${JSON.stringify(shown)}`);
        process.exit(0);
    }

    publish(options).then((result) => {
        console.log(`publish ${result.platform}  ${result.status || 'ERR'}  ${result.url ?? result.error ?? ''}`);
        process.exit(result.ok ? 0 : 1);
    }).catch((err) => {
        console.error(err instanceof UploadConfigError ? err.message : `publish failed: ${err.message}`);
        process.exit(err instanceof UploadConfigError ? 2 : 1);
    });
}
