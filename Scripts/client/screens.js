// -----------------------------------------------------------------------------
// screens — UltraDark's menus and overlays, on the engine's UI.
//
// The original client drew the arena on a canvas and everything around it as DOM:
// index.html's menu, lobby, draft, Core Shop, score, settings and leaderboards, a
// toast and a banner; styles.css's neon look over them; and ui.js, whose functions
// main.js called to fill them in. This module is ui.js on the engine's retained UI,
// export for export -- the flow imports it as `import * as Screens from
// "./screens.js"` where main.js imported ui.js as UI, a name that is a contract
// global here.
//
//   initScreens({ ui, clock })  once, first: the engine's UI global (or anything with
//                               its members) and a clock with now, setTimeout and
//                               clearTimeout, which engine/clock.js's Clock is
//   poll()                      once a frame: the engine's UI is polled, not called
//                               back, so this delivers the frame's clicks, slider
//                               moves and typing to the handlers ui.js's DOM events
//                               reached, and redraws what hovering changes
//   bind(id, handler)           an element's onclick, for the buttons main.js bound by
//                               id (btn-challenge, btn-tab-shop, btn-settings, ...)
//   setValue(id, value)         an input's value, as main.js wrote the callsign
//
// How it stands on the engine:
//
//   - The DOM ui.js wrote to is kept as a small element model (ScreenElement): tags,
//     ids, classes, inline style, text, children and handlers, built as index.html
//     built it. Every ui.js statement that touched an element touches the same member
//     of one here, in the same order; what an innerHTML template wrote is built as the
//     same elements. Each change is drawn at once onto the engine's nodes.
//   - Every element is a Panel named after its id, whose `style` is its class list --
//     this canvas has no theme, so the classes draw nothing and say what state the
//     element is in. Its box (background, border, corners, box-shadow as a shadow,
//     padding) is the Panel; what is inside it hangs under one content node beneath,
//     laid out as a column, a flex row, a wrapping row or a grid.
//   - styles.css is a cascade here too (computeStyle): each rule the screens use, in
//     the stylesheet's order and weighed by its selector's specificity, over the
//     inherited text properties, with :hover and :active read from the engine's node
//     state. Margins between blocks collapse as CSS collapses them.
//   - Text is what the engine's faces draw. body text is `body`, bold text
//     `body-strong`, h2 and h3 `heading`, the logo `display`, and the monospace bits
//     `mono`/`mono-bold`; engine/glyphs.js cuts every string into runs, so a symbol
//     draws in mono in a node of its own. A line that can wrap is a wrapping row of
//     word chips, so it wraps between words as a browser does and each line takes the
//     block's alignment; a label's own wrapping would align the whole block at once.
//   - Sizes are CSS pixels times one scale: 1 at a 1280x720 view, growing with the
//     view past that, and the stylesheet's max-width: 640px rule applies when the view
//     is that narrow in CSS pixels, as on a phone held upright. The contract has no
//     display density, so a view is measured in its own units.
//
// Where the engine cannot say what the DOM said, the place is marked "DarkShapes:"
// with what differs and why. Two changes run through every function and are marked
// once, where they are defined: ui.js's $(id) is byId(id), and document's
// createElement and querySelectorAll are this module's own.
// -----------------------------------------------------------------------------

import { PILOTS, PHASE, MAX_PLAYERS } from "../shared/constants.js";
import { MODS, modById } from "../shared/mods.js";
import { shopItemById } from "../shared/shop.js";
import { runs } from "../engine/glyphs.js";

const screens = ["screen-menu", "screen-lobby", "screen-draft", "screen-score", "screen-settings", "screen-lb", "screen-shop"];

export const ui = { onAction: null }; // main.js wires this

// DarkShapes: what the module stands on is handed to it rather than found on window.
let engineUi = null;
let clock = null;

// ---- the element model -------------------------------------------------------------------

/** A text node: a run of text in an element. */
class ScreenText {
  constructor(text) {
    this.nodeValue = String(text);
    this.parentElement = null;
  }
}

/** classList: the element's classes, in the order a DOMTokenList keeps them. */
class ScreenClassList {
  constructor(element) {
    this.element = element;
    this.names = [];
  }

  contains(name) { return this.names.indexOf(name) >= 0; }

  add(...names) {
    let changed = false;
    for (const name of names) if (!this.contains(name)) { this.names.push(name); changed = true; }
    if (changed) lookChanged(this.element, names.indexOf("hidden") >= 0);
  }

  remove(...names) {
    const kept = this.names.filter((name) => names.indexOf(name) < 0);
    if (kept.length === this.names.length) return;
    this.names = kept;
    lookChanged(this.element, names.indexOf("hidden") >= 0);
  }

  toggle(name, force) {
    const on = force === undefined ? !this.contains(name) : Boolean(force);
    if (on) this.add(name);
    else this.remove(name);
    return on;
  }
}

/** The style attribute: the few properties ui.js wrote inline. */
class ScreenStyle {
  constructor(element) {
    this.element = element;
    this.values = {};
  }

  get opacity() { return this.values.opacity ?? ""; }
  set opacity(value) { this.write("opacity", String(value)); }

  get width() { return this.values.width ?? ""; }
  set width(value) { this.write("width", String(value)); }

  get color() { return this.values.color ?? ""; }
  set color(value) { this.write("color", String(value)); }

  setProperty(name, value) { this.write(name, String(value)); }

  getPropertyValue(name) { return this.values[name] ?? ""; }

  write(name, value) {
    if (this.values[name] === value) return;
    this.values[name] = value;
    const element = this.element;
    if (name === "width" && engineUi && element.live && element.look && element.childNodes.length === 0) {
      // An inline width outweighs every rule and moves nothing else of a box with nothing in it, as a
      // timer bar's fill is: the one property is written, which main.js does every intermission frame.
      element.computed.width = value;
      element.look.width = value;
      handleOf(element).width = value;
      return;
    }
    lookChanged(element, false);
  }
}

/** An element: what ui.js's DOM held for it, and the engine nodes it is drawn with. */
class ScreenElement {
  constructor(tagName) {
    this.tagName = tagName;
    this.id = "";
    this.parentElement = null;
    this.childNodes = [];
    this.dataset = {};
    this.classList = new ScreenClassList(this);
    this.style = new ScreenStyle(this);
    this.oninput = null;
    this.type = "";
    this.min = "";
    this.max = "";
    this.step = "";
    this.placeholder = "";
    this.maxLength = -1;
    this.clickHandler = null;
    this.isDisabled = false;
    this.rawValue = "";

    // What the engine shows of it: its computed style, the node, the node under it that
    // holds its content, and the pointer states the stylesheet reads.
    this.computed = null;
    this.look = null;
    this.live = false;
    this.node = null;
    this.content = null;
    this.path = null;
    this.parts = null;
    this.outer = [0, 0];
    this.empty = false;
    this.flow = null;
    this.margins = null;
    this.hover = false;
    this.active = false;
    this.focus = false;
    this.fade = null;
    this.shownAt = -1;
  }

  get className() { return this.classList.names.join(" "); }
  set className(value) {
    const wasHidden = this.classList.contains("hidden");
    this.classList.names = splitClasses(value);
    lookChanged(this, wasHidden !== this.classList.contains("hidden"));
  }

  get onclick() { return this.clickHandler; }
  set onclick(handler) {
    const had = Boolean(this.clickHandler);
    this.clickHandler = typeof handler === "function" ? handler : null;
    if (had !== Boolean(this.clickHandler)) lookChanged(this, false);
  }

  get disabled() { return this.isDisabled; }
  set disabled(value) {
    const next = Boolean(value);
    if (next === this.isDisabled) return;
    this.isDisabled = next;
    lookChanged(this, false);
  }

  /** An input's value: a range's is kept inside min..max on a step, as the DOM keeps it. */
  get value() {
    if (this.type === "range" && this.rawValue === "") return String(rangeValue(this, Number(this.min) + (Number(this.max) - Number(this.min)) / 2));
    return this.rawValue;
  }
  set value(value) {
    const next = this.type === "range" ? String(rangeValue(this, Number(value))) : String(value ?? "");
    if (next === this.rawValue) return;
    this.rawValue = next;
    valueChanged(this);
  }

  get textContent() {
    let out = "";
    for (const child of this.childNodes) out += child instanceof ScreenText ? child.nodeValue : child.textContent;
    return out;
  }
  set textContent(value) {
    const text = String(value ?? "");
    this.setChildren(text === "" ? [] : [text]);
  }

  get firstElementChild() {
    for (const child of this.childNodes) if (child instanceof ScreenElement) return child;
    return null;
  }

  appendChild(child) {
    child.parentElement = this;
    this.childNodes.push(child);
    childAppended(this, child);
    return child;
  }

  /** What an innerHTML template wrote: these nodes, strings as text, in place of what was there. */
  setChildren(nodes) {
    for (const child of this.childNodes) {
      child.parentElement = null;
      if (child instanceof ScreenElement) retire(child);
    }
    this.childNodes = [];
    for (const node of nodes) {
      const child = typeof node === "string" ? new ScreenText(node) : node;
      child.parentElement = this;
      this.childNodes.push(child);
    }
    contentChanged(this);
  }

  /** querySelectorAll for the one form ui.js used: ".class". */
  querySelectorAll(selector) {
    const name = selector.slice(1);
    const found = [];
    const walk = (element) => {
      for (const child of element.childNodes) {
        if (!(child instanceof ScreenElement)) continue;
        if (child.classList.contains(name)) found.push(child);
        walk(child);
      }
    };
    walk(this);
    return found;
  }
}

function splitClasses(text) {
  return String(text ?? "").split(" ").filter((name) => name !== "");
}

/** A range input's value as the DOM sanitises it: on a step from min, inside min..max. */
function rangeValue(input, value) {
  const min = Number(input.min);
  const max = Number(input.max);
  const step = Number(input.step) > 0 ? Number(input.step) : 1;
  if (!Number.isFinite(value)) return min + Math.round((max - min) / 2 / step) * step;
  const stepped = min + Math.round((value - min) / step) * step;
  return Math.min(max, Math.max(min, stepped));
}

/** An element built as markup builds one: attributes, then children, strings as text. */
function h(tagName, attributes, ...children) {
  const element = new ScreenElement(tagName);
  const attrs = attributes ?? {};
  for (const key of Object.keys(attrs)) {
    const value = attrs[key];
    if (key === "class") element.classList.names = splitClasses(value);
    else if (key === "style") for (const name of Object.keys(value)) element.style.values[name] = String(value[name]);
    else if (key === "data") Object.assign(element.dataset, value);
    else if (key === "value") element.rawValue = String(value);
    else element[key] = value;
  }
  for (const child of children) {
    const node = typeof child === "string" ? new ScreenText(child) : child;
    node.parentElement = element;
    element.childNodes.push(node);
  }
  return element;
}

// DarkShapes: document.createElement.
function createElement(tagName) {
  return new ScreenElement(tagName);
}

// ---- index.html --------------------------------------------------------------------------

/** index.html's #overlay: every screen, the toast and the banner, as the page first shows them. */
function buildOverlay() {
  return h("div", { id: "overlay" },
    // MENU
    h("section", { id: "screen-menu", class: "screen" },
      // DarkShapes: the product's name, in the logo's two colours, where it read ULTRA DARK.
      h("h1", { class: "logo" }, "DARK", h("span", null, "SHAPES")),
      h("p", { class: "tag" }, "Two sticks. Ten thousand robots. One link brings a friend."),
      h("div", { class: "row" },
        h("input", { id: "name", type: "text", maxLength: 12, placeholder: "CALLSIGN" })),
      h("div", { id: "pilots", class: "pilots" }),
      h("div", { id: "challenge-banner", class: "challenge hidden" }),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-solo", class: "btn primary" }, "SOLO RUN"),
        h("button", { id: "btn-create", class: "btn" }, "CREATE LOBBY"),
        h("button", { id: "btn-join", class: "btn primary hidden" }, "JOIN RUN"),
        h("button", { id: "btn-accept", class: "btn primary hidden" }, "⚔ ACCEPT CHALLENGE")),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-daily", class: "btn daily" }, "\u{1F311} DAILY DARK"),
        h("button", { id: "btn-lb", class: "btn" }, "\u{1F3C6} LEADERBOARDS"),
        h("button", { id: "btn-settings", class: "btn" }, "⚙")),
      h("p", { id: "menu-msg", class: "dim" }),
      h("p", { class: "dim tiny" }, "WASD move · mouse aim & fire · SPACE dash · E bomb · Q ability · F use item · gamepad & touch supported"),
      h("p", { class: "dim tiny" }, "\u{1F3AE} couch co-op: in a lobby, press START on extra controllers — up to 4 on one screen")),

    // LOBBY
    h("section", { id: "screen-lobby", class: "screen hidden" },
      h("h2", null, "LOBBY ", h("span", { id: "lobby-code", class: "code" })),
      h("div", { id: "roster", class: "roster" }),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-invite", class: "btn big invite" }, "\u{1F517} INVITE — SEND THE LINK")),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-start", class: "btn primary big" }, "▶ START RUN")),
      h("p", { class: "dim" }, "Friends open the link and drop straight in. 1–8 players."),
      h("p", { class: "couch-hint" }, "\u{1F3AE} COUCH CO-OP — press START on a controller to add a local player")),

    // DRAFT (intermission)
    h("section", { id: "screen-draft", class: "screen hidden compact" },
      h("h3", { id: "draft-title" }, "WAVE CLEAR — DRAFT AN UPGRADE"),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-tab-shop", class: "btn small shoptab hidden" }, "⬡ SHOP")),
      h("div", { id: "draft-locals" }),
      h("p", { id: "class-grant", class: "grant hidden" }),
      h("div", { id: "draft-cards", class: "cards" }),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-bank", class: "btn bank" }, "\u{1F3E6} BANK ", h("span", { id: "bank-amount" }))),
      h("div", { id: "draft-timer", class: "timerbar" }, h("i", null))),

    // SCORE
    h("section", { id: "screen-score", class: "screen hidden" },
      h("h2", { id: "score-title" }, "RUN OVER"),
      h("div", { id: "score-body", class: "scorebody" }),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-again", class: "btn primary big" }, "↻ AGAIN"),
        h("button", { id: "btn-challenge", class: "btn challenge-btn" }, "⚔ CHALLENGE A FRIEND"),
        h("button", { id: "btn-invite2", class: "btn" }, "\u{1F517} INVITE"))),

    // CORE SHOP (post-boss intermissions)
    h("section", { id: "screen-shop", class: "screen hidden" },
      h("h3", null, "⬡ CORE SHOP ", h("span", { id: "shop-cores", class: "code" })),
      h("p", { class: "dim tiny" }, "No rush: the next wave waits until everyone has shopped and picked an upgrade. READY takes you to your draft."),
      h("div", { id: "shop-cards", class: "cards shopgrid" }),
      h("div", { class: "row buttons" },
        h("button", { id: "btn-tab-draft", class: "btn small" }, "← DRAFT"),
        h("button", { id: "btn-shop-ready", class: "btn primary" }, "✔ READY")),
      h("div", { id: "shop-timer", class: "timerbar" }, h("i", null))),

    // SETTINGS
    h("section", { id: "screen-settings", class: "screen hidden" },
      h("h2", null, "SETTINGS"),
      h("div", { class: "setting" }, h("label", null, "Screen shake ", h("span", { id: "v-shake" })),
        h("input", { id: "set-shake", type: "range", min: "0", max: "100", step: "5" })),
      h("div", { class: "setting" }, h("label", null, "Flashes & strobe"),
        h("button", { id: "set-flash", class: "btn small" })),
      h("div", { class: "setting" }, h("label", null, "The-dark floor brightness ", h("span", { id: "v-floor" })),
        h("input", { id: "set-floor", type: "range", min: "0", max: "80", step: "5" })),
      h("div", { class: "setting" }, h("label", null, "Volume ", h("span", { id: "v-vol" })),
        h("input", { id: "set-vol", type: "range", min: "0", max: "100", step: "5" })),
      h("div", { class: "setting" }, h("label", null, "View"),
        h("button", { id: "set-view", class: "btn small" })),
      h("div", { class: "setting" }, h("label", null, "World theme"),
        h("button", { id: "set-thworld", class: "btn small" })),
      h("div", { class: "setting" }, h("label", null, "Player theme"),
        h("button", { id: "set-thplayers", class: "btn small" })),
      h("div", { class: "setting" }, h("label", null, "Enemy theme"),
        h("button", { id: "set-thenemies", class: "btn small" })),
      h("div", { class: "row buttons" }, h("button", { id: "btn-settings-done", class: "btn primary" }, "DONE")),
      h("p", { class: "dim tiny" }, "Floor brightness raises minimum light in deep waves without changing gameplay.")),

    // LEADERBOARDS
    h("section", { id: "screen-lb", class: "screen hidden" },
      h("h2", null, "\u{1F3C6} LEADERBOARDS"),
      h("div", { class: "row buttons" },
        h("button", { class: "btn small lb-tab", data: { mode: "run", period: "all" } }, "ALL TIME"),
        h("button", { class: "btn small lb-tab", data: { mode: "run", period: "week" } }, "THIS WEEK"),
        h("button", { class: "btn small lb-tab", data: { mode: "daily" } }, "DAILY DARK")),
      h("div", { id: "lb-body", class: "lb-body" }, "Loading…"),
      h("div", { class: "row buttons" }, h("button", { id: "btn-lb-done", class: "btn primary" }, "BACK"))),

    h("div", { id: "toast", class: "toast hidden" }),
    h("div", { id: "banner", class: "banner hidden" }));
}

const overlay = buildOverlay();
const elementsById = new Map();
(function index(element) {
  if (element.id) elementsById.set(element.id, element);
  for (const child of element.childNodes) if (child instanceof ScreenElement) index(child);
})(overlay);

// DarkShapes: document.getElementById, over index.html's elements.
function byId(id) {
  return elementsById.get(id) ?? null;
}

// DarkShapes: document.querySelectorAll(".class"), over the whole overlay.
function querySelectorAll(selector) {
  return overlay.querySelectorAll(selector);
}

// ---- styles.css ---------------------------------------------------------------------------

/** What body gives everything: styles.css's body rule over the user agent's. */
const BODY = Object.freeze({
  color: "#dfe8ff", fontSize: 16, weight: 400, family: "text", letterSpacing: 0, textAlign: "start",
  textShadow: null, lineHeight: 0, nowrap: false, textCase: "none", contentWidth: 0,
});

/** How each tag is laid out before a stylesheet says otherwise. */
const DISPLAY = { span: "inline", b: "inline", i: "inline", label: "inline", button: "inline-block", input: "inline-block", br: "br" };

/** The view in CSS pixels and the scale from them to canvas units: see the header. */
const view = { width: 0, height: 0, scale: 1, vw: 1280, vh: 720, narrow: false };

function measureView() {
  const width = Number(engineUi.width) || 1280;
  const height = Number(engineUi.height) || 720;
  view.width = width;
  view.height = height;
  view.scale = Math.max(1, width / 1280, height / 720);
  view.vw = width / view.scale;
  view.vh = height / view.scale;
  view.narrow = view.vw <= 640;
}

/** CSS pixels to canvas units, on a sixty-fourth. */
function px(value) {
  return Math.round(value * view.scale * 64) / 64;
}

/** Whether an ancestor carries every one of these classes. */
function within(element, ...names) {
  for (let up = element.parentElement; up; up = up.parentElement) {
    if (names.every((name) => up.classList.contains(name))) return true;
  }
  return false;
}

/**
 * An element's computed style: the properties it inherits, then every rule of styles.css that
 * matches it. Each declaration is weighed as the cascade weighs it -- its selector's specificity,
 * then where the rule stands in the stylesheet -- so the rules are applied by the element's own
 * classes rather than all tried in turn, and the order they are tried in decides nothing. The
 * comment on each block is its selector and its line in styles.css. Lengths are CSS pixels; ems are
 * resolved at the end.
 */
function computeStyle(element, parent) {
  const s = {
    display: DISPLAY[element.tagName] ?? "block",
    color: parent.color, fontSize: parent.fontSize, weight: parent.weight, family: parent.family,
    letterSpacing: parent.letterSpacing, textAlign: parent.textAlign, textShadow: parent.textShadow,
    lineHeight: parent.lineHeight, nowrap: parent.nowrap, textCase: parent.textCase,
    spacingEm: null, width: null, widthEm: null, height: null, maxWidth: 0, grow: 0,
    pt: 0, pr: 0, pb: 0, pl: 0, mt: 0, mb: 0, bw: 0, bc: null, radius: 0,
    background: null, gradient: null, shadow: null, outline: null, opacity: 1, lift: 0,
    position: null, clip: false, screen: false, fade: 0, passThrough: false, order: 0,
    justify: "start", align: "stretch", wrap: false, gap: 0, columns: 0, ruleTop: null, ruleBottom: null,
    contentWidth: 0,
  };
  const weights = {};
  // A declaration of a rule with this specificity (ids 100, classes and pseudo-classes 10, tags 1)
  // at this line of styles.css: the heavier wins, and of two alike the later line.
  let weight = 0;
  const rule = (specificity, line) => { weight = specificity * 1000 + line; };
  const set = (key, value) => {
    const held = weights[key];
    if (held !== undefined && weight < held) return;
    s[key] = value;
    weights[key] = weight;
  };
  const padding = (top, right, bottom, left) => { set("pt", top); set("pr", right); set("pb", bottom); set("pl", left); };
  const names = element.classList.names;
  const has = (name) => names.indexOf(name) >= 0;
  const tag = element.tagName;

  // The user agent's own rules, under every author rule: headings are larger and bold, a <b> is
  // bold, and a form control starts from a font of its own rather than its parent's.
  rule(0, 0);
  switch (tag) {
    case "h1": set("fontSize", parent.fontSize * 2); set("weight", 700); break;
    case "h2": set("fontSize", parent.fontSize * 1.5); set("weight", 700); set("family", "heading"); break;
    case "h3": set("fontSize", parent.fontSize * 1.17); set("weight", 700); set("family", "heading"); break;
    case "b": set("weight", 700); break;
    case "button":
    case "input":
      set("weight", 400); set("family", "text"); set("spacingEm", 0); set("textShadow", null);
      set("textCase", "none"); set("textAlign", tag === "button" ? "center" : "start");
      break;
    default: break;
  }

  // Rules on a tag.
  switch (tag) {
    case "h2": rule(1, 32); set("spacingEm", 0.12); set("mb", 12); break;   // h2
    case "h3": rule(1, 33); set("spacingEm", 0.1); set("mb", 10); set("color", "#39f0ff"); break;   // h3
    case "span": if (within(element, "logo")) { rule(11, 30); set("color", "#39f0ff"); } break;   // .logo span
    case "b":
      if (within(element, "grant")) { rule(11, 75); set("color", "#ffffff"); }   // .grant b
      if (within(element, "challenge")) { rule(11, 162); set("color", "#ffe45b"); }   // .challenge b
      break;
    case "i":
      if (within(element, "timerbar")) {   // .timerbar i
        // DarkShapes: the width's 0.2 s transition has no engine form; the fill moves each tick.
        rule(11, 96); set("display", "block"); set("height", "100%"); set("width", "100%"); set("gradient", [90, "#39f0ff", "#c26bfa"]);
      }
      break;
    case "label":
      if (within(element, "setting")) {   // .setting label
        rule(11, 170); set("display", "flex"); set("justify", "spaceBetween"); set("color", "#a9b8d8"); set("fontSize", 14.4); set("mb", 6);
      }
      break;
    case "input":
      rule(1, 40);   // input
      set("background", "#0d0722"); set("bw", 1); set("bc", "#2c3f66"); set("color", "#ffffff"); set("radius", 8);
      padding(10, 14, 10, 14); set("fontSize", 16.8); set("textAlign", "center"); set("spacingEm", 0.15);
      set("textCase", "upper"); set("width", 240);
      if (element.focus) { rule(11, 45); set("bc", "#39f0ff"); set("shadow", ["#39f0ff4d", 12]); }   // input:focus
      if (element.type === "range" && within(element, "setting")) { rule(21, 171); set("width", "100%"); }   // .setting input[type="range"]
      break;
    default: break;
  }
  if (element.id === "overlay") { rule(100, 14); set("display", "flex"); set("justify", "center"); set("align", "center"); }   // #overlay

  // Rules on a class, each under the class it names last.
  for (const name of names) {
    switch (name) {
      case "screen":
        rule(10, 15);   // .screen
        set("background", "#080414eb");   // rgba(8, 4, 20, 0.92)
        set("bw", 1); set("bc", "#39f0ff40");   // 1px solid rgba(57, 240, 255, 0.25)
        // DarkShapes: the box-shadow's outer glow is a shadow; its inset glow, 0 0 60px at 4%, has no engine form.
        set("shadow", ["#39f0ff1f", 40]);
        set("radius", 12); padding(28, 34, 28, 34); set("textAlign", "center");
        set("screen", true);   // max-width: min(92vw, 560px); max-height: 92vh; overflow-y: auto
        if (view.narrow) { rule(10, 182); padding(18, 14, 18, 14); }   // @media (max-width: 640px) .screen
        break;
      case "compact": if (has("screen")) { rule(20, 26); padding(16, 22, 16, 22); set("background", "#080414d9"); } break;   // .screen.compact
      case "hidden": rule(1e6, 27); set("display", "none"); break;   // .hidden { display: none !important }
      case "logo":
        rule(10, 29); set("fontSize", 41.6); set("spacingEm", 0.18); set("color", "#ffffff"); set("textShadow", ["#39f0ff", 18]);
        set("family", "display");
        if (view.narrow) { rule(10, 183); set("fontSize", 30.4); }   // @media (max-width: 640px) .logo
        break;
      case "tag": rule(10, 31); set("color", "#8fa3c8"); set("mt", 6); set("mb", 18); break;
      case "code":
        rule(10, 34); set("color", "#ffe45b"); set("family", "mono"); set("spacingEm", 0.2);
        set("textShadow", ["#ffe45bb3", 12]);   // rgba(255, 228, 91, 0.7)
        break;
      case "dim": rule(10, 35); set("color", "#66779c"); set("mt", 12); set("fontSize", 13.6); break;
      case "tiny": rule(10, 36); set("fontSize", 11.52); break;
      case "row": rule(10, 38); set("mt", 10); set("mb", 10); break;
      case "buttons": rule(10, 39); set("display", "flex"); set("gap", 10); set("justify", "center"); set("wrap", true); break;
      case "btn":
        rule(10, 47);   // .btn
        set("background", "#10082a"); set("color", "#dfe8ff"); set("bw", 1); set("bc", "#39f0ff55"); set("radius", 8);
        padding(12, 22, 12, 22); set("fontSize", 16); set("spacingEm", 0.08);
        // DarkShapes: a button's label keeps to one line, as every label here fits one; the
        // transitions on transform and box-shadow have no engine form, so a hover lands at once.
        set("nowrap", true);
        if (element.hover) { rule(20, 52); set("shadow", ["#39f0ff59", 16]); set("lift", -1); }   // .btn:hover
        if (element.active) { rule(20, 53); set("lift", 1); }   // .btn:active
        if (element.disabled) { rule(20, 59); set("opacity", 0.4); }   // .btn:disabled
        break;
      case "primary":
        if (has("btn")) {   // .btn.primary
          rule(20, 54); set("background", null); set("gradient", [160, "#0f4b57", "#0b2340"]); set("bc", "#39f0ff");
          set("color", "#ffffff"); set("textShadow", ["#39f0ff", 8]);
        }
        break;
      case "big":
        if (has("btn")) { rule(20, 55); set("fontSize", 18.4); padding(14, 30, 14, 30); }   // .btn.big
        if (within(element, "scorebody")) { rule(20, 99); set("fontSize", 32); set("color", "#ffe45b"); set("textShadow", ["#ffe45b80", 16]); }   // .scorebody .big
        break;
      case "invite":
        if (has("btn")) {
          rule(20, 56); set("bc", "#ffe45b88"); set("color", "#ffe45b");   // .btn.invite
          if (element.hover) { rule(30, 57); set("shadow", ["#ffe45b59", 16]); }   // .btn.invite:hover
        }
        break;
      case "bank": if (has("btn")) { rule(20, 58); set("bc", "#b8ff5e88"); set("color", "#b8ff5e"); } break;
      case "pilots": rule(10, 61); set("display", "flex"); set("gap", 8); set("justify", "center"); set("wrap", true); set("mt", 12); set("mb", 12); break;
      case "pilot": rule(10, 62); set("bw", 1); set("bc", "#2c3f66"); set("radius", 10); padding(10, 12, 10, 12); set("width", 116); set("background", "#0a0620"); break;
      case "nm":
        if (within(element, "pilot")) { rule(20, 66); set("weight", 700); set("spacingEm", 0.1); }   // .pilot .nm
        if (within(element, "card")) { rule(20, 90); set("weight", 700); set("mt", 4); set("mb", 4); }   // .card .nm
        break;
      case "ab": if (within(element, "pilot")) { rule(20, 67); set("fontSize", 10.88); set("color", "#8fa3c8"); set("mt", 4); } break;
      case "sel":
        if (has("pilot")) {   // .pilot.sel
          const c = element.style.getPropertyValue("--c") || "#39f0ff";
          rule(20, 68); set("bc", c);
          set("shadow", [c + "73", 14]);   // color-mix(in srgb, var(--c) 45%, transparent)
        }
        if (has("card")) { rule(20, 139); set("outline", [2, "#ffe45b"]); set("lift", -3); set("shadow", ["#ffe45b66", 18]); }   // .card.sel
        break;
      case "roster": rule(10, 70); set("display", "grid"); set("columns", 2); set("gap", 6); set("mt", 10); set("mb", 16); break;
      case "grant":
        rule(10, 71); set("bw", 1); set("bc", "#7a5cff88"); set("radius", 8); set("background", "#7a5cff14");
        set("color", "#b8a6ff"); padding(8, 12, 8, 12); set("mb", 10); set("fontSize", 13.6);
        break;
      case "slot":
        if (within(element, "roster")) {   // .roster .slot
          // DarkShapes: an engine border is solid; the open slot's dashed border is drawn solid.
          rule(20, 76); set("bw", 1); set("bc", "#2c3f66"); set("radius", 8); padding(8, 12, 8, 12);
          set("color", "#66779c"); set("display", "flex"); set("justify", "spaceBetween"); set("align", "center");
        }
        break;
      case "filled": if (has("slot") && within(element, "roster")) { rule(30, 80); set("color", "#dfe8ff"); } break;   // .roster .slot.filled
      case "cards": rule(10, 82); set("display", "flex"); set("gap", 10); set("justify", "center"); set("wrap", true); set("mt", 8); set("mb", 12); break;
      case "card":
        rule(10, 83); set("width", 150); set("textAlign", "start"); set("bw", 1); set("bc", "#2c3f66"); set("radius", 10);
        set("background", "#0a0620"); padding(12, 12, 12, 12);
        if (element.hover) { rule(20, 87); set("lift", -3); set("shadow", ["#39f0ff4d", 18]); set("bc", "#39f0ff"); }   // .card:hover
        if (view.narrow) { rule(10, 184); set("width", 128); }   // @media (max-width: 640px) .card
        break;
      case "picked": if (has("card")) { rule(20, 88); set("outline", [2, "#b8ff5e"]); } break;   // .card.picked
      case "fam":
        if (within(element, "card")) { rule(20, 89); set("fontSize", 9.92); set("spacingEm", 0.14); set("color", "#66779c"); }   // .card .fam
        if (within(element, "card", "cursed")) { rule(30, 93.5); set("color", "#ff4d4d"); }   // .card.cursed .fam
        break;
      case "ds": if (within(element, "card")) { rule(20, 91); set("fontSize", 12.48); set("color", "#a9b8d8"); } break;
      case "r2":
        if (has("card")) {
          rule(20, 92); set("bc", "#c26bfa66");   // .card.r2
          if (element.hover) { rule(30, 92.5); set("bc", "#c26bfa"); set("shadow", ["#c26bfa59", 18]); }   // .card.r2:hover
        }
        break;
      case "cursed": if (has("card")) { rule(20, 93); set("bc", "#ff4d4d88"); } break;   // .card.cursed
      case "timerbar":
        rule(10, 95); set("height", 6); set("background", "#131033"); set("radius", 3); set("clip", true);
        // DarkShapes: the bar is as wide as the screen's content box, fixed, so a tick lays out the bar alone.
        set("width", parent.contentWidth || null);
        break;
      case "scorebody": rule(10, 98); set("fontSize", 16.8); set("lineHeight", 1.9); set("mt", 8); set("mb", 8); break;
      case "lost": if (within(element, "scorebody")) { rule(20, 100); set("color", "#ff5b6e"); } break;
      case "toast":
        rule(10, 102); set("position", "toast"); set("background", "#080414f2"); set("bw", 1); set("bc", "#39f0ff66");
        set("radius", 8); padding(10, 18, 10, 18); set("passThrough", true); set("fade", 0.3); set("order", 40);
        break;
      case "banner":
        rule(10, 107); set("position", "banner"); set("fontSize", 35.2); set("weight", 800); set("spacingEm", 0.25);
        set("color", "#ffffff"); set("textShadow", ["#39f0ff", 24]); set("passThrough", true); set("order", 30); set("nowrap", true);
        break;
      case "warn": if (has("banner")) { rule(20, 112); set("color", "#ff5b6e"); set("textShadow", ["#ff5b6e", 24]); } break;
      case "small":
        if (has("btn")) { rule(20, 134); padding(8, 14, 8, 14); set("fontSize", 13.6); }   // .btn.small
        if (has("grant")) { rule(20, 140); set("fontSize", 12); set("color", "#b8a6ff"); set("textAlign", "start"); set("mb", 6); }   // .grant.small
        if (has("card") && has("shopcard")) { rule(30, 151); set("width", 118); set("fontSize", parent.fontSize * 0.85); }   // .card.shopcard.small
        break;
      case "draft-seat": rule(10, 137); set("ruleTop", [1, "#1a1440"]); set("mt", 10); set("pt", 8); break;
      case "seat-label": rule(10, 138); set("fontSize", 12.8); set("spacingEm", 0.08); set("textAlign", "start"); set("mb", 6); break;
      case "couch-hint": rule(10, 141); set("color", "#66779c"); set("fontSize", 12.8); set("mt", 10); break;
      case "shopgrid": rule(10, 144); set("maxWidth", 560); break;
      case "shopcard": if (has("card")) { rule(20, 145); set("pb", 26); } break;   // .card.shopcard
      case "price":
        if (within(element, "card", "shopcard")) {   // .card.shopcard .price
          rule(30, 146); set("position", "price"); set("color", "#ffe45b"); set("weight", 700); set("fontSize", 13.6);
          if (within(element, "card", "shopcard", "owned")) { rule(40, 148); set("color", "#b8ff5e"); }   // .card.shopcard.owned .price
        }
        break;
      case "owned": if (has("card") && has("shopcard")) { rule(30, 147); set("opacity", 0.35); } break;
      case "poor":
        if (has("card") && has("shopcard")) {
          rule(30, 149); set("opacity", 0.55);   // .card.shopcard.poor
          if (element.hover) { rule(40, 150); set("lift", 0); set("shadow", null); set("bc", "#2c3f66"); }   // .card.shopcard.poor:hover
        }
        break;
      case "ready": if (has("card") && has("shopcard")) { rule(30, 152); set("bc", "#b8ff5e88"); } break;
      case "stacks": if (within(element, "card", "shopcard")) { rule(30, 153); set("color", "#b8ff5e"); set("fontSize", parent.fontSize * 0.8); } break;
      case "shoptab": if (has("btn")) { rule(20, 154); set("bc", "#ffe45b88"); set("color", "#ffe45b"); } break;
      case "challenge":
        rule(10, 157); set("bw", 1); set("bc", "#ff5b6e88"); set("radius", 10); padding(12, 16, 12, 16); set("mt", 10); set("mb", 10);
        set("background", "#ff5b6e12"); set("color", "#ffd7dc"); set("fontSize", 15.2); set("shadow", ["#ff5b6e26", 18]);
        break;
      case "challenge-btn":
        if (has("btn")) {
          rule(20, 163); set("bc", "#ff5b6e88"); set("color", "#ff9aa6");   // .btn.challenge-btn
          if (element.hover) { rule(30, 164); set("shadow", ["#ff5b6e66", 16]); }   // .btn.challenge-btn:hover
        }
        break;
      case "daily":
        if (has("btn")) {
          rule(20, 165); set("bc", "#7a5cff88"); set("color", "#b8a6ff");   // .btn.daily
          if (element.hover) { rule(30, 166); set("shadow", ["#7a5cff66", 16]); }   // .btn.daily:hover
        }
        break;
      case "active": if (has("lb-tab")) { rule(20, 167); set("bc", "#ffe45b"); set("color", "#ffe45b"); } break;   // .lb-tab.active
      case "setting": rule(10, 169); set("mt", 14); set("mb", 14); set("textAlign", "start"); break;
      case "lb-body": rule(10, 173); set("mt", 10); set("mb", 10); set("family", "mono"); set("fontSize", 13.6); break;
      case "lb-row":
        rule(10, 174); set("display", "flex"); set("gap", 10); padding(6, 8, 6, 8); set("ruleBottom", [1, "#1a1440"]);
        set("align", "center");   // align-items: baseline: one font, one size, so centred is the same line
        break;
      case "rank":
      case "score":
        if (within(element, "lb-row")) {
          rule(20, name === "rank" ? 175 : 176);   // .lb-row .rank, .lb-row .score
          set("color", name === "rank" ? "#66779c" : "#ffe45b"); set("widthEm", name === "rank" ? 2.2 : 7); set("textAlign", "end");
          if (firstRow(element)) { rule(30, 179); set("color", "#ffffff"); set("textShadow", ["#ffe45b", 10]); }   // .lb-row:first-child .rank, .score
        }
        break;
      case "who":
        if (within(element, "lb-row")) {   // .lb-row .who
          // DarkShapes: text-overflow: ellipsis has no engine form; a long name is cut at the column's edge.
          rule(20, 177); set("grow", 1); set("textAlign", "start"); set("clip", true); set("nowrap", true);
        }
        break;
      case "wave": if (within(element, "lb-row")) { rule(20, 178); set("color", "#8fa3c8"); } break;
      default: break;
    }
  }

  // The inline style attribute outweighs every rule.
  rule(1e5, 0);
  const inline = element.style.values;
  if (inline.opacity !== undefined && inline.opacity !== "" && Number.isFinite(Number(inline.opacity))) set("opacity", Number(inline.opacity));
  if (inline.color) set("color", inline.color);
  if (inline.width !== undefined && inline.width !== "") set("width", inline.width);

  if (s.spacingEm !== null) s.letterSpacing = s.spacingEm * s.fontSize;
  if (s.widthEm !== null) s.width = s.widthEm * s.fontSize;
  if (tag === "input") {
    // An input's content box is one line of its font, or a slider's sixteen pixels.
    const line = element.type === "range" ? 16 : s.fontSize * LINE_HEIGHT[faceOf(s)];
    s.height = line + s.pt + s.pb + 2 * s.bw;
  }
  if (s.screen) {
    // DarkShapes: max-width: min(92vw, 560px) is the screen's width. The engine sizes a box to its
    // content without wrapping that content to a maximum first, so a screen is always this wide.
    s.width = Math.min(view.vw * 0.92, 560);
  }
  s.contentWidth = typeof s.width === "number" ? Math.max(0, s.width - s.pl - s.pr - 2 * s.bw) : 0;
  return s;
}

/** Whether an element's parent is the first .lb-row among its siblings: .lb-row:first-child. */
function firstRow(element) {
  const row = element.parentElement;
  if (!row || !row.classList.contains("lb-row") || !row.parentElement) return false;
  return row.parentElement.firstElementChild === row;
}

/** The engine face a computed style draws its text in. */
// UltraDark-sb: a web build ships only the engine fonts a project names WHOLE somewhere the exporter
// reads (html5/tools/export.js, fontsUsedBy): a `font: "<name>"` in a shipped file. The faces below are
// the ones styles.css's font-families resolve to in faceOf, named so the export carries them; without
// this the live site drew every screen in the bitmap font while the editor, which serves every font,
// looked right.
const SHIPPED_FONTS = [{ font: "display" }, { font: "heading" }, { font: "body" }, { font: "body-strong" }, { font: "mono" }, { font: "mono-bold" }];
void SHIPPED_FONTS;

function faceOf(style) {
  if (style.family === "mono") return style.weight >= 600 ? "mono-bold" : "mono";
  if (style.family === "display" || style.family === "heading") return style.family;
  return style.weight >= 600 ? "body-strong" : "body";
}

/** The properties that decide how an element's text is drawn: a change to any redraws its content. */
function textKey(style) {
  return [style.color, style.fontSize, style.weight, style.family, style.letterSpacing, style.textAlign,
    style.textShadow ? style.textShadow.join(" ") : "", style.lineHeight, style.nowrap, style.textCase,
    style.contentWidth].join("|");
}

// ---- drawing ------------------------------------------------------------------------------

/** The live elements poll reads: buttons, pilots, cards and inputs. */
const controls = [];

/** Engine alignments by CSS keyword. */
const ALIGN = { start: "start", left: "start", center: "center", end: "end", right: "end", stretch: "stretch", spaceBetween: "spaceBetween" };

/** Whether text holds a character outside printable Latin-1. */
const LATIN1 = /[^\x20-\x7e\xa0-\xff]/;

/** The line height of each face, in ems: what CSS's line-height: normal comes to in the engine. */
const LINE_HEIGHT = { display: 1.205, heading: 1.2, body: 1.2, "body-strong": 1.2, mono: 1.164, "mono-bold": 1.164 };

function classText(element) {
  return element.classList.names.filter((name) => name !== "hidden").join(" ");
}

function isControl(element) {
  return element.tagName === "button" || element.tagName === "input"
    || element.classList.contains("pilot") || element.classList.contains("card");
}

/** The node an element is drawn as: its box, what is in it, and what is drawn round it. */
function nodeSpec(element, parentStyle, computed) {
  const style = computed ?? computeStyle(element, parentStyle);
  element.computed = style;
  element.live = true;
  element.node = null;
  element.content = null;
  element.parts = null;
  element.fade = null;
  if (isControl(element) && controls.indexOf(element) < 0) {
    controls.push(element);
    let up = element.parentElement;
    while (up && up.tagName !== "section") up = up.parentElement;
    element.screen = up ? up.id : "";
  }

  if (element.tagName === "input") {
    element.flow = [];
    element.outer = [style.mt, style.mb];
    element.empty = false;
    const look = lookOf(element, style);
    element.look = look;
    return element.type === "range" ? rangeSpec(element, look) : fieldSpec(element, style, look);
  }

  // A hidden element is drawn as its box with nothing in it, and filled the first time it is shown:
  // a script's call has a statement cap natively, and the screens nobody is looking at are most of them.
  element.built = style.display !== "none";
  const content = element.built ? contentSpec(element, style) : { width: "*", height: "*" };
  if (!element.built) {
    element.flow = [];
    element.outer = [style.mt, style.mb];
    element.empty = false;
  }
  const look = lookOf(element, style);
  element.look = look;
  const spec = Object.assign({}, look);
  spec.children = [content].concat(decorations(element, style));
  return spec;
}

/** The engine properties of an element's own box, as a flat object a look update compares. */
function lookOf(element, style) {
  const look = {};
  if (element.id) look.name = element.id;
  look.style = classText(element);
  look.visible = style.display !== "none";
  if (isControl(element) && element.tagName !== "input") {
    look.interactive = element.tagName === "button" ? !element.disabled : Boolean(element.onclick);
  }
  look.opacity = style.opacity;
  // DarkShapes: pointer-events: none. A node with a background takes the pointer, so a box that
  // lets it through paints its background as a hard shadow the size of the box instead.
  look.background = style.passThrough ? null : style.background;
  look.gradient = style.gradient ? { kind: "linear", angle: style.gradient[0], stops: style.gradient.slice(1) } : null;
  look.borderColour = style.bw > 0 ? style.bc : null;
  look.borderWidth = style.bw > 0 ? px(style.bw) : 0;
  look.cornerRadius = px(style.radius);
  if (style.passThrough && style.background) {
    look.shadow = style.background;
    look.shadowBlur = 0;
  } else {
    look.shadow = style.shadow ? style.shadow[0] : null;
    look.shadowBlur = style.shadow ? px(style.shadow[1]) : 0;
  }
  look.shadowOffset = [0, 0];
  const ruleTop = style.ruleTop ? style.ruleTop[0] : 0;
  const ruleBottom = style.ruleBottom ? style.ruleBottom[0] : 0;
  look.padding = [px(style.pl + style.bw), px(style.pt + style.bw + ruleTop), px(style.pr + style.bw), px(style.pb + style.bw + ruleBottom)];
  look.margin = marginOf(element, style);
  look.width = sizeOf(style.width);
  look.height = sizeOf(style.height);
  look.grow = style.grow;
  look.clip = style.clip;
  look.order = style.order;
  if (style.screen) {
    look.maxHeight = px(view.vh * 0.92);
    look.scroll = "vertical";
  }
  if (style.position === "toast") {
    look.absolute = true;
    look.anchor = "bottom";
    look.offset = [0, -px(view.vh * 0.08)];
  } else if (style.position === "banner") {
    look.absolute = true;
    look.anchor = "top";
    look.offset = [0, px(view.vh * 0.18)];
  } else if (style.position === "price") {
    // bottom: 8px; left: 12px of the card's padding box, from its content box.
    const card = element.parentElement.computed;
    look.absolute = true;
    look.anchor = "bottomleft";
    look.offset = [px(12 - card.pl), px(card.pb - 8)];
  }
  return look;
}

function sizeOf(value) {
  if (value === null || value === undefined) return "auto";
  if (typeof value === "number") return px(value);
  return value;
}

/** What is drawn round a box: a card's outline, and a one-sided border as a line of its own. */
function decorations(element, style) {
  const parts = [];
  const inset = (side) => px(side + style.bw);
  if (element.classList.contains("card")) {
    // DarkShapes: an outline is a ring round the border box, outside it as CSS draws one.
    const width = style.outline ? style.outline[0] : 2;
    parts.push({
      absolute: true, anchorMin: [0, 0], anchorMax: [1, 1], pivot: [0, 0],
      offset: [-(inset(style.pl) + px(width)), -(inset(style.pt) + px(width))],
      offsetMax: [inset(style.pr) + px(width), inset(style.pb) + px(width)],
      borderColour: style.outline ? style.outline[1] : null, borderWidth: px(width),
      cornerRadius: px(style.radius + width), visible: Boolean(style.outline),
    });
  }
  if (style.ruleTop) {
    parts.push({
      absolute: true, anchorMin: [0, 0], anchorMax: [1, 0], pivot: [0, 0],
      offset: [-inset(style.pl), -(inset(style.pt) + px(style.ruleTop[0]))], offsetMax: [inset(style.pr), 0],
      height: px(style.ruleTop[0]), background: style.ruleTop[1],
    });
  }
  if (style.ruleBottom) {
    parts.push({
      absolute: true, anchorMin: [0, 1], anchorMax: [1, 1], pivot: [0, 1],
      offset: [-inset(style.pl), inset(style.pb) + px(style.ruleBottom[0])], offsetMax: [inset(style.pr), 0],
      height: px(style.ruleBottom[0]), background: style.ruleBottom[1],
    });
  }
  return parts;
}

/** The node under an element's box that holds everything in it, laid out as its display says. */
function contentSpec(element, style) {
  const spec = { width: "*", height: "*" };
  if (style.display === "flex" || style.display === "grid") {
    if (style.display === "grid") {
      spec.layout = "grid";
      spec.columns = style.columns;
    } else {
      spec.layout = style.wrap ? "flow" : "row";
      spec.mainAlign = ALIGN[style.justify];
      spec.crossAlign = ALIGN[style.align];
    }
    if (style.gap) spec.gap = [px(style.gap), px(style.gap)];
    if (style.maxWidth) spec.maxWidth = px(style.maxWidth);
    spec.children = flexItems(element, style);
    element.outer = [style.mt, style.mb];
    element.empty = false;
    return spec;
  }
  spec.layout = "column";
  spec.crossAlign = "stretch";
  spec.children = blockContent(element, style);
  return spec;
}

/** A flex or grid container's items: each child element, and each run of text as an item of its own. */
function flexItems(element, style) {
  const items = [];
  element.flow = [];
  for (const child of element.childNodes) {
    if (child instanceof ScreenElement) {
      const spec = nodeSpec(child, style);
      // A flex item is a box, whatever its tag would make it.
      if (child.computed.display === "inline" || child.computed.display === "inline-block") child.computed.display = "block";
      child.margins = null;
      child.path = [items.length];
      items.push(spec);
      continue;
    }
    const index = items.length;
    const lines = lineSpecs([{ text: child.nodeValue, style }], style, (n) => [index, n]);
    if (lines.length === 0) continue;
    items.push({ layout: "column", crossAlign: "stretch", children: lines });
  }
  return items;
}

/**
 * A block container's content: its block children as boxes, and the inline content between them
 * as lines, with the margins between the boxes collapsed.
 */
function blockContent(element, style) {
  const specs = [];
  const flow = [];
  let inline = [];

  const flushLines = () => {
    if (inline.length === 0) return;
    const base = specs.length;
    const lines = lineSpecs(inline, style, (n) => [base + n]);
    inline = [];
    for (const line of lines) {
      flow.push(null);
      specs.push(line);
    }
  };

  const collect = (node, nodeStyle) => {
    if (node instanceof ScreenText) {
      inline.push({ text: node.nodeValue, style: nodeStyle });
      return;
    }
    if (node.tagName === "br") {
      inline.push({ br: true });
      return;
    }
    const childStyle = computeStyle(node, nodeStyle);
    const level = childStyle.display === "none" ? (DISPLAY[node.tagName] ?? "block") : childStyle.display;
    if (level === "inline") {
      node.computed = childStyle;
      node.live = true;
      for (const grandchild of node.childNodes) collect(grandchild, childStyle);
      return;
    }
    if (level === "inline-block") {
      inline.push({ element: node, style: nodeStyle, computed: childStyle });
      return;
    }
    flushLines();
    const spec = nodeSpec(node, nodeStyle, childStyle);
    node.path = [specs.length];
    flow.push(node);
    specs.push(spec);
  };

  for (const child of element.childNodes) collect(child, style);
  flushLines();

  element.flow = flow;
  collapse(element, style, specs);
  return specs;
}

/** Whether a block's top or bottom margin meets its first or last child's: no padding or border between. */
function opensTop(element, style) {
  return !style.screen && style.display === "block" && style.pt === 0 && style.bw === 0 && !style.ruleTop && !isControl(element);
}

function opensBottom(element, style) {
  return !style.screen && style.display === "block" && style.pb === 0 && style.bw === 0 && !style.ruleBottom && !isControl(element);
}

/**
 * Collapses the vertical margins of a block's children as CSS does: adjacent margins become the
 * larger, an empty block's margins fall through it, and a first or last child's margin passes out
 * through a parent with nothing between them. Writes the margins each child is drawn with, and the
 * block's own outer margins.
 */
function collapse(element, style, specs) {
  const items = element.flow;
  const margins = new Array(items.length);
  let pending = [];
  let first = -1;
  let last = -1;
  let outerTop = style.mt;

  for (let i = 0; i < items.length; i++) {
    const child = items[i];
    margins[i] = [0, 0];
    if (child !== null && (child.computed.display === "none" || child.computed.position)) continue;
    if (child !== null && child.empty) {
      pending.push(child.outer[0], child.outer[1]);
      continue;
    }
    pending.push(child === null ? 0 : child.outer[0]);
    const between = Math.max(0, ...pending);
    if (first < 0 && opensTop(element, style)) outerTop = Math.max(outerTop, between);
    else margins[i][0] = between;
    if (first < 0) first = i;
    last = i;
    pending = [child === null ? 0 : child.outer[1]];
  }

  let outerBottom = style.mb;
  if (last < 0) {
    element.empty = opensTop(element, style) && opensBottom(element, style) && style.height === null;
    element.outer = element.empty ? [Math.max(style.mt, style.mb, ...pending), 0] : [style.mt, style.mb];
  } else {
    const trailing = Math.max(0, ...pending);
    if (opensBottom(element, style)) outerBottom = Math.max(outerBottom, trailing);
    else margins[last][1] = trailing;
    element.empty = false;
    element.outer = [outerTop, outerBottom];
  }

  for (let i = 0; i < items.length; i++) {
    const child = items[i];
    if (child === null) continue;
    child.margins = margins[i];
    if (specs) {
      const margin = marginOf(child, child.computed);
      specs[i].margin = margin;
      if (child.look) child.look.margin = margin;
    }
  }
}

/** The engine margin of an element's box: its collapsed margins, moved by a transform's lift. */
function marginOf(element, style) {
  const margins = element.margins ?? [style.mt, style.mb];
  return [0, px(margins[0] + style.lift), 0, px(margins[1] - style.lift)];
}

/**
 * Inline content as lines: split at <br>, white space collapsed as CSS collapses it, each run cut
 * into what a face draws. A line that cannot wrap, or is one run aligned to the start, is a label or
 * a row of them; any other line is a wrapping row of word chips, so each wrapped line is aligned.
 */
function lineSpecs(pieces, blockStyle, pathOf) {
  const lines = [];
  let line = [];
  const flush = () => {
    const spec = lineSpec(line, blockStyle, pathOf(lines.length));
    if (spec) lines.push(spec);
    line = [];
  };
  for (const piece of pieces) {
    if (piece.br) flush();
    else line.push(piece);
  }
  flush();
  return lines;
}

function lineSpec(pieces, blockStyle, linePath) {
  // White space: runs of it are one space, none at either end of the line.
  const segments = [];
  let spaceBefore = true;
  for (const piece of pieces) {
    if (piece.element) {
      segments.push(piece);
      spaceBefore = false;
      continue;
    }
    let text = piece.text.replace(/[ \t\n\r\f]+/g, " ");
    if (spaceBefore && text.startsWith(" ")) text = text.slice(1);
    if (text === "") continue;
    spaceBefore = text.endsWith(" ");
    segments.push({ text, style: piece.style });
  }
  while (segments.length > 0) {
    const tail = segments[segments.length - 1];
    if (tail.element || !tail.text.endsWith(" ")) break;
    tail.text = tail.text.slice(0, -1);
    if (tail.text === "") segments.pop();
  }
  if (segments.length === 0) return null;

  const align = ALIGN[blockStyle.textAlign] ?? "start";
  const hasElement = segments.some((segment) => segment.element);
  const faceRuns = [];
  for (const segment of segments) {
    if (segment.element) {
      faceRuns.push(segment);
      continue;
    }
    const face = faceOf(segment.style);
    // Latin-1 is drawn by every face as it is written, which is what runs() finds for it, a
    // character at a time; only text with anything past it is cut there.
    if (!LATIN1.test(segment.text)) faceRuns.push({ text: segment.text, face, style: segment.style });
    else for (const run of runs(segment.text, face)) faceRuns.push({ text: run.text, face: run.face, style: segment.style });
  }

  const lineHeight = blockStyle.lineHeight;
  const leading = (style, face) => (lineHeight > 0 ? Math.max(0, lineHeight - LINE_HEIGHT[face]) * style.fontSize : 0);

  if (!hasElement && faceRuns.length === 1 && (blockStyle.nowrap || align === "start")) {
    const run = faceRuns[0];
    const label = labelSpec(run.text, run.face, run.style);
    label.textAlign = align;
    if (!blockStyle.nowrap) label.wrapText = true;
    const lead = leading(run.style, run.face);
    if (lead > 0) {
      label.lineSpacing = px(lead);
      label.padding = [0, px(lead / 2), 0, px(lead / 2)];
    }
    return label;
  }

  if (blockStyle.nowrap && !hasElement) {
    return { layout: "row", mainAlign: align, crossAlign: "center", children: faceRuns.map((run) => labelSpec(run.text, run.face, run.style)) };
  }

  // Word chips: each word keeps the space after it, and a word that changes face stays whole.
  const chips = [];
  let chip = null;
  const place = (text, run) => {
    if (chip === null) chip = [];
    const tail = chip.length > 0 ? chip[chip.length - 1] : null;
    if (tail && !tail.element && tail.face === run.face && tail.style === run.style) tail.text += text;
    else chip.push({ text, face: run.face, style: run.style });
  };
  for (const run of faceRuns) {
    if (run.element) {
      if (chip !== null) chips.push(chip);
      chips.push([run]);
      chip = null;
      continue;
    }
    const words = run.text.split(" ");
    for (let i = 0; i < words.length; i++) {
      if (i > 0 && chip !== null) {
        place(" ", run);
        chips.push(chip);
        chip = null;
      }
      if (words[i] !== "") place(words[i], run);
    }
  }
  if (chip !== null) chips.push(chip);

  const children = chips.map((parts, index) => {
    if (parts.length === 1 && parts[0].element) {
      // An inline-block control sits in the line as one of its items.
      const element = parts[0].element;
      const spec = nodeSpec(element, parts[0].style, parts[0].computed);
      element.path = linePath.concat([index]);
      element.margins = null;
      return spec;
    }
    if (parts.length === 1) return labelSpec(parts[0].text, parts[0].face, parts[0].style);
    return { layout: "row", crossAlign: "center", children: parts.map((part) => labelSpec(part.text, part.face, part.style)) };
  });
  const flowSpec = { layout: "flow", mainAlign: align, crossAlign: "center", children };
  const first = faceRuns.find((run) => !run.element);
  const lead = first ? leading(first.style, first.face) : 0;
  if (lead > 0) {
    flowSpec.gap = [0, px(lead)];
    flowSpec.padding = [0, px(lead / 2), 0, px(lead / 2)];
  }
  return flowSpec;
}

/** One run of text in one face: a label drawn in the run's colour, size, tracking and glow. */
function labelSpec(text, face, style) {
  const spec = { kind: "label", text, font: face, fontSize: px(style.fontSize), tint: style.color };
  if (style.letterSpacing) {
    // CSS tracks after every glyph, the engine between them: the last glyph's is a margin.
    spec.letterSpacing = px(style.letterSpacing);
    spec.margin = [0, 0, px(style.letterSpacing), 0];
  }
  if (style.textShadow) {
    // DarkShapes: text-shadow 0 0 <blur> is a glow, which a face's field reaches only about a
    // tenth of an em past the glyphs (wiki 9, Fonts); a wide CSS halo is drawn at that reach.
    spec.textGlow = style.textShadow[0];
    spec.textGlowWidth = px(style.textShadow[1]);
  }
  if (style.textCase === "upper") spec.textCase = "upper";
  return spec;
}

/** The callsign field: a TextField, with the placeholder the engine's field does not have. */
function fieldSpec(element, style, look) {
  const face = faceOf(style);
  const spec = Object.assign({}, look);
  spec.kind = "textfield";
  spec.text = element.rawValue;
  spec.font = face;
  spec.fontSize = px(style.fontSize);
  spec.letterSpacing = px(style.letterSpacing);
  spec.textCase = "upper";
  spec.tint = style.color;
  // DarkShapes: the engine's field has no placeholder and no maxlength, and draws what is typed from
  // its start where the input centred it. The placeholder is a centred label over the field while it
  // is empty, and poll keeps the text to the maxlength.
  spec.children = [{
    kind: "label", text: element.placeholder, absolute: true, anchorMin: [0, 0], anchorMax: [1, 1], pivot: [0, 0],
    font: face, fontSize: px(style.fontSize), letterSpacing: px(style.letterSpacing), textCase: "upper",
    tint: "#757575", textAlign: "center", visible: element.rawValue === "",
  }];
  return spec;
}

/**
 * A range input: the input's own box, which styles.css's input rule dresses as it dresses the
 * callsign, with the engine's slider across its content box in the accent colour.
 */
function rangeSpec(element, look) {
  // What the slider shows, so poll hears only a value the player moved it to.
  element.shownValue = Number(element.value);
  const spec = Object.assign({}, look);
  spec.children = [{
    kind: "slider", width: "*", height: "*", minValue: Number(element.min), maxValue: Number(element.max),
    step: Number(element.step), value: Number(element.value),
    background: "#efefef", tint: "#39f0ff",   // accent-color: #39f0ff over the browser's light track
  }];
  return spec;
}

// ---- keeping the nodes in step with the model --------------------------------------------

/** The handle of an element's node, found the first time it is needed. */
function handleOf(element) {
  if (element.node) return element.node;
  const parent = element.parentElement;
  let handle = contentOf(parent);
  for (const index of element.path) handle = handle.children[index];
  element.node = handle;
  return handle;
}

/** The handle of the node an element's content hangs under. */
function contentOf(element) {
  if (element.content) return element.content;
  if (element.computed.display === "inline") return contentOf(element.parentElement);
  element.content = handleOf(element).children[0];
  return element.content;
}

/** Whether an element and everything round it is drawn. */
function shown(element) {
  for (let up = element; up; up = up.parentElement) {
    if (!up.live || !up.computed || up.computed.display === "none") return false;
  }
  return true;
}

/** Marks a subtree gone from the engine: its handles are stale, and poll forgets its controls. */
function retire(element) {
  element.live = false;
  element.node = null;
  element.content = null;
  element.parts = null;
  for (const child of element.childNodes) if (child instanceof ScreenElement) retire(child);
}

/** The nearest element drawn as a box of its own: an inline element's text is its block's. */
function boxOf(element) {
  let box = element;
  while (box && box.computed && box.computed.display === "inline") box = box.parentElement;
  return box;
}

function parentStyleOf(element) {
  return element.parentElement ? element.parentElement.computed : BODY;
}

/** A class, the inline style, a pointer state or a handler changed: the box is drawn again. */
function lookChanged(element, visibility) {
  if (!engineUi || !element.live) return;
  const box = boxOf(element);
  if (box !== element) {
    contentChanged(box);
    return;
  }
  const style = computeStyle(element, parentStyleOf(element));
  const before = element.computed;
  element.computed = style;
  const unfilled = style.display !== "none" && !element.built;
  if (element.tagName !== "input" && (unfilled || textKey(before) !== textKey(style))) {
    rebuildContent(element);
  }
  if (before.display !== style.display && style.display !== "none") element.shownAt = clock ? clock.now() : 0;
  writeLook(element, style);
  if (visibility || before.display !== style.display) reflow(element.parentElement);
}

/** Writes what changed in an element's box to its node, and its outline. */
function writeLook(element, style) {
  const look = lookOf(element, style);
  const before = element.look ?? {};
  element.look = look;
  const handle = handleOf(element);
  for (const key of Object.keys(look)) {
    if (same(look[key], before[key])) continue;
    if (key === "opacity" && style.fade > 0 && before.visible && look.visible && element.shownAt !== (clock ? clock.now() : 0)) {
      if (element.fade) element.fade.cancel();
      element.fade = engineUi.tween(handle, { opacity: look.opacity }, style.fade, "outQuad");
      continue;
    }
    if (key === "opacity" && element.fade) {
      element.fade.cancel();
      element.fade = null;
    }
    handle[key] = look[key];
  }
  if (element.classList.contains("card")) {
    const ring = partOf(element, 0);
    ring.visible = Boolean(style.outline);
    if (style.outline) ring.borderColour = style.outline[1];
  }
}

/** A decoration's handle: the parts follow the content node under the box. */
function partOf(element, index) {
  if (!element.parts) element.parts = handleOf(element).children.filter((child) => child !== contentOf(element));
  return element.parts[index];
}

function same(a, b) {
  if (a === b) return true;
  if (a === null || b === null || typeof a !== "object" || typeof b !== "object") return false;
  return JSON.stringify(a) === JSON.stringify(b);
}

/** The element's children or text changed: what is in its box is built again. */
function contentChanged(element) {
  if (!engineUi || !element.live) return;
  const box = boxOf(element);
  if (box !== element) {
    contentChanged(box);
    return;
  }
  const outer = element.outer.join(",") + element.empty;
  rebuildContent(element);
  if (element.outer.join(",") + element.empty !== outer) reflow(element.parentElement);
}

function rebuildContent(element) {
  if (element.computed.display === "none" && !element.built) return;
  element.built = true;
  const old = contentOf(element);
  for (const child of element.childNodes) if (child instanceof ScreenElement) retire(child);
  if (element.parts === null && element.classList.contains("card")) partOf(element, 0);
  old.remove();
  element.content = handleOf(element).add(contentSpec(element, element.computed));
  if (element.computed.display === "block") collapseLive(element);
}

/** A child was appended to a live element: its node is added under the content, and margins settle. */
function childAppended(element, child) {
  if (!engineUi || !element.live || !boxOf(element).built) return;
  const box = boxOf(element);
  if (box !== element || element.computed.display === "block") {
    contentChanged(box);
    return;
  }
  const spec = nodeSpec(child, element.computed);
  if (child.computed.display === "inline" || child.computed.display === "inline-block") child.computed.display = "block";
  child.margins = null;
  child.path = null;
  child.node = contentOf(element).add(spec);
  element.flow.push(child);
}

/** A block's children changed how they stack: their margins collapse again. */
function reflow(element) {
  if (!element || !element.live || element.computed.display !== "block") return;
  const outer = element.outer.join(",") + element.empty;
  collapseLive(element);
  if (element.outer.join(",") + element.empty !== outer) reflow(element.parentElement);
}

function collapseLive(element) {
  collapse(element, element.computed, null);
  for (const child of element.flow) {
    if (child === null || !child.live) continue;
    const margin = marginOf(child, child.computed);
    if (child.look && !same(margin, child.look.margin)) {
      handleOf(child).margin = margin;
      child.look.margin = margin;
    }
  }
}

/** An input's value was written: the field's text, or the slider's value. */
function valueChanged(element) {
  if (!engineUi || !element.live) return;
  if (element.type === "range") {
    contentOf(element).value = Number(element.value);
    element.shownValue = Number(element.value);
  } else {
    handleOf(element).text = element.rawValue;
    placeholderOf(element).visible = element.rawValue === "";
  }
}

/** The label over an empty callsign field. */
function placeholderOf(element) {
  if (!element.parts) element.parts = handleOf(element).children;
  return element.parts[0];
}

/** Draws the whole overlay from the model: at start, and whenever the view changes size. */
function render() {
  measureView();
  if (overlay.node) overlay.node.remove();
  retire(overlay);
  controls.length = 0;
  const spec = nodeSpec(overlay, BODY);
  // #overlay { position: fixed; inset: 0 }
  spec.width = "*";
  spec.height = "*";
  overlay.node = engineUi.root.add(spec);
  overlay.content = null;
}

// ---- the engine's side ----------------------------------------------------------------------

/**
 * DarkShapes: sets the module on the engine and draws index.html's overlay under the UI root. `ui` is
 * the engine's UI global, or anything with its members; `clock` has now(), setTimeout(fn, ms) and
 * clearTimeout(id), as engine/clock.js's Clock does, and runs the toast's and the banner's timers.
 */
export function initScreens({ ui: engine, clock: timers }) {
  engineUi = engine;
  clock = timers;
  render();
}

/**
 * DarkShapes: once a frame. The engine reports clicks, hovering, slider drags and typing as node
 * state rather than as events, so this reads that state for every control on screen and does what the
 * DOM's events did: runs an onclick or an oninput, keeps the callsign to its maxlength, and draws
 * :hover, :active and :focus as they change.
 */
export function poll() {
  if (!engineUi) return;
  if (Number(engineUi.width) !== view.width || Number(engineUi.height) !== view.height) render();
  // Every control is on a screen, and at most one screen is up: its controls are the ones to read.
  const front = screens.find((s) => !byId(s).classList.contains("hidden"));
  if (front === undefined) return;
  for (const element of controls.slice()) {
    if (!element.live) {
      controls.splice(controls.indexOf(element), 1);
      continue;
    }
    if (element.screen !== front || !shown(element)) continue;
    const handle = handleOf(element);
    if (element.tagName === "input") {
      pollInput(element, handle);
      continue;
    }
    const buttonLike = element.tagName === "button" || element.classList.contains("card");
    if (buttonLike) {
      const hover = Boolean(handle.hovered);
      const active = element.tagName === "button" && Boolean(handle.pressed);
      if (hover !== element.hover || active !== element.active) {
        element.hover = hover;
        element.active = active;
        lookChanged(element, false);
      }
    }
    if (element.onclick && element.live && handle.clicked) element.onclick({ type: "click", target: element, currentTarget: element });
  }
}

function pollInput(element, handle) {
  if (element.type === "range") {
    const value = Number(contentOf(element).value);
    if (value !== element.shownValue) {
      element.shownValue = value;
      element.rawValue = String(value);
      if (element.oninput) element.oninput({ type: "input", target: element, currentTarget: element });
    }
    return;
  }
  let text = String(handle.text ?? "");
  if (element.maxLength >= 0 && text.length > element.maxLength) {
    text = text.slice(0, element.maxLength);
    handle.text = text;
  }
  if (text !== element.rawValue) {
    element.rawValue = text;
    placeholderOf(element).visible = text === "";
  }
  const focus = Boolean(handle.focused);
  if (focus !== element.focus) {
    element.focus = focus;
    lookChanged(element, false);
  }
}

/**
 * DarkShapes: whether a full screen is up -- the menu, lobby, draft, shop, score, settings or
 * leaderboards; the toast and the banner do not count. The page's screens took the pointer before
 * the canvas under them did, and the engine's input reaches the game whatever is drawn over it, so
 * the flow keeps mouse buttons and touches from the game while this is true.
 */
export function screenShown() {
  return screens.some((s) => !byId(s).classList.contains("hidden"));
}

/**
 * DarkShapes: canvas units to one of the page's CSS pixels, as the screens are laid out: what the flow
 * hands the renderer, so the HUD it draws on the canvas is the size the screens are.
 */
export function viewScale() {
  return view.scale;
}

/** DarkShapes: `document.getElementById(id).onclick = handler`, for the buttons main.js bound itself. */
export function bind(id, handler) {
  byId(id).onclick = handler;
}

/** DarkShapes: `document.getElementById(id).value = value`, as main.js filled the callsign in. */
export function setValue(id, value) {
  byId(id).value = value;
}

export function showScreen(name) {
  for (const s of screens) byId(s).classList.toggle("hidden", s !== name);
}
export function hideScreens() { for (const s of screens) byId(s).classList.add("hidden"); }

// ---------- menu ----------
export function initMenu(joinCode, defaults) {
  byId("name").value = defaults.name ?? "";
  const pilotsEl = byId("pilots");
  pilotsEl.setChildren([]);   // DarkShapes: innerHTML = ""
  for (const p of PILOTS) {
    const el = createElement("div");
    el.className = "pilot" + (p.id === defaults.pilot ? " sel" : "");
    el.style.setProperty("--c", p.color);
    // DarkShapes: the innerHTML template, as elements.
    el.setChildren([
      h("div", { class: "nm", style: { color: p.color } }, `${p.symbol} ${p.name}`),
      h("div", { class: "ab" }, p.lean, h("br"), `▸ ${p.weapon.label}`, h("br"), `✦ ${p.ability}`),
    ]);
    el.onclick = () => {
      pilotsEl.querySelectorAll(".pilot").forEach(x => x.classList.remove("sel"));
      el.classList.add("sel");
      ui.onAction?.({ t: "ui_pilot", pilot: p.id });
    };
    pilotsEl.appendChild(el);
  }
  if (joinCode) {
    byId("btn-join").classList.remove("hidden");
    byId("btn-solo").classList.add("hidden");
    byId("btn-create").classList.add("hidden");
    byId("menu-msg").textContent = `Joining room ${joinCode}…press JOIN`;
    byId("btn-join").textContent = `JOIN RUN · ${joinCode}`;
  }
  byId("btn-solo").onclick = () => ui.onAction?.({ t: "ui_solo" });
  byId("btn-create").onclick = () => ui.onAction?.({ t: "ui_create" });
  byId("btn-join").onclick = () => ui.onAction?.({ t: "ui_join", code: joinCode });
  showScreen("screen-menu");
}

export function menuMessage(msg) { byId("menu-msg").textContent = msg; }
export function getName() {
  // DarkShapes: the field's text as the player typed it this frame, held to the maxlength poll keeps.
  const field = byId("name");
  if (engineUi && field.live) field.rawValue = String(handleOf(field).text ?? "").slice(0, field.maxLength);
  return field.value.trim().toUpperCase() || "PILOT";
}

// ---------- lobby ----------
export function showLobby(code, roster, myId) {
  byId("lobby-code").textContent = code;
  updateRoster(roster, myId);
  showScreen("screen-lobby");
}
export function updateRoster(roster, myId) {
  const el = byId("roster");
  // DarkShapes: innerHTML = "" and one appendChild a slot, as one set of children drawn once.
  const slots = [];
  for (let i = 0; i < MAX_PLAYERS; i++) {
    const r = roster[i];
    const slot = createElement("div");
    slot.className = "slot" + (r ? " filled" : "");
    if (r) {
      const pilot = PILOTS[r.pilot] ?? PILOTS[0];
      // DarkShapes: the innerHTML template, as elements. A name is shown as text, where markup in one
      // would have been parsed.
      slot.setChildren([
        h("span", { style: { color: pilot.color } }, `${r.name}${r.id === myId ? " (YOU)" : ""}`),
        h("span", { class: "dim" }, `${pilot.symbol} ${pilot.name}`),
      ]);
    } else {
      slot.textContent = "— open slot — send the link —";
    }
    slots.push(slot);
  }
  el.setChildren(slots);
}

// ---------- invite ----------
export async function invite(joinUrl, code) {
  const text = `Fight with me in DarkShapes — wave shooter, right in the browser. Room ${code}:`;
  // DarkShapes: the contract has no share sheet and no clipboard, so this is the original's last
  // resort, the link itself in a toast, that a player without navigator.share or a clipboard got.
  void text;
  toast(joinUrl);
}

// ---------- draft ----------
// `show` is false when the Core Shop is up: after a boss the shop comes first,
// and the draft waits behind it until the player presses READY.
export function showDraft(offer, bankAmount, canBank, grant, show = true) {
  setDraftTitle("WAVE CLEAR — DRAFT AN UPGRADE");
  const g = byId("class-grant");
  if (grant) {
    // DarkShapes: the innerHTML template, as elements.
    g.setChildren(["✦ CLASS UPGRADE — ", h("b", null, `${grant.name}`), `: ${grant.desc}`]);
    g.classList.remove("hidden");
  } else {
    g.classList.add("hidden");
  }
  const cardsEl = byId("draft-cards");
  // DarkShapes: innerHTML = "" and one appendChild a card, as one set of children drawn once.
  const cards = [];
  for (const id of offer) {
    const m = modById(id);
    if (!m) continue;
    const el = createElement("div");
    el.className = "card" + (m.rarity >= 2 ? " r2" : "") + (m.cursed ? " cursed" : "");
    el.setChildren(cardFace(m));
    el.onclick = () => {
      cardsEl.querySelectorAll(".card").forEach(x => { x.onclick = null; x.style.opacity = 0.4; });
      el.style.opacity = 1;
      el.classList.add("picked");
      ui.onAction?.({ t: "pick", id });
    };
    cards.push(el);
  }
  cardsEl.setChildren(cards);
  updateBank(bankAmount, canBank);
  if (show) showScreen("screen-draft");
}
export function setDraftTitle(text) { byId("draft-title").textContent = text; }

/** DarkShapes: a mod card's innerHTML template, as elements. */
function cardFace(m) {
  return [
    h("div", { class: "fam" }, `${m.cursed ? "⚠ CURSED · " : ""}${m.family.toUpperCase()}`),
    h("div", { class: "nm" }, `${m.name}`),
    h("div", { class: "ds" }, `${m.desc}`),
  ];
}

export function updateBank(amount, canBank) {
  const text = amount.toLocaleString("en-US");
  // DarkShapes: main.js calls this every frame; an unchanged amount is left as it is drawn.
  if (byId("bank-amount").textContent !== text) byId("bank-amount").textContent = text;
  byId("btn-bank").disabled = !canBank || amount <= 0;
}
export function updateDraftTimer(frac) {
  const w = `${Math.max(0, frac * 100)}%`;
  // DarkShapes: a width CSS would ignore (NaN) is ignored rather than refused by the engine.
  if (!Number.isFinite(Math.max(0, frac * 100))) return;
  byId("draft-timer").firstElementChild.style.width = w;
  byId("shop-timer").firstElementChild.style.width = w;
}
export function hideDraft() { byId("screen-draft").classList.add("hidden"); }

// ---------- couch co-op: extra draft rows, one per local seat ----------
export function clearDraftLocals() { byId("draft-locals").setChildren([]); }   // DarkShapes: innerHTML = ""

// Renders a pad-navigable card row for a couch seat. Selection moves with
// the seat's d-pad, A confirms (wired from main.js via draftNav/draftConfirm).
export function addDraftRow(seat, pilotDef) {
  const wrap = createElement("div");
  wrap.className = "draft-seat";
  // DarkShapes: the innerHTML template, as elements.
  const grantLine = seat.grant
    ? [h("div", { class: "grant small" }, `✦ ${seat.grant.name}: ${seat.grant.desc}`)] : [];
  wrap.setChildren(
    [h("div", { class: "seat-label", style: { color: pilotDef.color } }, `${pilotDef.symbol} ${seat.name} — \u{1F3AE} d-pad + Ⓐ`)].concat(grantLine));
  const row = createElement("div");
  row.className = "cards";
  seat.cardEls = [];
  seat.sel = 0;
  seat.pickedUi = false;
  for (const id of seat.offer) {
    const m = modById(id);
    if (!m) continue;
    const el = createElement("div");
    el.className = "card" + (m.rarity >= 2 ? " r2" : "") + (m.cursed ? " cursed" : "");
    el.setChildren(cardFace(m));
    seat.cardEls.push({ el, id });
    row.appendChild(el);
  }
  wrap.appendChild(row);
  byId("draft-locals").appendChild(wrap);
  drawSeatSel(seat);
}

export function drawSeatSel(seat) {
  seat.cardEls?.forEach(({ el }, i) => el.classList.toggle("sel", i === seat.sel && !seat.pickedUi));
}

export function seatDraftMove(seat, dir) {
  if (!seat.cardEls?.length || seat.pickedUi) return;
  seat.sel = (seat.sel + dir + seat.cardEls.length) % seat.cardEls.length;
  drawSeatSel(seat);
}

export function seatDraftConfirm(seat) {
  if (!seat.cardEls?.length || seat.pickedUi) return null;
  const { el, id } = seat.cardEls[seat.sel];
  seat.pickedUi = true;
  seat.cardEls.forEach(({ el: e }) => { e.style.opacity = 0.4; e.classList.remove("sel"); });
  el.style.opacity = 1;
  el.classList.add("picked");
  return id;
}

// ---------- the Core Shop (post-boss intermissions) ----------
// P1's storefront. Items grey out when owned or unaffordable; the live
// cores balance re-renders affordability every snapshot via updateShop().
let shopState = { items: [], ownedArr: [], cores: 0 };
let shopRenderKey = ""; // re-render ONLY on real changes — a per-frame rebuild
                        // destroys the node between mousedown and mouseup, so
                        // mouse clicks never register (the "shop dead on PC" bug)

function shopKey(cores, ownedArr) {
  // duplicates included: buying another stack of the same item must re-render
  return cores + "|" + ownedArr.filter(i => i.startsWith("s_")).sort().join(",");
}

export function showShop(itemIds, cores, ownedIds) {
  shopState = { items: itemIds, ownedArr: [...(ownedIds ?? [])], cores };
  shopRenderKey = shopKey(cores, shopState.ownedArr);
  renderShopCards();
  showScreen("screen-shop");
}
export function updateShop(cores, ownedIds) {
  if (byId("screen-shop").classList.contains("hidden")) return;
  const ownedArr = ownedIds ? [...ownedIds] : shopState.ownedArr;
  const key = shopKey(cores, ownedArr);
  if (key === shopRenderKey) return; // nothing changed — leave the DOM alone
  shopRenderKey = key;
  shopState.cores = cores;
  shopState.ownedArr = ownedArr;
  renderShopCards();
}
function renderShopCards() {
  byId("shop-cores").textContent = `⬡ ${shopState.cores.toLocaleString("en-US")}`;
  const el = byId("shop-cards");
  // DarkShapes: innerHTML = "" and one appendChild a card, as one set of children drawn once.
  const cards = [];
  for (const id of shopState.items) {
    const it = shopItemById(id);
    if (!it) continue;
    const stacks = shopState.ownedArr.filter(m => m === id).length;
    const owned = it.once && stacks > 0;
    // rank economics: the next copy costs base × next-rank, and continuous
    // effects arrive at 1/rank strength (flat consumables exempt)
    const nextRank = it.once || it.flat ? 1 : stacks + 1;
    const price = it.price * nextRank;
    const poor = shopState.cores < price;
    const card = createElement("div");
    card.className = "card shopcard" + (owned ? " owned" : poor ? " poor" : "");
    // DarkShapes: the innerHTML template, as elements.
    const stackBadge = !it.once && stacks > 0 ? [" ", h("span", { class: "stacks" }, `×${stacks}`)] : [];
    const rankNote = nextRank > 1 ? [" ", h("span", { class: "stacks" }, `R${nextRank}`)] : [];
    card.setChildren([
      h("div", { class: "fam" }, `${it.pilot === null ? "ANY CLASS" : PILOTS[it.pilot].symbol + " " + PILOTS[it.pilot].name}${it.once ? " · SIGNATURE" : ""}`),
      h("div", { class: "nm" }, ...[`${it.name}`].concat(stackBadge)),
      h("div", { class: "ds" }, `${it.desc}`),
      h("div", { class: "price" }, ...(owned ? ["OWNED"] : [`⬡ ${price}`].concat(rankNote))),
    ]);
    if (!owned && !poor) {
      card.onclick = () => ui.onAction?.({ t: "buy", id });
    }
    cards.push(card);
  }
  el.setChildren(cards);
}
export function hideShopTab() {
  byId("btn-tab-shop").classList.add("hidden");
}
export function showShopTab(cores) {
  const b = byId("btn-tab-shop");
  b.classList.remove("hidden");
  b.textContent = `⬡ SHOP (${cores})`;
}

// couch seats: a compact pad-navigable shop row appended under their draft
export function addShopRow(seat, pilotDef, itemIds, cores) {
  const wrap = createElement("div");
  wrap.className = "draft-seat";
  // DarkShapes: the innerHTML template, as elements.
  wrap.setChildren([h("div", { class: "seat-label", style: { color: pilotDef.color } }, `${pilotDef.symbol} ${seat.name} — SHOP ⬡${cores} (d-pad + Ⓐ, READY to finish)`)]);
  const row = createElement("div");
  row.className = "cards";
  seat.shopEls = [];
  seat.shopSel = 0;
  seat.shopDoneUi = false;
  for (const id of itemIds) {
    const it = shopItemById(id);
    if (!it) continue;
    const el = createElement("div");
    el.className = "card shopcard small";
    // DarkShapes: the innerHTML template, as elements.
    el.setChildren([h("div", { class: "nm" }, `${it.name}`), h("div", { class: "ds" }, `${it.desc}`), h("div", { class: "price" }, `⬡ ${it.price}`)]);
    seat.shopEls.push({ el, id });
    row.appendChild(el);
  }
  const done = createElement("div");
  done.className = "card shopcard small ready";
  // DarkShapes: the innerHTML template, as elements.
  done.setChildren([h("div", { class: "nm" }, "✔ READY"), h("div", { class: "ds" }, "Finish shopping")]);
  seat.shopEls.push({ el: done, id: "__done" });
  row.appendChild(done);
  wrap.appendChild(row);
  byId("draft-locals").appendChild(wrap);
  drawSeatShopSel(seat);
}
export function drawSeatShopSel(seat) {
  seat.shopEls?.forEach(({ el }, i) => el.classList.toggle("sel", i === seat.shopSel && !seat.shopDoneUi));
}
export function seatShopMove(seat, dir) {
  if (!seat.shopEls?.length || seat.shopDoneUi) return;
  seat.shopSel = (seat.shopSel + dir + seat.shopEls.length) % seat.shopEls.length;
  drawSeatShopSel(seat);
}
export function seatShopConfirm(seat) {
  if (!seat.shopEls?.length || seat.shopDoneUi) return null;
  const { el, id } = seat.shopEls[seat.shopSel];
  if (id === "__done") {
    seat.shopDoneUi = true;
    seat.shopEls.forEach(({ el: e }) => e.classList.remove("sel"));
    el.classList.add("picked");
    return "__done";
  }
  el.classList.add("picked");
  return id;
}

// ---------- challenge links ----------
export function showChallengeBanner(ch) {
  const el = byId("challenge-banner");
  // DarkShapes: the innerHTML template, as elements; a name is text, so it needs no escaping.
  el.setChildren(["⚔ ", h("b", null, String(ch.n)), " challenges you!", h("br"),
    h("b", null, Number(ch.s).toLocaleString("en-US")), ` points, wave ${ch.w}. `,
    "Same waves. One click. Beat it."]);
  el.classList.remove("hidden");
  byId("btn-accept").classList.remove("hidden");
  byId("btn-solo").classList.add("hidden");
}
export function bindChallenge(onAccept) { byId("btn-accept").onclick = onAccept; }

// ---------- score ----------
export function showScore(ev, victory, challenge) {
  byId("score-title").textContent = victory ? "\u{1F3C6} ARENA CLEARED" : "RUN OVER";
  // DarkShapes: each innerHTML template below, as elements.
  const rows = (ev.roster ?? []).map(r => h("div", null, `${r.name}: ${r.kills} kills`));
  let rankLine = [];
  if (ev.counted && ev.rank) {
    const what = ev.mode === "daily" ? "DAILY DARK RANK" : "WORLD RANK";
    rankLine = [h("div", { style: { color: "#39f0ff" } }, `★ ${what} #${ev.rank}`)];
  } else if (ev.mode === "daily" && ev.counted === false) {
    rankLine = [h("div", { class: "lost" }, "Daily already attempted today — score not counted")];
  }
  let challengeLine = [];
  if (challenge) {
    const beat = ev.score > challenge.s;
    challengeLine = beat
      ? [h("div", { style: { color: "#b8ff5e" } }, `⚔ CHALLENGE BEATEN — ${String(challenge.n)}'s ${Number(challenge.s).toLocaleString("en-US")} falls`)]
      : [h("div", { class: "lost" }, `⚔ ${String(challenge.n)}'s ${Number(challenge.s).toLocaleString("en-US")} stands — again?`)];
  }
  byId("score-body").setChildren(
    [h("div", { class: "big" }, ev.score.toLocaleString("en-US"))].concat(challengeLine, rankLine,
      ev.lost > 0 ? [h("div", { class: "lost" }, `${ev.lost.toLocaleString("en-US")} unbanked — gone`)] : [],
      [h("div", null, `Wave ${ev.wave} · best ×${ev.bestMult}`)], rows));
  showScreen("screen-score");
}

// ---------- settings ----------
export function openSettings(s) {
  byId("set-shake").value = Math.round(s.shake * 100);
  byId("set-floor").value = Math.round(s.floor * 100);
  byId("set-vol").value = Math.round(s.volume * 400); // master 0..0.25 → 0..100
  syncSettingLabels(s);
  showScreen("screen-settings");
}
const THEME_LABELS = { retro: "◆ RETRO", bots: "\u{1F916} BOTS", zombies: "\u{1F9DF} ZOMBIES", geom: "△ GEO WARS" };
const THEME_ORDER = ["retro", "bots", "zombies", "geom"];

export function syncSettingLabels(s) {
  byId("v-shake").textContent = `${Math.round(s.shake * 100)}%`;
  byId("v-floor").textContent = `${Math.round(s.floor * 100)}%`;
  byId("v-vol").textContent = `${Math.round(s.volume * 400)}%`;
  byId("set-flash").textContent = s.flash ? "ON" : "OFF (photosensitive)";
  byId("set-view").textContent = s.view === "2d" ? "CLASSIC 2D" : "3D STAGE";   // UltraDark-sb
  byId("set-thworld").textContent = THEME_LABELS[s.themeWorld] ?? THEME_LABELS.retro;
  byId("set-thplayers").textContent = THEME_LABELS[s.themePlayers] ?? THEME_LABELS.retro;
  byId("set-thenemies").textContent = THEME_LABELS[s.themeEnemies] ?? THEME_LABELS.retro;
}
export function bindSettings(s, onChange) {
  byId("set-shake").oninput = (e) => { s.shake = e.target.value / 100; syncSettingLabels(s); onChange(); };
  byId("set-floor").oninput = (e) => { s.floor = e.target.value / 100; syncSettingLabels(s); onChange(); };
  byId("set-vol").oninput = (e) => { s.volume = e.target.value / 400; syncSettingLabels(s); onChange(); };
  byId("set-flash").onclick = () => { s.flash = !s.flash; syncSettingLabels(s); onChange(); };
  byId("set-view").onclick = () => { s.view = s.view === "2d" ? "3d" : "2d"; syncSettingLabels(s); onChange(); };   // UltraDark-sb
  const cycle = (key) => {
    const cur = THEME_ORDER.indexOf(s[key]);
    s[key] = THEME_ORDER[(cur + 1) % THEME_ORDER.length];
    syncSettingLabels(s); onChange();
  };
  byId("set-thworld").onclick = () => cycle("themeWorld");
  byId("set-thplayers").onclick = () => cycle("themePlayers");
  byId("set-thenemies").onclick = () => cycle("themeEnemies");
}

// ---------- leaderboards ----------
export function openLeaderboard(fetchTab) {
  const tabs = querySelectorAll(".lb-tab");
  tabs.forEach(b => {
    b.onclick = () => {
      tabs.forEach(x => x.classList.remove("active"));
      b.classList.add("active");
      fetchTab(b.dataset.mode, b.dataset.period);
    };
  });
  tabs[0].classList.add("active");
  tabs.forEach((x, i) => { if (i) x.classList.remove("active"); });
  fetchTab("run", "all");
  showScreen("screen-lb");
}
export function renderLeaderboard(rows) {
  const el = byId("lb-body");
  // DarkShapes: each innerHTML template below, as elements; names are text, so they need no escaping.
  if (!rows.length) { el.setChildren([h("p", { class: "dim" }, "No scores yet — set the first one.")]); return; }
  el.setChildren(rows.map((r, i) =>
    h("div", { class: "lb-row" }, h("span", { class: "rank" }, `${i + 1}`),
      h("span", { class: "score" }, r.score.toLocaleString("en-US")),
      h("span", { class: "who" }, r.names.map(String).join(" + ")),
      h("span", { class: "wave" }, `w${r.wave}${r.squad > 1 ? ` · ${r.squad}p` : ""}`))
  ));
}

// ---------- toast & banner ----------
// DarkShapes: the timers are the injected clock's, where the page had setTimeout.
let toastT = null;
export function toast(msg, ms = 2600) {
  const el = byId("toast");
  el.textContent = msg;
  el.classList.remove("hidden");
  el.style.opacity = 1;
  clock.clearTimeout(toastT);
  toastT = clock.setTimeout(() => { el.style.opacity = 0; clock.setTimeout(() => el.classList.add("hidden"), 400); }, ms);
}

let bannerT = null;
export function banner(text, warn = false, ms = 1800) {
  const el = byId("banner");
  el.textContent = text;
  el.className = "banner" + (warn ? " warn" : "");
  clock.clearTimeout(bannerT);
  bannerT = clock.setTimeout(() => el.classList.add("hidden"), ms);
}

export function bindButtons() {
  byId("btn-invite").onclick = () => ui.onAction?.({ t: "ui_invite" });
  byId("btn-invite2").onclick = () => ui.onAction?.({ t: "ui_invite" });
  byId("btn-start").onclick = () => ui.onAction?.({ t: "start" });
  byId("btn-again").onclick = () => ui.onAction?.({ t: "again" });
  byId("btn-bank").onclick = () => ui.onAction?.({ t: "bank" });
}
