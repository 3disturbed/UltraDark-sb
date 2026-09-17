// -----------------------------------------------------------------------------
// glyphs — what the engine's faces can draw, and a stand-in for what they cannot.
//
// UltraDark drew its words with the browser's system fonts, and the browser fell
// back to an emoji font for 💣 and a symbol font for ⬡ without being asked. The
// engine draws text with its own baked faces (html5/src/ui/fonts), which fall back
// to nothing:
//
//   - the five text faces -- body, body-strong, heading, display, display-alt --
//     cover Latin text and punctuation, and no symbols at all;
//   - the two mono faces -- mono and mono-bold, DejaVu Sans Mono -- cover those and
//     the symbols the game uses as well: arrows, shapes, stars, dingbats. They have
//     no emoji, and three of the game's own symbols are missing from them too.
//
// So this module does two things, and nothing else:
//
//   substitute(text)   the text with a stand-in for each character no face draws
//   runs(text, face)   the text cut into runs a face draws, and the rest in mono
//
// Nothing here reaches the wire or the simulation. A pilot's symbol, a consumable's
// glyph and every string stay the original's; only what ends up on screen changes,
// in one place. Each stand-in is a single-colour symbol mono has, chosen to read as
// what it stands for and to collide with no symbol the game already shows: pilots
// ✦ ▲ ◯ ◉ ⌁ ⚒ ❄, consumables ✚ ◈ ⚡ ● ❄, and ★ ☠ ♥ ◇ ⚔ ⚙ ⚠ ✓ ✔ ⬇ ← → ↻ ▶ ▸ ◆ in
// the client. html5/tests/darkShapesClientGlyphs.test.js holds the coverage tables
// to the font files, and walks every character the game can show through them.
// -----------------------------------------------------------------------------

/** Each character no shipped face draws, and what is drawn in its place. */
export const STAND_INS = new Map([
    ["⌖", "⊕"],        // ⌖ HAWK's reticle → ⊕ circled plus, as UltraDarkNative drew it
    ["⬡", "◊"],        // ⬡ a core, the shop's currency → ◊ lozenge
    ["Ⓐ", "(A)"],           // Ⓐ a pad's A button → (A)
    ["\u{1F4A3}", "✹"],     // 💣 a bomb → ✹ twelve-pointed star, a blast
    ["\u{1F311}", "◐"],     // 🌑 the Daily Dark's new moon → ◐ half-dark circle
    ["\u{1F3C6}", "✪"],     // 🏆 leaderboards → ✪ circled star (★ is a rank)
    ["\u{1F3E6}", "⌂"],     // 🏦 bank → ⌂ house
    ["\u{1F517}", "⌘"],     // 🔗 an invite link → ⌘ looped square
    ["\u{1F525}", "✺"],     // 🔥 a kill streak → ✺ sixteen-pointed burst
    ["\u{1F3AE}", "✜"],     // 🎮 a controller → ✜ open-centre cross, a d-pad
    ["\u{1F916}", "▣"],     // 🤖 the bots theme → ▣ square in a square, a panel
    ["\u{1F9DF}", "✝"],     // 🧟 the zombies theme → ✝ cross, a grave
]);

/** The characters the text faces draw, as inclusive code point ranges. */
export const TEXT_COVERAGE = Object.freeze([
    [32, 126], [160, 255], [8211, 8212], [8216, 8218], [8220, 8222], [8226, 8226], [8230, 8230],
    [8242, 8243], [8249, 8250], [8364, 8364], [8482, 8482], [8722, 8722], [65533, 65533],
]);

/** The characters the mono faces draw, as inclusive code point ranges. */
export const MONO_COVERAGE = Object.freeze([
    [32, 126], [160, 255], [8211, 8212], [8216, 8218], [8220, 8222], [8226, 8226], [8230, 8230], [8242, 8243],
    [8249, 8250], [8364, 8364], [8482, 8482], [8592, 8592], [8594, 8594], [8635, 8635], [8722, 8722], [8853, 8853],
    [8960, 8966], [8968, 8981], [8984, 8985], [8988, 8993], [9632, 9727], [9733, 9733], [9760, 9760], [9829, 9829],
    [9873, 9884], [9888, 9889], [10002, 10023], [10025, 10059], [11015, 11015], [65533, 65533],
]);

/** The shipped faces, and which table each draws from. */
export const FACES = Object.freeze({
    body: TEXT_COVERAGE,
    "body-strong": TEXT_COVERAGE,
    heading: TEXT_COVERAGE,
    display: TEXT_COVERAGE,
    "display-alt": TEXT_COVERAGE,
    mono: MONO_COVERAGE,
    "mono-bold": MONO_COVERAGE,
});

/** Whether a face draws a code point. A face this module does not know draws nothing. */
export function covers(face, codePoint) {
    const ranges = Object.prototype.hasOwnProperty.call(FACES, face) ? FACES[face] : null;
    if (ranges === null) return false;
    let lo = 0;
    let hi = ranges.length - 1;
    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        const [from, to] = ranges[mid];
        if (codePoint < from) hi = mid - 1;
        else if (codePoint > to) lo = mid + 1;
        else return true;
    }
    return false;
}

/** The text with a stand-in for every character no face draws; the same string when there is none. */
export function substitute(text) {
    const source = String(text ?? "");
    let out = "";
    let changed = false;
    for (const ch of source) {
        const stand = STAND_INS.get(ch);
        if (stand === undefined) {
            out += ch;
        } else {
            out += stand;
            changed = true;
        }
    }
    return changed ? out : source;
}

/** The mono face a text face falls back to: bold behind the strong and display faces. */
export function monoFor(face) {
    return face === "body" || face === "mono" ? "mono" : "mono-bold";
}

/**
 * The substituted text as runs `{ text, face }`, in order: each run in `face` where `face` draws
 * it, and in `monoFor(face)` where it does not. Adjacent characters with the same face share a
 * run, so plain text is one run. A character neither draws -- there is none in the game once
 * substituted -- stays in `face`, which draws its missing-glyph mark.
 */
export function runs(text, face) {
    const fallback = monoFor(face);
    const out = [];
    for (const ch of substitute(text)) {
        const cp = ch.codePointAt(0);
        const runFace = covers(face, cp) || !covers(fallback, cp) ? face : fallback;
        const last = out.length > 0 ? out[out.length - 1] : null;
        if (last !== null && last.face === runFace) last.text += ch;
        else out.push({ text: ch, face: runFace });
    }
    return out;
}
