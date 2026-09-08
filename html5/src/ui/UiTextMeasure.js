// -----------------------------------------------------------------------------
// UiTextMeasure — the one place the layout engine asks how big text is.
//
// A seam, on purpose, and the mirror of SexyBiscuit.Engine/UI/UiTextMeasure.cs.
// Today every answer comes from the shared 5x7 bitmap font, whose glyph table both
// engines read from the same file so the two cannot disagree. When scalable fonts
// land they land here, and the layout engine does not change. A widget that
// measures text by asking a rasteriser instead is a parity bug.
// -----------------------------------------------------------------------------

import { measure, measureHeight } from './BitmapFont.js';

/** The widest line of a block, which is what centring has to measure. */
export function width(text, scale) {
    const s = effectiveScale(scale);
    return String(text ?? '').split('\n').reduce((w, line) => Math.max(w, measure(line, s)), 0);
}

/** The height of a block, counting its line breaks. */
export function height(text, scale, lineSpacing = 0) {
    if (!text) return 0;
    const s = effectiveScale(scale);
    const breaks = String(text).split('\n').length - 1;
    return measureHeight(text, s) + Math.max(0, lineSpacing) * breaks;
}

/** Measures a block, wrapping it to a width first when asked. */
export function measureText(text, scale, wrapping, maxWidth, lineSpacing = 0) {
    if (!text) return { x: 0, y: 0 };

    const body = wrapping && Number.isFinite(maxWidth) && maxWidth > 0
        ? wrap(text, scale, maxWidth)
        : text;

    return { x: width(body, scale), y: height(body, scale, lineSpacing) };
}

/**
 * Greedily breaks a string to a maximum width, at spaces and after hyphens.
 *
 * Deliberately not the Unicode line-breaking algorithm. A word longer than the line
 * is left to overflow rather than cut mid-word, because a truncated word reads as a
 * bug and an overflowing one reads as a layout to fix.
 */
export function wrap(text, scale, maxWidth) {
    if (!text || maxWidth <= 0) return text ?? '';

    return String(text)
        .split('\n')
        .map((paragraph) => wrapParagraph(paragraph, scale, maxWidth))
        .join('\n');
}

function wrapParagraph(paragraph, scale, maxWidth) {
    const out = [];
    let lineStart = 0;
    let lastBreak = -1;

    for (let i = 0; i < paragraph.length; i++) {
        // A hyphen may be broken *after*; a space is consumed by the break.
        if (paragraph[i] === ' ' || paragraph[i] === '-') lastBreak = i;

        if (width(paragraph.slice(lineStart, i + 1), scale) <= maxWidth) continue;
        if (lastBreak <= lineStart) continue;

        out.push(paragraph.slice(lineStart, lastBreak).replace(/\s+$/, ''));
        lineStart = lastBreak + 1;
        lastBreak = -1;
        i = lineStart - 1;
    }

    if (lineStart < paragraph.length) out.push(paragraph.slice(lineStart));
    return out.join('\n');
}

/** The height of one line, which is what an empty text field still reserves. */
export function lineHeightOf(scale) { return measureHeight('A', effectiveScale(scale)); }

/**
 * Bitmap glyphs only look right at whole multiples of their cell, so the scale is
 * rounded and never allowed below one.
 */
function effectiveScale(scale) { return Math.max(1, Math.round(Number(scale) || 1)); }
