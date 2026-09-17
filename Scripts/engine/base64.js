// -----------------------------------------------------------------------------
// base64 — bytes as text, in plain ECMAScript.
//
// A script message carries JSON, and UltraDark's wire is binary: every frame
// protocol.js writes crosses the engine's session as a base64 string. Neither
// engine gives a script btoa or atob (Jint has no Web APIs at all), so this is the
// encoding written out, integer operations only, which makes the same bytes the
// same text on both engines. The alphabet and the padding are RFC 4648's, the
// ones btoa writes.
// -----------------------------------------------------------------------------

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const PAD = 61;             // "="
const CHUNK_GROUPS = 2048;  // groups of three bytes turned into text at a time

const CODES = [];
const VALUES = [];
for (let i = 0; i < 128; i++) VALUES.push(-1);
for (let i = 0; i < ALPHABET.length; i++) {
  CODES.push(ALPHABET.charCodeAt(i));
  VALUES[ALPHABET.charCodeAt(i)] = i;
}

/** The base64 text of a Uint8Array or an ArrayBuffer. */
export function encodeBase64(data) {
  const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
  const length = bytes.length;
  const parts = [];
  for (let start = 0; start < length; start += CHUNK_GROUPS * 3) {
    const end = Math.min(length, start + CHUNK_GROUPS * 3);
    const codes = [];
    let i = start;
    for (; i + 2 < end; i += 3) {
      const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
      codes.push(CODES[n >> 18], CODES[(n >> 12) & 63], CODES[(n >> 6) & 63], CODES[n & 63]);
    }
    if (end - i === 1) {
      const n = bytes[i] << 16;
      codes.push(CODES[n >> 18], CODES[(n >> 12) & 63], PAD, PAD);
    } else if (end - i === 2) {
      const n = (bytes[i] << 16) | (bytes[i + 1] << 8);
      codes.push(CODES[n >> 18], CODES[(n >> 12) & 63], CODES[(n >> 6) & 63], PAD);
    }
    parts.push(String.fromCharCode.apply(null, codes));
  }
  return parts.join("");
}

/**
 * The bytes of base64 text, or null when the text is not base64 as encodeBase64 writes it: a length
 * that is a multiple of four, only the alphabet, and at most two "=" at the very end. A frame off the
 * wire is whatever the sender wanted it to be, so nothing here throws.
 */
export function decodeBase64(text) {
  if (typeof text !== "string" || text.length % 4 !== 0) return null;
  let padding = 0;
  if (text.length > 0 && text.charCodeAt(text.length - 1) === PAD) padding++;
  if (text.length > 1 && text.charCodeAt(text.length - 2) === PAD) padding++;
  const bytes = new Uint8Array((text.length / 4) * 3 - padding);
  let out = 0;
  for (let i = 0; i < text.length; i += 4) {
    const last = i + 4 === text.length;
    let n = 0;
    for (let k = 0; k < 4; k++) {
      const code = text.charCodeAt(i + k);
      let value;
      if (code === PAD && last && k >= 4 - padding) value = 0;
      else value = code < 128 ? VALUES[code] : -1;
      if (value < 0) return null;
      n = (n << 6) | value;
    }
    bytes[out++] = (n >> 16) & 255;
    if (out < bytes.length) bytes[out++] = (n >> 8) & 255;
    if (out < bytes.length) bytes[out++] = n & 255;
  }
  return bytes;
}
