#!/usr/bin/env node
// -----------------------------------------------------------------------------
// serve — a static file server for local development.
//
// ES modules will not load from `file://`: the browser refuses the import as
// cross-origin. So the editor and the runtime both need a real HTTP origin even
// to open a project sitting on the same disk. This is the smallest thing that
// provides one, with no dependencies.
//
//     node html5/tools/serve.js [port] [root]
// -----------------------------------------------------------------------------

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

// Serve from the repository root, so the editor can reach Templates/ and any
// project living beside html5/ rather than only what is inside it.
const root = path.resolve(process.argv[3] ?? path.join(here, '..', '..'));
const port = Number(process.argv[2] ?? process.env.PORT ?? 8080);

const TYPES = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
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

const server = http.createServer((request, response) => {
    let pathname;
    try {
        pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    } catch {
        response.writeHead(400).end('Bad request');
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

    response.writeHead(200, {
        'content-type': TYPES[path.extname(file).toLowerCase()] ?? 'application/octet-stream',
        // A dev server that caches is a dev server that lies about your edits.
        'cache-control': 'no-store',
    });
    fs.createReadStream(file).pipe(response);
});

server.listen(port, () => {
    console.log(`SexyBiscuit HTML5 - serving ${root}`);
    console.log(`  editor   http://localhost:${port}/html5/editor/`);
    console.log(`  runtime  http://localhost:${port}/html5/runtime/`);
    console.log("\nOn a phone, use this machine's LAN address in place of localhost.");
});
