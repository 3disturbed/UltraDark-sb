// -----------------------------------------------------------------------------
// png — writes a solid-colour PNG, for a placeholder app icon.
//
// A PWA needs 192 and 512 pixel icons to be installable, and a prototype does
// not have artwork yet. A flat square in the theme colour is enough for the
// home screen until someone draws one.
// -----------------------------------------------------------------------------

import zlib from 'node:zlib';

/**
 * @param {number} size Width and height in pixels.
 * @param {[number, number, number, number]} rgba 0-255 each.
 * @returns {Buffer}
 */
export function solidPng(size, rgba) {
    const [r, g, b, a] = rgba;

    // One filter byte (0 = none) then RGBA pixels, per row.
    const row = Buffer.alloc(1 + size * 4);
    for (let x = 0; x < size; x++) row.set([r, g, b, a], 1 + x * 4);
    const raw = Buffer.concat(Array.from({ length: size }, () => row));

    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(size, 0);
    ihdr.writeUInt32BE(size, 4);
    ihdr[8] = 8;    // bit depth
    ihdr[9] = 6;    // colour type: RGBA
    ihdr[10] = 0;   // compression
    ihdr[11] = 0;   // filter
    ihdr[12] = 0;   // interlace

    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', ihdr),
        chunk('IDAT', zlib.deflateSync(raw)),
        chunk('IEND', Buffer.alloc(0)),
    ]);
}

function chunk(type, data) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length, 0);
    const typed = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(zlib.crc32(typed) >>> 0, 0);
    return Buffer.concat([length, typed, crc]);
}

/** Parses "#RRGGBB" or "#RRGGBBAA" into [r, g, b, a]. */
export function parseHexColour(text, fallback = [0x12, 0x14, 0x1a, 0xff]) {
    const match = /^#?([0-9a-f]{6})([0-9a-f]{2})?$/i.exec(text ?? '');
    if (!match) return fallback;
    const n = parseInt(match[1], 16);
    return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff, match[2] ? parseInt(match[2], 16) : 0xff];
}
