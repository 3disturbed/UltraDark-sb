// -----------------------------------------------------------------------------
// input — UltraDark's keyboard, mouse, pads and touch, read from the engine.
//
// TwinStickTron's client/js/input.js at 7eee633, ported onto the engine's Input.
// It merges every device into one state, {mx, my, ax, ay, buttons} with the BTN
// bits of shared/protocol.js; claims pads for couch co-op; reads a seat's whole
// pad; gives a seat's d-pad and A as edges for the draft; and runs a phone's twin
// sticks, flick dash and four buttons. The logic is the original's, statement for
// statement. What moved is what it stood on:
//
//   - The page's events are polled. pollInput takes the frame's touches and mouse
//     from Input before anything else, so it is called once a frame ahead of the
//     rest of this module, as main.js's frame() called it. What the original
//     latched as an event arrived -- a right click, a tap on a button -- is latched
//     from the frame's pressed edge, or a touch listed for the first time, so each
//     is latched exactly once and held for its tap window, as the original held it.
//   - navigator.getGamepads() is Input's pads 0 to 3, in the standard layout the
//     Gamepad API gave; performance.now(), game.js's world.me, render.js's
//     screenToWorld and the page's size (innerWidth, and the vw and vh of
//     styles.css) are handed to initInput.
//   - The phone's controls were elements in #touch-ui, placed by styles.css. The
//     buttons are hit-tested here with the stylesheet's own geometry, and
//     touchSticks, touchButtons and the sizes below are what a painter draws them
//     from. Every length is a CSS pixel, as the stylesheet's were: Input's
//     positions, screen() and what screenToWorld is handed must all be in them, so
//     on a canvas drawn at the display's density the host hands in an input and a
//     screen measured in CSS pixels.
//   - touchActive is a function, as a module may not export a let.
//
// Every change from the original is marked "DarkShapes" where it is made.
// -----------------------------------------------------------------------------

// Input: keyboard+mouse, gamepad, touch — merged into one state object
// {mx,my,ax,ay,buttons} in world axes (SDD §2.2 mappings).

import { BTN } from "../shared/protocol.js";

// DarkShapes: what the original imported from game.js and render.js or read off the page, handed in
// by initInput. `input` is the engine's Input, or anything with its members.
let input = null, clock = null, screenToWorld = null, ship = null, screen = null;
const world = { get me() { return ship(); } };
const performance = { now: () => clock() };

// DarkShapes: the sizes styles.css gave the touch controls, which a painter now draws.

/** The base of a touch stick, 110px across (styles.css `.stick`). */
export const STICK_BASE_SIZE = 110;

/** A touch stick's knob, 44px across (`.stick i`). */
export const STICK_KNOB_SIZE = 44;

/** How far a knob is moved at full deflection: the original translated it by 30px times the stick's value. */
export const STICK_KNOB_TRAVEL = 30;

// DarkShapes: Input keeps the keys held, by the codes the original kept in a Set from keydown, keyup
// and blur.
const keys = { has: (code) => input.isKeyDown(code) };
let mouseX = 0, mouseY = 0, mouseDown = false, rmbDown = false;
// A tap is held for a short window rather than for one frame. Input is polled
// every animation frame but SENT at 30 Hz (main.js), so a press that lasted one
// frame was overwritten before it went out about half the time at 60fps -- the
// touch buttons and right-click bomb silently dropped taps. The server acts on
// the rising edge, so holding the bit never repeats an action.
const TAP_HOLD_MS = 100;
const tapAt = { dash: -1e9, bomb: -1e9, abil: -1e9, use: -1e9 };
const tap = (key) => { tapAt[key] = performance.now(); };
const tapped = (key, now) => now - tapAt[key] < TAP_HOLD_MS;
const touch = { l: null, r: null, lx: 0, ly: 0, rx: 0, ry: 0 };
// DarkShapes: `export let touchActive` is refused in a module; it is exported below as a function
// that reads it.
let touchActive = false;

/**
 * DarkShapes: the original took the canvas and put its listeners on it and on the page; what they
 * heard is read from `input` by pollInput, once a frame.
 *
 * @param {object} host
 * @param {object} host.input The engine's Input, or anything with its members.
 * @param {() => number} host.now Milliseconds on one clock: performance.now().
 * @param {(sx: number, sy: number) => { x: number, y: number }} host.screenToWorld render.js's.
 * @param {() => { x: number, y: number }} host.me The local ship: game.js's world.me.
 * @param {() => { width: number, height: number }} host.screen The screen: innerWidth and innerHeight.
 */
export function initInput(host) {
  ({ input, now: clock, screenToWorld, me: ship, screen } = host);
}

// touch: left half = move stick, right half = aim/fire stick
// DarkShapes: #stick-l and #stick-r were moved to where their touch began; where each was last put
// is kept for touchSticks.
const RADIUS = 55;
const stickAt = { l: null, r: null };

// DarkShapes: the canvas's touchstart listener, for one touch.
function touchStart(t) {
  touchActive = true;
  const side = t.clientX < screen().width / 2 ? "l" : "r"; // DarkShapes: innerWidth
  if (!touch[side]) {
    touch[side] = { id: t.identifier, ox: t.clientX, oy: t.clientY };
    stickAt[side] = { x: t.clientX, y: t.clientY };
  }
}

// DarkShapes: the canvas's touchmove listener, for one touch. The knob it moved is touchSticks'.
function touchMove(t) {
  for (const side of ["l", "r"]) {
    const s = touch[side];
    if (s && s.id === t.identifier) {
      const dx = (t.clientX - s.ox) / RADIUS, dy = (t.clientY - s.oy) / RADIUS;
      const len = Math.hypot(dx, dy) || 1;
      const cl = len > 1 ? 1 / len : 1;
      touch[side === "l" ? "lx" : "rx"] = dx * cl;
      touch[side === "l" ? "ly" : "ry"] = dy * cl;
    }
  }
}

// DarkShapes: endTouch, the canvas's touchend and touchcancel listener, for one touch.
function endTouch(t) {
  for (const side of ["l", "r"]) {
    const s = touch[side];
    if (s && s.id === t.identifier) {
      touch[side] = null;
      touch[side === "l" ? "lx" : "rx"] = 0;
      touch[side === "l" ? "ly" : "ry"] = 0;
    }
  }
}

// DarkShapes: the buttons' touchstart listeners. A touch that began on a button was the button's,
// and the canvas never heard of it; the buttons could be touched once the canvas's first touch had
// shown #touch-ui. Each button taps the key the original's [id, key] table gave it.
function buttonTouchStart(t) {
  const button = touchActive ? touchButtonAt(t.clientX, t.clientY) : null;
  if (button === "dash") tap("dash");
  else if (button === "bomb") tap("bomb");
  else if (button === "abil") tap("abil");
  else if (button === "use") tap("use");
  return button !== null;
}

// DarkShapes: the touches seen to begin and not yet seen to end, by id.
const touchIds = new Set();

// DarkShapes: what the listeners initInput added did as each event arrived, done from what Input
// reports for the frame. A touch has begun the first frame Input lists its id, or when "Began"
// names an id already held, whose touch ended unseen. It has ended when it is listed "Ended" or
// "Cancelled", or no longer listed at all (as a touchcancel); one that began and ended between two
// frames does both. What the original's touchstart was handed is `found.startX, found.startY`, where
// the finger landed, which is what the side test and the buttons' hit test read and what a stick
// anchors to; touchmove gets where it is now. A finger is also a press of the left button in a
// browser, and never was in the original, whose touchstart kept the page from making mouse events of
// it -- Input.isMouseTouch says which a press was, so a thumb elsewhere on the screen no longer
// stops the gun. A finger moves a browser's cursor too, which nothing reads: the mouse stops aiming
// at the first touch.
function readInput() {
  const listed = new Set();
  for (let i = 0; i < input.touchCount; i++) {
    const found = input.getTouch(i);
    if (!found) continue;
    const id = found.id;
    const landed = { identifier: id, clientX: found.startX, clientY: found.startY };
    const at = { identifier: id, clientX: found.x, clientY: found.y };
    const ended = found.phase === "Ended" || found.phase === "Cancelled";
    listed.add(id);
    if (found.phase === "Began" && touchIds.has(id)) {
      endTouch(at);
      touchIds.delete(id);
    }
    if (!touchIds.has(id)) {
      touchIds.add(id);
      if (!buttonTouchStart(landed)) touchStart(landed);
    } else if (!ended) {
      touchMove(at);
    }
    if (ended) {
      endTouch(at);
      touchIds.delete(id);
    }
  }
  for (const id of [...touchIds]) {
    if (listed.has(id)) continue;
    endTouch({ identifier: id });
    touchIds.delete(id);
  }

  mouseX = input.mouseX; mouseY = input.mouseY;
  mouseDown = input.isMouseDown(0) && !input.isMouseTouch(0);
  rmbDown = input.isMouseDown(2);
  if (input.isMousePressed(2)) tap("bomb");
}

// double-tap-ish dash on touch: quick full deflection after neutral
let lastLMag = 0, dashTapT = 0;

// ---------- couch co-op pad management ----------
// Pads are CLAIMED: the first active pad belongs to P1 (merged with
// keyboard/mouse, as ever). Any *unclaimed* pad pressing START becomes a
// new local player. main.js polls detectPadJoin() each frame.
const padClaims = new Map(); // padIndex -> "p1" | seat object marker
const padStartPrev = new Map();

export function claimPad(index, owner) { padClaims.set(index, owner); }
export function releasePad(index) { padClaims.delete(index); }

// DarkShapes: navigator.getGamepads() is Input's pads, each shaped as much of a Gamepad as this
// module reads: its index, connected, the standard layout's four axes and seventeen buttons, and
// `anyPressed` where the original wrote `buttons.some(b => b.pressed)`. That one read is the only
// place the original looked at a pad's whole button list rather than at a button it names, and a
// browser's list is longer than the layout for some pads -- a DualShock 4 or DualSense touchpad is
// 17 -- so Input.padAnyButton answers it instead of this array, which stays the seventeen a script
// may name.
const PAD_BUTTONS = 17;
const navigator = {
  getGamepads() {
    const pads = [];
    for (let index = 0; index < input.padCount; index++) {
      if (!input.padConnected(index)) { pads.push(null); continue; }
      const axes = [input.padAxis(index, 0), input.padAxis(index, 1), input.padAxis(index, 2), input.padAxis(index, 3)];
      const buttons = [];
      for (let b = 0; b < PAD_BUTTONS; b++) buttons.push({ pressed: input.padButton(index, b) });
      pads.push({ index, connected: true, axes, buttons, anyPressed: input.padAnyButton(index) });
    }
    return pads;
  },
};

function livePads() {
  return [...(navigator.getGamepads?.() ?? [])].filter(gp => gp && gp.connected);
}

export function detectPadJoin() {
  for (const gp of livePads()) {
    const start = !!gp.buttons[9]?.pressed;
    const prev = padStartPrev.get(gp.index) ?? false;
    padStartPrev.set(gp.index, start);
    if (start && !prev && !padClaims.has(gp.index)) return gp.index;
  }
  return null;
}

// Read ONE pad as a full input state (a couch seat's whole controller)
export function pollPad(index) {
  let mx = 0, my = 0, ax = 1, ay = 0, buttons = 0;
  const gp = navigator.getGamepads?.()[index];
  if (gp && gp.connected) {
    const [lx, ly, rx, ry] = gp.axes;
    if (Math.hypot(lx, ly) > 0.18) { mx = lx; my = ly; }
    if (Math.hypot(rx ?? 0, ry ?? 0) > 0.35) { ax = rx; ay = ry; buttons |= BTN.FIRE; }
    if (gp.buttons[5]?.pressed || gp.buttons[10]?.pressed) buttons |= BTN.DASH; // RB / L3
    if (gp.buttons[4]?.pressed) buttons |= BTN.BOMB;                            // LB
    if (gp.buttons[7]?.pressed || gp.buttons[0]?.pressed) buttons |= BTN.ABILITY; // RT / A
    if (gp.buttons[2]?.pressed) buttons |= BTN.USE;                             // X — consumable
  }
  return { mx, my, ax, ay, buttons };
}

// D-pad/A edges for menu (draft) navigation by a couch seat's pad
const padNavPrev = new Map();
export function pollPadNav(index) {
  const gp = navigator.getGamepads?.()[index];
  const cur = {
    left: !!gp?.buttons[14]?.pressed, right: !!gp?.buttons[15]?.pressed,
    confirm: !!gp?.buttons[0]?.pressed,
  };
  const prev = padNavPrev.get(index) ?? { left: false, right: false, confirm: false };
  padNavPrev.set(index, cur);
  return {
    left: cur.left && !prev.left,
    right: cur.right && !prev.right,
    confirm: cur.confirm && !prev.confirm,
  };
}

export function pollInput() {
  readInput(); // DarkShapes: the frame's events, as the listeners had them before the frame began
  let mx = 0, my = 0, ax = 0, ay = 0, buttons = 0;

  // keyboard
  if (keys.has("KeyW") || keys.has("ArrowUp")) my -= 1;
  if (keys.has("KeyS") || keys.has("ArrowDown")) my += 1;
  if (keys.has("KeyA") || keys.has("ArrowLeft")) mx -= 1;
  if (keys.has("KeyD") || keys.has("ArrowRight")) mx += 1;
  if (keys.has("Space") || keys.has("ShiftLeft") || keys.has("ShiftRight")) buttons |= BTN.DASH;
  if (keys.has("KeyE")) buttons |= BTN.BOMB;
  if (keys.has("KeyQ")) buttons |= BTN.ABILITY;
  if (keys.has("KeyF")) buttons |= BTN.USE;
  if (mouseDown) buttons |= BTN.FIRE;

  // mouse aim: vector from my ship to cursor, in world space
  if (!touchActive) {
    const w = screenToWorld(mouseX, mouseY);
    const dx = w.x - world.me.x, dy = w.y - world.me.y;
    const len = Math.hypot(dx, dy) || 1;
    ax = dx / len; ay = dy / len;
  }

  // P1's own pad (first active unclaimed pad claims to P1; claimed-by-seat
  // pads are strictly hands-off)
  for (const gp of livePads()) {
    const owner = padClaims.get(gp.index);
    if (owner !== undefined && owner !== "p1") continue;
    if (owner === undefined) {
      const [lx, ly, rx, ry] = gp.axes;
      const active = Math.hypot(lx, ly) > 0.18 || Math.hypot(rx ?? 0, ry ?? 0) > 0.35 ||
        gp.anyPressed;   // DarkShapes: the original's gp.buttons.some(b => b.pressed)
      if (!active || [...padClaims.values()].includes("p1")) continue;
      padClaims.set(gp.index, "p1");
    }
    const p = pollPad(gp.index);
    if (Math.hypot(p.mx, p.my) > 0.18) { mx = p.mx; my = p.my; }
    if (p.buttons & BTN.FIRE) { ax = p.ax; ay = p.ay; }
    buttons |= p.buttons;
    break;
  }

  // touch merges
  if (touchActive) {
    if (Math.hypot(touch.lx, touch.ly) > 0.12) { mx = touch.lx; my = touch.ly; }
    if (Math.hypot(touch.rx, touch.ry) > 0.25) { ax = touch.rx; ay = touch.ry; buttons |= BTN.FIRE; }
    // flick dash: stick snaps from neutral to full deflection
    const mag = Math.hypot(touch.lx, touch.ly);
    const now = performance.now();
    if (mag > 0.95 && lastLMag < 0.2 && now - dashTapT > 400) { dashTapT = now; }
    if (now - dashTapT < 120) buttons |= BTN.DASH;
    lastLMag = mag;
  }
  const tapNow = performance.now();
  if (tapped("dash", tapNow)) buttons |= BTN.DASH;
  if (tapped("bomb", tapNow)) buttons |= BTN.BOMB;
  if (tapped("abil", tapNow)) buttons |= BTN.ABILITY;
  if (tapped("use", tapNow)) buttons |= BTN.USE;

  const l = Math.hypot(mx, my);
  if (l > 1) { mx /= l; my /= l; }
  return { mx, my, ax, ay, buttons };
}

// ---------- what the page drew ----------
// DarkShapes: #touch-ui showed from the first touch on, and a painter shows its controls while
// touchActive() is true. Lengths in vw and vh are percentages of the screen's width and height, as
// CSS works them out.

const percentOf = (size, n) => (n * size) / 100;

/** Whether a finger has touched the game: the touch controls show, and the mouse no longer aims. */
function isTouchActive() {
  return touchActive;
}
export { isTouchActive as touchActive };

/**
 * Both sticks as the page showed them: whether a touch holds each, where its base is centred -- where
 * its touch began, or where styles.css put it before any touch had -- and its value, which moved the
 * knob by that times STICK_KNOB_TRAVEL.
 *
 * @returns {{ l: { active: boolean, ox: number, oy: number, dx: number, dy: number }, r: object }}
 */
export function touchSticks() {
  const { width, height } = screen();
  const half = STICK_BASE_SIZE / 2;
  const y = height - percentOf(height, 12) - half;                     // .stick { bottom: 12vh }
  const l = stickAt.l ?? { x: percentOf(width, 6) + half, y };          // #stick-l { left: 6vw }
  const r = stickAt.r ?? { x: width - percentOf(width, 6) - half, y };  // #stick-r { right: 6vw }
  return {
    l: { active: touch.l !== null, ox: l.x, oy: l.y, dx: touch.lx, dy: touch.ly },
    r: { active: touch.r !== null, ox: r.x, oy: r.y, dx: touch.rx, dy: touch.ry },
  };
}

/**
 * The four buttons, in the page's order: each one's id, centre and radius. `.tbtn` is a 54px circle
 * with its bottom edge 26vh up; each button's own rule places its right edge, two move it up, and
 * the dash button's moves it down.
 *
 * @returns {{ id: "dash" | "bomb" | "abil" | "use", x: number, y: number, r: number }[]}
 */
export function touchButtons() {
  const { width, height } = screen();
  const r = 54 / 2;
  const at = (id, right, bottom) => ({ id, x: width - right - r, y: height - bottom - r, r });
  return [
    // Dash sits beside the aim stick, under the bomb: the right thumb taps it while
    // the left thumb keeps steering, and a dash goes the way you are moving.
    at("dash", percentOf(width, 6) + 130, percentOf(height, 12) - 10), // #tbtn-dash { right: calc(6vw + 130px); bottom: calc(12vh - 10px) }
    at("bomb", percentOf(width, 6) + 130, percentOf(height, 26)),     // #tbtn-bomb { right: calc(6vw + 130px) }
    at("abil", percentOf(width, 6) - 10, percentOf(height, 26) + 40), // #tbtn-abil { right: calc(6vw - 10px); bottom: calc(26vh + 40px) }
    at("use", percentOf(width, 6) + 130, percentOf(height, 26) + 70), // #tbtn-use { right: calc(6vw + 130px); bottom: calc(26vh + 70px) }
  ];
}

/** The button a point is on -- inside its circle, as border-radius: 50% shapes it -- or null. */
function touchButtonAt(x, y) {
  for (const button of touchButtons()) {
    const dx = x - button.x, dy = y - button.y;
    if (dx * dx + dy * dy <= button.r * button.r) return button.id;
  }
  return null;
}
