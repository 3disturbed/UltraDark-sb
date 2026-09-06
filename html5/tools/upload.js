#!/usr/bin/env node
// -----------------------------------------------------------------------------
// upload — sends a build to a site that accepts HTTP uploads with a token.
//
// One multipart POST per artifact: the file plus the metadata a download page
// needs (game, version, platform, configuration, commit, channel, checksum).
// The endpoint and the token come from the environment, never from a file that
// might be committed, and the field names are configurable so the request can
// be shaped to whatever the site expects. The C# pipeline's HttpUploadTarget
// sends the same request, so a build uploaded from either side looks the same.
//
//     node html5/tools/upload.js <file> [--url <u>] [--token-env SB_UPLOAD_TOKEN]
//                                [--game <name>] [--version <v>] [--platform web] [--rid <rid>]
//                                [--configuration Release] [--channel dev] [--config <BuildSettings.json>]
//                                [--field contractName=apiName ...] [--dry-run]
//
// Prints one line: `upload <platform> <status> <url>`. Exit 1 on failure, 2 on
// a configuration problem.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';

import { readGitSha } from './export.js';

/** The metadata fields every upload carries, in the contract's names. */
export const FIELDS = ['game', 'version', 'platform', 'rid', 'configuration', 'gitSha', 'channel', 'builtUtc', 'sha256'];

/**
 * Reads the `upload` section of a BuildSettings.json, when there is one.
 * @returns {{ url?: string, tokenVariable?: string, channel?: string, fields?: Record<string, string> }}
 */
export function readUploadConfig(file) {
    if (!file || !fs.existsSync(file)) return {};
    const settings = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
    return settings.upload ?? settings.Upload ?? {};
}

/**
 * Uploads one artifact.
 *
 * @param {object} options
 * @param {string} options.file
 * @param {string} [options.url] Default: the SB_UPLOAD_URL environment variable.
 * @param {string} [options.token] Default: the variable named by tokenVariable.
 * @param {string} [options.tokenVariable="SB_UPLOAD_TOKEN"]
 * @param {Record<string, string>} [options.metadata] Overrides for the FIELDS.
 * @param {Record<string, string>} [options.fields] Contract field name to API field name.
 * @param {string} [options.fileField="file"]
 * @param {number} [options.retries=1] Extra attempts on a 5xx or a network error.
 * @param {typeof fetch} [options.fetchImpl]
 * @returns {Promise<{ ok: boolean, status: number, url: string|null, platform: string, error?: string }>}
 */
export async function upload({
    file, url, token, tokenVariable = 'SB_UPLOAD_TOKEN', metadata = {}, fields = {},
    fileField = 'file', retries = 1, fetchImpl = fetch, env = process.env,
}) {
    url = url ?? env.SB_UPLOAD_URL;
    token = token ?? env[tokenVariable];
    if (!url) throw new UploadConfigError('no upload URL: pass --url or set SB_UPLOAD_URL');
    if (!token) throw new UploadConfigError(`no upload token: set ${tokenVariable}`);
    if (!fs.existsSync(file)) throw new UploadConfigError(`${file} does not exist`);

    const data = fs.readFileSync(file);
    const values = {
        game: metadata.game ?? path.basename(file).replace(/-.*$/, ''),
        version: metadata.version ?? '0.0.0',
        platform: metadata.platform ?? guessPlatform(file),
        rid: metadata.rid ?? '',
        configuration: metadata.configuration ?? 'Release',
        gitSha: metadata.gitSha ?? readGitSha(path.dirname(file)) ?? '',
        channel: metadata.channel ?? 'dev',
        builtUtc: metadata.builtUtc ?? new Date().toISOString(),
        sha256: crypto.createHash('sha256').update(data).digest('hex'),
    };

    let lastError = null;
    for (let attempt = 0; attempt <= retries; attempt++) {
        const form = new FormData();
        for (const name of FIELDS) form.append(fields[name] ?? name, values[name]);
        form.append(fileField, new Blob([data]), path.basename(file));

        try {
            const response = await fetchImpl(url, {
                method: 'POST',
                headers: { Authorization: `Bearer ${token}` },
                body: form,
            });

            if (response.status >= 500 && attempt < retries) {
                lastError = `HTTP ${response.status}`;
                continue;
            }

            let location = response.headers.get('location');
            if (!location) {
                try {
                    const body = await response.json();
                    location = body?.url ?? body?.downloadUrl ?? body?.location ?? null;
                } catch { /* not JSON */ }
            }

            return {
                ok: response.ok,
                status: response.status,
                url: location ?? (response.ok ? url : null),
                platform: values.platform,
                error: response.ok ? undefined : `HTTP ${response.status}`,
            };
        } catch (err) {
            lastError = err.message;
            if (attempt >= retries) break;
        }
    }

    return { ok: false, status: 0, url: null, platform: values.platform, error: lastError ?? 'upload failed' };
}

export class UploadConfigError extends Error {}

function guessPlatform(file) {
    const name = path.basename(file).toLowerCase();
    for (const platform of ['web', 'win-x64', 'win-x86', 'osx-arm64', 'osx-x64', 'linux-x64', 'android', 'ios']) {
        if (name.includes(platform)) return platform;
    }
    return 'unknown';
}

// -----------------------------------------------------------------------------
// CLI
// -----------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    const take = (flag) => { const i = args.indexOf(flag); return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined; };
    const valueFlags = new Set(['--url', '--token-env', '--game', '--version', '--platform', '--rid', '--configuration', '--channel', '--config', '--field']);
    const file = args.find((a, i) => !a.startsWith('--') && !valueFlags.has(args[i - 1]));

    if (!file) {
        console.error('usage: node html5/tools/upload.js <file> [--url <u>] [--token-env <VAR>] [--game <name>] [--version <v>] [--platform <p>] [--config <BuildSettings.json>] [--field a=b] [--dry-run]');
        process.exit(2);
    }

    const config = readUploadConfig(take('--config') ?? path.join(process.cwd(), 'BuildSettings.json'));
    const fields = { ...(config.fields ?? {}) };
    args.forEach((a, i) => { if (a === '--field' && args[i + 1]?.includes('=')) { const [k, v] = args[i + 1].split('='); fields[k] = v; } });

    const options = {
        file,
        url: take('--url') ?? config.url,
        tokenVariable: take('--token-env') ?? config.tokenVariable ?? 'SB_UPLOAD_TOKEN',
        fields,
        metadata: {
            game: take('--game'), version: take('--version'), platform: take('--platform'), rid: take('--rid'),
            configuration: take('--configuration'), channel: take('--channel') ?? config.channel,
        },
    };
    for (const key of Object.keys(options.metadata)) if (options.metadata[key] === undefined) delete options.metadata[key];

    if (args.includes('--dry-run')) {
        console.log(`would upload ${file} to ${options.url ?? '(no url)'} as ${JSON.stringify(options.metadata)} with fields ${JSON.stringify(fields)}`);
        process.exit(0);
    }

    upload(options).then((result) => {
        console.log(`upload ${result.platform}  ${result.status || 'ERR'}  ${result.url ?? result.error ?? ''}`);
        process.exit(result.ok ? 0 : 1);
    }).catch((err) => {
        console.error(err instanceof UploadConfigError ? err.message : `upload failed: ${err.message}`);
        process.exit(err instanceof UploadConfigError ? 2 : 1);
    });
}
