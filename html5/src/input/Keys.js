// -----------------------------------------------------------------------------
// Keys — key names, and the translation from browser codes to XNA's spelling.
//
// Action maps and scripts written against the C# engine name keys the way XNA's
// `Keys` enum does: "A", "Space", "Left", "LeftShift", "D1". The browser reports
// `KeyboardEvent.code` instead: "KeyA", "Space", "ArrowLeft", "ShiftLeft",
// "Digit1". Normalising to the XNA spelling means an ActionMap JSON file works
// in both engines untouched.
// -----------------------------------------------------------------------------

/** Explicit browser-code to XNA-name pairs. Anything regular is handled below. */
const CODE_MAP = {
    ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    ShiftLeft: 'LeftShift', ShiftRight: 'RightShift',
    ControlLeft: 'LeftControl', ControlRight: 'RightControl',
    AltLeft: 'LeftAlt', AltRight: 'RightAlt',
    MetaLeft: 'LeftWindows', MetaRight: 'RightWindows',
    Backspace: 'Back', Backquote: 'OemTilde', Minus: 'OemMinus', Equal: 'OemPlus',
    BracketLeft: 'OemOpenBrackets', BracketRight: 'OemCloseBrackets',
    Backslash: 'OemPipe', Semicolon: 'OemSemicolon', Quote: 'OemQuotes',
    Comma: 'OemComma', Period: 'OemPeriod', Slash: 'OemQuestion',
    NumpadAdd: 'Add', NumpadSubtract: 'Subtract', NumpadMultiply: 'Multiply',
    NumpadDivide: 'Divide', NumpadDecimal: 'Decimal', NumpadEnter: 'Enter',
    ContextMenu: 'Apps', PrintScreen: 'PrintScreen', ScrollLock: 'Scroll',
    CapsLock: 'CapsLock', NumLock: 'NumLock', Pause: 'Pause',
};

/**
 * Turns a `KeyboardEvent.code` into the name the engine and its action maps use.
 * @param {string} code
 * @returns {string}
 */
export function normalizeKeyCode(code) {
    if (!code) return '';
    if (CODE_MAP[code]) return CODE_MAP[code];

    if (code.startsWith('Key')) return code.slice(3);            // KeyA    -> A
    if (code.startsWith('Digit')) return `D${code.slice(5)}`;    // Digit1  -> D1
    if (code.startsWith('Numpad')) return `NumPad${code.slice(6)}`;

    return code;   // Space, Enter, Escape, Tab, F1..F12, Home, End, Delete, Insert
}

/**
 * Accepts whatever a caller passes for a key — "A", "a", "KeyA", "Space" — and
 * returns the canonical name, so `isKeyDown` is forgiving about spelling.
 */
export function canonicalKey(key) {
    if (typeof key !== 'string' || key.length === 0) return '';

    const mapped = normalizeKeyCode(key);
    if (mapped !== key) return mapped;

    // A bare letter or digit typed in either case.
    if (key.length === 1) {
        if (/[a-zA-Z]/.test(key)) return key.toUpperCase();
        if (/[0-9]/.test(key)) return `D${key}`;
    }

    return key;
}

/** Names used often enough to be worth a constant, so typos surface at import time. */
export const Keys = Object.freeze({
    A: 'A', B: 'B', C: 'C', D: 'D', E: 'E', F: 'F', G: 'G', H: 'H', I: 'I',
    J: 'J', K: 'K', L: 'L', M: 'M', N: 'N', O: 'O', P: 'P', Q: 'Q', R: 'R',
    S: 'S', T: 'T', U: 'U', V: 'V', W: 'W', X: 'X', Y: 'Y', Z: 'Z',
    D0: 'D0', D1: 'D1', D2: 'D2', D3: 'D3', D4: 'D4',
    D5: 'D5', D6: 'D6', D7: 'D7', D8: 'D8', D9: 'D9',
    F1: 'F1', F2: 'F2', F3: 'F3', F4: 'F4', F5: 'F5', F6: 'F6',
    F7: 'F7', F8: 'F8', F9: 'F9', F10: 'F10', F11: 'F11', F12: 'F12',
    Up: 'Up', Down: 'Down', Left: 'Left', Right: 'Right',
    Space: 'Space', Enter: 'Enter', Escape: 'Escape', Tab: 'Tab', Back: 'Back',
    LeftShift: 'LeftShift', RightShift: 'RightShift',
    LeftControl: 'LeftControl', RightControl: 'RightControl',
    LeftAlt: 'LeftAlt', RightAlt: 'RightAlt',
    Delete: 'Delete', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown',
});

/**
 * Mouse buttons, matching `Input/MouseButton.cs` and `MouseEvent.button`: both put the
 * middle button at 1 and the right button at 2. (This table once had them the other way
 * round, so a script asking for the right button got the wheel.)
 */
export const MouseButton = Object.freeze({
    Left: 0,
    Middle: 1,
    Right: 2,
    XButton1: 3,
    XButton2: 4,
});
