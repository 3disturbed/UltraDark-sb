// -----------------------------------------------------------------------------
// BitmapFont — text without an asset pipeline.
//
// The C# UI has always been able to draw text and has never had a font to draw
// it with: `Label.Draw` begins `if (font == null) return;` and there is no font
// asset anywhere in the repository, so every label rendered as nothing, quietly.
// Shipping a script-facing UI on top of that would have shipped the same defect
// one layer up.
//
// So the font is data, not an asset: `font5x7.json` holds 95 glyphs as rows of
// '#' and '.', this module draws them as quads, and the C# engine embeds the
// very same file. Neither engine can render text the other cannot.
// -----------------------------------------------------------------------------

import font from './font5x7.json' with { type: 'json' };

/** Space between glyphs, in unscaled pixels. */
export const LETTER_SPACING = 1;

/** Space between lines, in unscaled pixels. */
export const LINE_SPACING = 2;

export const GLYPH_WIDTH = font.width;
export const GLYPH_HEIGHT = font.height;

/** Every printable glyph, as a 35-character mask. Unknown characters draw as a space. */
const glyphs = font.glyphs;

/** The width one line of text occupies at a given scale. */
export function measure(text, scale = 1) {
    const line = String(text ?? '');
    if (line.length === 0) return 0;
    return (line.length * (GLYPH_WIDTH + LETTER_SPACING) - LETTER_SPACING) * scale;
}

/** The height one line occupies at a given scale. */
export function lineHeight(scale = 1) {
    return GLYPH_HEIGHT * scale;
}

/** The height a block of text occupies, counting newlines. */
export function measureHeight(text, scale = 1) {
    const lines = String(text ?? '').split('\n').length;
    return (lines * (GLYPH_HEIGHT + LINE_SPACING) - LINE_SPACING) * scale;
}

/**
 * Draws text as filled rectangles, one per lit pixel.
 *
 * A quad per pixel sounds extravagant and is not: a HUD is a few hundred glyphs,
 * the rectangles batch, and it buys identical output on two engines with no font
 * file, no glyph atlas and no measuring differences to reconcile.
 */
export function drawText(ctx, text, x, y, { scale = 1, colour = '#ffffff' } = {}) {
    const lines = String(text ?? '').split('\n');
    ctx.fillStyle = colour;

    for (let row = 0; row < lines.length; row++) {
        const lineY = y + row * (GLYPH_HEIGHT + LINE_SPACING) * scale;
        let penX = x;

        for (const character of lines[row]) {
            const mask = glyphs[character];
            if (mask) {
                for (let gy = 0; gy < GLYPH_HEIGHT; gy++) {
                    for (let gx = 0; gx < GLYPH_WIDTH; gx++) {
                        if (mask[gy * GLYPH_WIDTH + gx] !== '#') continue;
                        ctx.fillRect(penX + gx * scale, lineY + gy * scale, scale, scale);
                    }
                }
            }
            penX += (GLYPH_WIDTH + LETTER_SPACING) * scale;
        }
    }
}
