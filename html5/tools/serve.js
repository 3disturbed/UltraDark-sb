#!/usr/bin/env node
// -----------------------------------------------------------------------------
// serve — a static file server for local development.
//
// ES modules will not load from `file://`: the browser refuses the import as
// cross-origin. So the editor and the runtime both need a real HTTP origin even
// to open a project sitting on the same disk. This is the smallest thing that
// provides one, with no dependencies.
//
//     node html5/tools/serve.js [port] [root] [--watch]
//
// With `--watch` every page served gets a two-line script that reloads it when
// a scene, script, asset or page under the root changes, so a playtester's tab
// follows an edit without anyone pressing anything.
// -----------------------------------------------------------------------------

import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

const TYPES = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.webmanifest': 'application/manifest+json; charset=utf-8',
    // A .scene file is JSON despite the extension; serving it as JSON means the
    // browser's network panel formats it and fetch().json() works either way.
    '.scene': 'application/json; charset=utf-8',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.gif': 'image/gif',
    '.webp': 'image/webp',
    '.svg': 'image/svg+xml',
    '.ico': 'image/x-icon',
    '.wav': 'audio/wav',
    '.mp3': 'audio/mpeg',
    '.ogg': 'audio/ogg',
    '.gltf': 'model/gltf+json',
    '.glb': 'model/gltf-binary',
    '.ttf': 'font/ttf',
    '.woff2': 'font/woff2',
};

/** File types whose change is worth a reload. Anything else (a log, a build) is not. */
const WATCHED = new Set(['.js', '.mjs', '.scene', '.json', '.html', '.css', '.png', '.jpg', '.jpeg',
    '.webp', '.gif', '.svg', '.wav', '.mp3', '.ogg', '.gltf', '.glb']);

const RELOAD_SNIPPET = '<script>new EventSource("/__reload").onmessage=()=>location.reload();</script>';

/**
 * Starts the server.
 *
 * @param {object} [options]
 * @param {number} [options.port=8080]
 * @param {string} [options.root] Defaults to the repository root.
 * @param {boolean} [options.watch=false]
 * @param {(line: string) => void} [options.log]
 * @returns {Promise<{ server: http.Server, port: number, root: string, close: () => Promise<void> }>}
 */
export function serve({ port = 8080, root, watch = false, log = console.log } = {}) {
    // Serve from the repository root, so the editor can reach Templates/ and any
    // project living beside html5/ rather than only what is inside it.
    root = path.resolve(root ?? path.join(here, '..', '..'));

    const clients = new Set();
    let watcher = null;
    let pending = null;

    if (watch) {
        watcher = fs.watch(root, { recursive: true }, (_, file) => {
            if (!file || !WATCHED.has(path.extname(file).toLowerCase())) return;
            if (file.includes('node_modules') || file.split(path.sep).includes('dist')) return;
            // Editors write in bursts; one reload per burst.
            clearTimeout(pending);
            pending = setTimeout(() => {
                for (const client of clients) client.write(`data: ${JSON.stringify(file)}\n\n`);
            }, 150);
        });
    }

    const server = http.createServer((request, response) => {
        let pathname;
        try {
            pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
        } catch {
            response.writeHead(400).end('Bad request');
            return;
        }

        if (watch && pathname === '/__reload') {
            response.writeHead(200, {
                'content-type': 'text/event-stream',
                'cache-control': 'no-store',
                connection: 'keep-alive',
            });
            response.write(': connected\n\n');
            clients.add(response);
            request.on('close', () => clients.delete(response));
            return;
        }

        // Resolve before comparing: decoding can produce `..` segments that only
        // escape the root once joined.
        const target = path.resolve(path.join(root, pathname));
        if (target !== root && !target.startsWith(root + path.sep)) {
            response.writeHead(403).end('Forbidden');
            return;
        }

        let file = target;
        if (fs.existsSync(file) && fs.statSync(file).isDirectory()) {
            file = path.join(file, 'index.html');
        }

        if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
            response.writeHead(404, { 'content-type': 'text/plain' }).end(`404 - ${pathname}`);
            return;
        }

        const extension = path.extname(file).toLowerCase();
        const headers = {
            'content-type': TYPES[extension] ?? 'application/octet-stream',
            // A dev server that caches is a dev server that lies about your edits.
            'cache-control': 'no-store',
        };

        if (watch && extension === '.html') {
            const html = fs.readFileSync(file, 'utf8');
            const injected = html.includes('</body>')
                ? html.replace('</body>', `${RELOAD_SNIPPET}\n</body>`)
                : html + RELOAD_SNIPPET;
            response.writeHead(200, headers).end(injected);
            return;
        }

        response.writeHead(200, headers);
        fs.createReadStream(file).pipe(response);
    });

    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, () => {
            const actualPort = server.address().port;
            log(`SexyBiscuit HTML5 - serving ${root}${watch ? ' (watching for changes)' : ''}`);
            log(`  editor   http://localhost:${actualPort}/html5/editor/`);
            log(`  runtime  http://localhost:${actualPort}/html5/runtime/`);
            const lan = lanAddress();
            if (lan) log(`  on a phone: http://${lan}:${actualPort}/html5/runtime/?project=/Games/<Name>/`);
            resolve({
                server,
                port: actualPort,
                root,
                close: () => new Promise((done) => {
                    watcher?.close();
                    for (const client of clients) client.end();
                    server.close(() => done());
                }),
            });
        });
    });
}

/** The machine's first non-internal IPv4 address, for the hand-off URL. */
export function lanAddress() {
    for (const addresses of Object.values(os.networkInterfaces())) {
        for (const address of addresses ?? []) {
            if (address.family === 'IPv4' && !address.internal) return address.address;
        }
    }
    return null;
}

// -----------------------------------------------------------------------------
// CLI
// -----------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const args = process.argv.slice(2);
    const watch = args.includes('--watch');
    const positional = args.filter((a) => !a.startsWith('--'));
    const port = Number(positional[0] ?? process.env.PORT ?? 8080);
    const root = positional[1];

    serve({ port, root, watch }).catch((err) => {
        console.error(err.message);
        process.exit(1);
    });
}
