// -----------------------------------------------------------------------------
// Color — byte RGBA, matching Microsoft.Xna.Framework.Color.
//
// Channels are 0-255 integers because that is what the C# type stores and what
// scene files contain. `toJSON` writes the `#RRGGBBAA` string `ColorJsonConverter`
// writes, and `from` accepts every shape it reads: hex string of 3, 6 or 8
// digits, `{ R, G, B, A }`, `{ r, g, b, a }`, `[r, g, b, a]` and colour names.
// -----------------------------------------------------------------------------

/** An 8-bit-per-channel RGBA colour. */
export class Color {
    /**
     * @param {number} [r=255] Red, 0-255.
     * @param {number} [g=255] Green, 0-255.
     * @param {number} [b=255] Blue, 0-255.
     * @param {number} [a=255] Alpha, 0-255.
     */
    constructor(r = 255, g = 255, b = 255, a = 255) {
        this.r = clampByte(r);
        this.g = clampByte(g);
        this.b = clampByte(b);
        this.a = clampByte(a);
    }

    clone() { return new Color(this.r, this.g, this.b, this.a); }
    set(r, g, b, a = this.a) {
        this.r = clampByte(r); this.g = clampByte(g);
        this.b = clampByte(b); this.a = clampByte(a);
        return this;
    }

    // ---- Conversions ---------------------------------------------------------

    /** Normalised `[r, g, b, a]` in 0..1, the form shaders want. */
    toFloatArray() { return [this.r / 255, this.g / 255, this.b / 255, this.a / 255]; }

    /** `#RRGGBBAA`, upper case — byte-identical to what the C# converter writes. */
    toHex() {
        const h = (n) => n.toString(16).toUpperCase().padStart(2, '0');
        return `#${h(this.r)}${h(this.g)}${h(this.b)}${h(this.a)}`;
    }

    /** `rgba(r, g, b, a)` for a Canvas2D fill or stroke style. */
    toCss() { return `rgba(${this.r}, ${this.g}, ${this.b}, ${(this.a / 255).toFixed(4)})`; }

    /** `#RRGGBB`, dropping alpha — what an `<input type="color">` accepts. */
    toHexRgb() {
        const h = (n) => n.toString(16).padStart(2, '0');
        return `#${h(this.r)}${h(this.g)}${h(this.b)}`;
    }

    toJSON() { return this.toHex(); }
    toString() { return this.toHex(); }

    equals(c) {
        return this.r === c.r && this.g === c.g && this.b === c.b && this.a === c.a;
    }

    /** Multiplies every channel, alpha included, by a scalar. */
    multiply(scalar) {
        return new Color(this.r * scalar, this.g * scalar, this.b * scalar, this.a * scalar);
    }

    /** A copy with a different alpha, given in 0..1. */
    withAlpha(alpha01) { return new Color(this.r, this.g, this.b, alpha01 * 255); }

    // ---- Construction --------------------------------------------------------

    /** From normalised floats in 0..1. */
    static fromFloats(r, g, b, a = 1) {
        return new Color(r * 255, g * 255, b * 255, a * 255);
    }

    static lerp(a, b, t) {
        return new Color(
            a.r + (b.r - a.r) * t,
            a.g + (b.g - a.g) * t,
            a.b + (b.b - a.b) * t,
            a.a + (b.a - a.a) * t);
    }

    /**
     * Reads every colour shape a scene file, a tool argument or a stylesheet uses.
     * Anything unrecognised comes back white rather than throwing, so one bad
     * value in a scene cannot stop it loading.
     */
    static from(value) {
        if (value == null) return new Color();
        if (value instanceof Color) return value.clone();

        if (typeof value === 'string') return Color.fromString(value);

        if (Array.isArray(value)) {
            return new Color(
                +value[0] || 0, +value[1] || 0, +value[2] || 0,
                value.length > 3 ? +value[3] : 255);
        }

        if (typeof value === 'object') {
            // Both casings turn up: C# writes R/G/B/A, hand-written JSON tends to lower case.
            const r = value.r ?? value.R;
            const g = value.g ?? value.G;
            const b = value.b ?? value.B;
            const a = value.a ?? value.A ?? 255;
            if (r !== undefined || g !== undefined || b !== undefined) {
                return new Color(+r || 0, +g || 0, +b || 0, +a);
            }
        }

        return new Color();
    }

    /** Parses `#RGB`, `#RRGGBB`, `#RRGGBBAA` (with or without `#`) or a colour name. */
    static fromString(text) {
        const raw = String(text).trim();
        const named = NAMED_COLOURS[raw.toLowerCase()];
        if (named) return Color.fromString(named);

        const hex = raw.replace(/^#/, '');
        const byte = (i, len) => len === 3
            ? parseInt(hex[i] + hex[i], 16)
            : parseInt(hex.substring(i * 2, i * 2 + 2), 16);

        if (/^[0-9a-fA-F]{3}$/.test(hex)) return new Color(byte(0, 3), byte(1, 3), byte(2, 3), 255);
        if (/^[0-9a-fA-F]{6}$/.test(hex)) return new Color(byte(0, 6), byte(1, 6), byte(2, 6), 255);
        if (/^[0-9a-fA-F]{8}$/.test(hex)) {
            return new Color(byte(0, 8), byte(1, 8), byte(2, 8), byte(3, 8));
        }

        return new Color();
    }

    // ---- Named constants -----------------------------------------------------
    // Getters, not fields: Color is mutable and a shared instance would be
    // corrupted by the first caller that assigned to a channel.

    static get white()       { return new Color(255, 255, 255, 255); }
    static get black()       { return new Color(0, 0, 0, 255); }
    static get transparent() { return new Color(0, 0, 0, 0); }
    static get red()         { return new Color(255, 0, 0, 255); }
    static get green()       { return new Color(0, 255, 0, 255); }
    static get blue()        { return new Color(0, 0, 255, 255); }
    static get yellow()      { return new Color(255, 255, 0, 255); }
    static get cyan()        { return new Color(0, 255, 255, 255); }
    static get magenta()     { return new Color(255, 0, 255, 255); }
    static get gray()        { return new Color(128, 128, 128, 255); }
    static get grey()        { return new Color(128, 128, 128, 255); }
    static get orange()      { return new Color(255, 165, 0, 255); }
    static get cornflowerBlue() { return new Color(100, 149, 237, 255); }
}

function clampByte(n) {
    return Math.max(0, Math.min(255, Math.round(+n || 0)));
}

/** The subset of CSS colour names the C# engine's colour parser accepts. */
const NAMED_COLOURS = {
    white: '#FFFFFF', black: '#000000', red: '#FF0000', green: '#00FF00',
    blue: '#0000FF', yellow: '#FFFF00', cyan: '#00FFFF', magenta: '#FF00FF',
    gray: '#808080', grey: '#808080', orange: '#FFA500', purple: '#800080',
    pink: '#FFC0CB', brown: '#A52A2A', lime: '#00FF00', navy: '#000080',
    teal: '#008080', olive: '#808000', maroon: '#800000', silver: '#C0C0C0',
    gold: '#FFD700', transparent: '#00000000', cornflowerblue: '#6495ED',
};
