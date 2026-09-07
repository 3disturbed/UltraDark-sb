// -----------------------------------------------------------------------------
// UiCanvas — screen-space UI a game script can actually build.
//
// The shared scripting contract had no UI and, worse, no viewport: a script
// could not ask how wide the window was, so it could not put anything in a
// corner. Every prototype therefore grew a HUD out of world-space sprites
// floating over the player's head, with no text anywhere, because there was no
// other option.
//
// This is that option. It is deliberately small — panel, label, bar, button,
// image — because every member has to exist on the C# side too, and a member
// that exists on one engine only is the defect this repository keeps finding.
// -----------------------------------------------------------------------------

import { drawText, measure, measureHeight, lineHeight } from './BitmapFont.js';

/** Anchor name -> fraction of the viewport. The element aligns to the same corner. */
export const ANCHORS = Object.freeze({
    topleft:     [0,   0  ], top:    [0.5, 0  ], topright:     [1, 0  ],
    left:        [0,   0.5], center: [0.5, 0.5], right:        [1, 0.5],
    bottomleft:  [0,   1  ], bottom: [0.5, 1  ], bottomright:  [1, 1  ],
});

const KINDS = new Set(['panel', 'label', 'bar', 'button', 'image']);

let nextId = 1;

/** One UI element. Plain data: the bridge hands a script a proxy over this. */
class Element {
    constructor(kind, options) {
        this.id = nextId++;
        this.kind = kind;
        this.x = 0;
        this.y = 0;
        this.width = 100;
        this.height = 24;
        this.text = '';
        this.value = 1;                 // bars: 0..1
        this.scale = 1;                 // text size multiplier
        this.visible = true;
        this.anchor = 'topleft';
        this.tint = '#ffffff';          // text, and the filled part of a bar
        this.background = null;         // panel/button/bar backing; null draws nothing
        this.texturePath = '';
        this.align = 'left';            // left | center | right, within the element
        this.padding = 6;

        this.hovered = false;
        this.clicked = false;           // true for the frame after a release inside
        this.destroyed = false;

        Object.assign(this, options ?? {});
    }
}

export class UiCanvas {
    constructor({ width = 1280, height = 720 } = {}) {
        this.width = width;
        this.height = height;
        this.elements = [];

        // Pointer state, fed by the host each frame.
        this._pointer = { x: -1, y: -1, down: false, wasDown: false };
    }

    setViewport(width, height) {
        this.width = Math.max(1, Math.round(width));
        this.height = Math.max(1, Math.round(height));
    }

    /** Adds an element of one of the five kinds. Unknown kinds are refused loudly. */
    add(kind, options) {
        if (!KINDS.has(kind)) throw new Error(`UiCanvas: unknown element kind "${kind}"`);
        const element = new Element(kind, options);
        this.elements.push(element);
        return element;
    }

    remove(element) {
        const at = this.elements.indexOf(element);
        if (at >= 0) this.elements.splice(at, 1);
        if (element) element.destroyed = true;
    }

    /** Everything at once — what a script's `UI.clear()` reaches. */
    clear() {
        for (const element of this.elements) element.destroyed = true;
        this.elements.length = 0;
    }

    /**
     * The element's screen rectangle.
     *
     * The anchor is both where on the screen the element hangs from AND which of
     * its own corners hangs there, so `anchor: "bottomright", x: -12, y: -12`
     * sits twelve pixels in from the bottom-right whatever the window size — the
     * thing a script has never been able to express.
     */
    rectOf(element) {
        const [ax, ay] = ANCHORS[element.anchor] ?? ANCHORS.topleft;

        // A label with no size measures itself. Without this a right-anchored
        // label hangs its LEFT edge on the right border and runs off the screen,
        // and a centred one starts at the centre instead of straddling it — the
        // caller would have to measure the text and set the width by hand, every
        // time the text changed.
        const scale = Math.max(1, Math.round(element.scale));
        const width = element.width > 0 ? element.width
            : (element.kind === 'label' ? widestLine(element.text, scale) : 0);
        const height = element.height > 0 ? element.height
            : (element.kind === 'label' ? measureHeight(element.text, scale) : 0);

        return {
            x: this.width * ax + element.x - width * ax,
            y: this.height * ay + element.y - height * ay,
            width,
            height,
        };
    }

    // -------------------------------------------------------------------------
    // Frame
    // -------------------------------------------------------------------------

    /** Feeds this frame's pointer in. `down` is "button held", not "pressed". */
    setPointer(x, y, down) {
        this._pointer.wasDown = this._pointer.down;
        this._pointer.x = x;
        this._pointer.y = y;
        this._pointer.down = Boolean(down);
    }

    /**
     * Resolves hover and click. A click is a release inside the element that also
     * went down inside it, which is what every other UI toolkit means by one.
     */
    update() {
        const p = this._pointer;
        const released = p.wasDown && !p.down;

        for (const element of this.elements) {
            element.clicked = false;
            if (!element.visible || element.kind !== 'button') { element.hovered = false; continue; }

            const r = this.rectOf(element);
            const inside = p.x >= r.x && p.x <= r.x + r.width && p.y >= r.y && p.y <= r.y + r.height;
            element.hovered = inside;

            if (inside && released) element.clicked = true;
        }

        // Consume the release. Without this a second update() in the same frame —
        // or a frame the host does not feed a pointer into — sees the same
        // release again and fires the button twice.
        p.wasDown = p.down;
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    /** Paints every visible element, in the order they were added. */
    draw(ctx, { images = null } = {}) {
        for (const element of this.elements) {
            if (!element.visible) continue;
            const r = this.rectOf(element);

            switch (element.kind) {
                case 'panel':  this._drawBox(ctx, element, r); break;
                case 'button': this._drawButton(ctx, element, r); break;
                case 'bar':    this._drawBar(ctx, element, r); break;
                case 'label':  this._drawLabel(ctx, element, r); break;
                case 'image':  this._drawImage(ctx, element, r, images); break;
                default: break;
            }
        }
    }

    _drawBox(ctx, element, r) {
        if (!element.background) return;
        ctx.fillStyle = element.background;
        ctx.fillRect(r.x, r.y, r.width, r.height);
    }

    _drawButton(ctx, element, r) {
        if (element.background) {
            ctx.fillStyle = element.hovered ? lighten(element.background) : element.background;
            ctx.fillRect(r.x, r.y, r.width, r.height);
        }
        this._drawLabel(ctx, element, r, 'center');
    }

    _drawBar(ctx, element, r) {
        if (element.background) {
            ctx.fillStyle = element.background;
            ctx.fillRect(r.x, r.y, r.width, r.height);
        }
        const fraction = Math.max(0, Math.min(1, Number(element.value) || 0));
        ctx.fillStyle = element.tint;
        ctx.fillRect(r.x, r.y, r.width * fraction, r.height);

        if (element.text) this._drawLabel(ctx, element, r, 'center');
    }

    _drawLabel(ctx, element, r, forceAlign) {
        if (!element.text) return;
        const align = forceAlign ?? element.align;
        const scale = Math.max(1, Math.round(element.scale));

        const textWidth = widestLine(element.text, scale);
        const textHeight = measureHeight(element.text, scale);

        let x = r.x + element.padding;
        if (align === 'center') x = r.x + (r.width - textWidth) / 2;
        else if (align === 'right') x = r.x + r.width - textWidth - element.padding;

        const y = element.kind === 'label' && r.height <= textHeight
            ? r.y
            : r.y + (r.height - textHeight) / 2;

        drawText(ctx, element.text, Math.round(x), Math.round(y), { scale, colour: element.tint });
    }

    _drawImage(ctx, element, r, images) {
        const texture = images?.get?.(element.texturePath);
        if (!texture) {
            // No art yet: a tinted box, the same answer SpriteRenderer gives.
            if (element.background) { ctx.fillStyle = element.background; ctx.fillRect(r.x, r.y, r.width, r.height); }
            return;
        }
        ctx.drawImage(texture, r.x, r.y, r.width, r.height);
    }
}

/** The widest line in a block, which is what centring has to measure. */
export function widestLine(text, scale = 1) {
    return String(text ?? '').split('\n').reduce((w, line) => Math.max(w, measure(line, scale)), 0);
}

/** A hover tint that works for "#rgb", "#rrggbb" and anything else (returned as-is). */
function lighten(colour) {
    const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(String(colour));
    if (!hex) return colour;

    let body = hex[1];
    if (body.length === 3) body = body.split('').map((c) => c + c).join('');

    const channels = [0, 2, 4].map((i) => Math.min(255, Math.round(parseInt(body.slice(i, i + 2), 16) * 1.25) + 12));
    return `#${channels.map((c) => c.toString(16).padStart(2, '0')).join('')}`;
}

export { measure, measureHeight, lineHeight };
