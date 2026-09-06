// -----------------------------------------------------------------------------
// zip — writes and lists .zip archives with nothing but node's zlib.
//
// A build is one folder; a download is one file. Node has deflate but no
// archive format, and pulling a dependency into a zero-dependency toolchain
// for forty lines of headers is the wrong trade.
// -----------------------------------------------------------------------------

import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

/**
 * Zips every file under `dir` into `zipPath`, paths relative to `dir` with
 * forward slashes. Returns the number of entries written.
 */
export function zipDirectory(dir, zipPath) {
    const files = walk(dir).sort();
    const parts = [];
    const central = [];
    let offset = 0;

    for (const file of files) {
        const name = Buffer.from(path.relative(dir, file).split(path.sep).join('/'), 'utf8');
        const data = fs.readFileSync(file);
        const stat = fs.statSync(file);
        const compressed = zlib.deflateRawSync(data);
        const crc = zlib.crc32(data) >>> 0;
        const { time, date } = dosDateTime(stat.mtime);

        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);            // version needed
        local.writeUInt16LE(0x0800, 6);        // flags: UTF-8 names
        local.writeUInt16LE(8, 8);             // method: deflate
        local.writeUInt16LE(time, 10);
        local.writeUInt16LE(date, 12);
        local.writeUInt32LE(crc, 14);
        local.writeUInt32LE(compressed.length, 18);
        local.writeUInt32LE(data.length, 22);
        local.writeUInt16LE(name.length, 26);
        local.writeUInt16LE(0, 28);            // extra length

        const entry = Buffer.alloc(46);
        entry.writeUInt32LE(0x02014b50, 0);
        entry.writeUInt16LE(0x031e, 4);        // made by: unix, 3.0 — carries the mode below
        entry.writeUInt16LE(20, 6);
        entry.writeUInt16LE(0x0800, 8);
        entry.writeUInt16LE(8, 10);
        entry.writeUInt16LE(time, 12);
        entry.writeUInt16LE(date, 14);
        entry.writeUInt32LE(crc, 16);
        entry.writeUInt32LE(compressed.length, 20);
        entry.writeUInt32LE(data.length, 24);
        entry.writeUInt16LE(name.length, 28);
        entry.writeUInt16LE(0, 30);            // extra
        entry.writeUInt16LE(0, 32);            // comment
        entry.writeUInt16LE(0, 34);            // disk
        entry.writeUInt16LE(0, 36);            // internal attributes
        entry.writeUInt32LE(((stat.mode & 0o7777) | 0o100000) << 16 >>> 0, 38);   // unix mode, keeps +x
        entry.writeUInt32LE(offset, 42);

        parts.push(local, name, compressed);
        central.push(entry, name);
        offset += local.length + name.length + compressed.length;
    }

    const centralStart = offset;
    const centralBytes = central.reduce((n, b) => n + b.length, 0);

    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(0, 4);
    end.writeUInt16LE(0, 6);
    end.writeUInt16LE(files.length, 8);
    end.writeUInt16LE(files.length, 10);
    end.writeUInt32LE(centralBytes, 12);
    end.writeUInt32LE(centralStart, 16);
    end.writeUInt16LE(0, 20);

    fs.mkdirSync(path.dirname(zipPath), { recursive: true });
    fs.writeFileSync(zipPath, Buffer.concat([...parts, ...central, end]));
    return files.length;
}

/** The entry names of an archive, read from its central directory. */
export function listZip(zipPath) {
    const buffer = fs.readFileSync(zipPath);
    let end = buffer.length - 22;
    while (end >= 0 && buffer.readUInt32LE(end) !== 0x06054b50) end--;
    if (end < 0) throw new Error(`${zipPath} is not a zip file`);

    const count = buffer.readUInt16LE(end + 10);
    let cursor = buffer.readUInt32LE(end + 16);
    const names = [];
    for (let i = 0; i < count; i++) {
        if (buffer.readUInt32LE(cursor) !== 0x02014b50) throw new Error(`${zipPath} has a damaged central directory`);
        const nameLength = buffer.readUInt16LE(cursor + 28);
        const extraLength = buffer.readUInt16LE(cursor + 30);
        const commentLength = buffer.readUInt16LE(cursor + 32);
        names.push(buffer.toString('utf8', cursor + 46, cursor + 46 + nameLength));
        cursor += 46 + nameLength + extraLength + commentLength;
    }
    return names;
}

function walk(root) {
    const found = [];
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
        const full = path.join(root, entry.name);
        if (entry.isDirectory()) found.push(...walk(full));
        else if (entry.isFile()) found.push(full);
    }
    return found;
}

function dosDateTime(date) {
    const d = date instanceof Date && !Number.isNaN(date.getTime()) ? date : new Date(1980, 0, 1);
    const year = Math.max(1980, d.getFullYear());
    return {
        time: (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1),
        date: ((year - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate(),
    };
}
