// UltraDark client bootstrap: screens flow, the render loop, the 30 Hz
// input pump, and the event → juice wiring.
//
// DarkShapes: TwinStickTron's client/js/main.js at 7eee633, ported onto the engine. The
// flow is the original's -- the same screens, rooms, couch seats, challenge links and
// event-to-juice table -- and so is every handler; what moved is the page it ran on:
//   - the page ran this module as it loaded and drew with requestAnimationFrame; the
//     entry script calls startDarkShapes from onStart with the engine's globals, and
//     updateDarkShapes once a frame with the frame's seconds;
//   - ui.js is screens.js on the engine's UI, imported as Screens because UI is the
//     engine's own global, and an element's onclick is Screens.bind(id, handler);
//   - localStorage is Prefs, under DarkShapes' keys;
//   - a /j/CODE or /c/BLOB address is a launch parameter (?room=CODE, ?c=BLOB), or the
//     join the Darks Games layer hands over; nothing has an address bar to rewrite;
//   - nothing awaits alike on both engines: createRoom answers through callbacks, a
//     board arrives on DG's "leaderboard" event, and DGAccount and DGOverlay are DG;
//   - setTimeout, setInterval and performance.now() are the client clock, which this
//     module owns and advances once a frame;
//   - a signed-in player's token never passes through the game: the engine hands it to
//     the official server itself.
// Each change is written down, with why, in html5/tests/fixtures/darkshapes-template/ports.json.

import { PHASE, PS, PLAYER, WAVE, PILOTS, ARENA_W, ARENA_H, MAX_PLAYERS } from "../shared/constants.js";
import { ENEMIES } from "../shared/enemies.js";
import { CONSUMABLES, CK } from "../shared/consumables.js";
import { computeStats } from "../shared/mods.js";
import { SHOP_ITEMS, shopItemById } from "../shared/shop.js";
import { BTN, PF, PROTO } from "../shared/protocol.js";
import { net, createRoom, connect, disconnect, sendInput, sendAction, connectExtra, initNet, pumpNet, linkFor } from "./net.js";
import { world, onSnapshot, handleEvent, resetForRun, inGame, initGame } from "./game.js";
import * as game from "./game.js";
import { initInput, pollInput, detectPadJoin, pollPad, pollPadNav, claimPad, releasePad } from "./input.js";
import * as R from "./render.js";
import { settings } from "./render.js";
import * as Screens from "./screens.js";
import { ensureAudio, sfx, setVolume, initAudio } from "./audio.js";
import { Clock } from "../engine/clock.js";
import { encodeBase64, decodeBase64 } from "../engine/base64.js";

// ---------- DarkShapes: the page ----------
// What the page gave this module as it loaded -- its document, storage, address and clocks -- the
// entry hands over in startDarkShapes; the client clock is this module's own.
let env = null;
const clock = new Clock();
let pointerOnScreens = false; // whether a screen was up as the frame's clicks arrived: they were its
const INPUT_MS = 1000 / 30; // the input pump's interval
let inputAcc = 0;

/**
 * DarkShapes: the page's boot, from the entry script's onStart. `engine` holds the engine's globals
 * the client stands on -- ui, input, draw, audio, network, dg, prefs and launch (GameInstance.launch)
 * -- with the entry's wire, and onSessionEnd, which stops the rooms this machine served.
 */
export function startDarkShapes(engine) {
  env = engine;
  Screens.initScreens({ ui: env.ui, clock }); // first: a toast or a banner needs its clock
  initGame({ now: () => clock.now() });
  initNet({ wire: env.wire, network: env.network, clock, server: launchParam("server"), onEnd: env.onSessionEnd });
  R.initRender({ draw: env.draw, now: () => clock.now(), ratio: pixelRatio });
  initInput({ input: pageInput(), now: () => clock.now(), screenToWorld: R.screenToWorld, me: () => world.me, screen: pageSize });
  initAudio({ audio: env.audio, now: () => clock.now() });
  Screens.bindButtons();

  // ---------- boot / URL ----------
  const joinCode = launchCode();
  saved = readPref("darkshapes");
  world.myPilot = saved.pilot ?? 0;
  Screens.initMenu(joinCode, { name: saved.name, pilot: world.myPilot });

  // ---------- challenge links (?c=<base64url{n,s,w,x}>) ----------
  const challengeBlob = launchParam("c");
  if (challengeBlob) {
    try {
      const ch = JSON.parse(atob(challengeBlob.replace(/-/g, "+").replace(/_/g, "/")));
      if (ch && Number.isFinite(ch.s) && Number.isFinite(ch.x)) {
        world.challenge = { n: String(ch.n ?? "A RIVAL").slice(0, 12), s: ch.s, w: ch.w | 0, seed: ch.x >>> 0 };
        Screens.showChallengeBanner(world.challenge);
      }
    } catch { /* malformed blob → plain menu */ }
  }
  Screens.bindChallenge(acceptChallenge);
  Screens.bind("btn-challenge", shareChallenge);

  // ---------- settings (SDD §2.11), persisted ----------
  Object.assign(settings, readPref("darkshapes-settings"));
  setVolume(settings.volume);
  Screens.bindSettings(settings, () => {
    env.prefs.set("darkshapes-settings", settings);
    setVolume(settings.volume);
  });

  initSocial();
  bindPageButtons();
  Screens.ui.onAction = onAction;
  net.onWelcome = netWelcome;
  net.onSnapshot = netSnapshot;
  net.onClose = netClose;
  net.onEvent = netEvent;
}

/** DarkShapes: the script is going: its room goes with it, as a closed tab's socket did. */
export function stopDarkShapes() {
  if (env === null) return;
  if (net.connected || net.ws) leaveRoom();
  env = null;
}

/** DarkShapes: a launch parameter the run was started with, or an empty string. */
function launchParam(name) {
  const value = env.launch[name];
  return typeof value === "string" ? value : "";
}

/** DarkShapes: the room a /j/CODE page was opened on: ?room=CODE (or ?join=, ?j=), as the path read it. */
function launchCode() {
  const code = launchParam("room") || launchParam("join") || launchParam("j");
  return /^[A-Za-z0-9]{4,8}$/.test(code) ? code.toUpperCase() : null;
}

/** DarkShapes: an object kept in Prefs, or an empty one, as JSON.parse(localStorage.getItem(key) ?? "{}") read it. */
function readPref(key) {
  const value = env.prefs.get(key, {});
  return value !== null && typeof value === "object" ? value : {};
}

/**
 * DarkShapes: the page's devicePixelRatio: the drawing buffer's pixels to one of the page's CSS pixels, as
 * the screens lay those out, so the HUD drawn on the canvas is the size of the screens drawn over it.
 */
function pixelRatio() {
  const ratio = env.draw.canvas("darkshapes-arena").width / env.ui.width * Screens.viewScale();
  return ratio > 0 && Number.isFinite(ratio) ? ratio : 1;
}

/** DarkShapes: the page's innerWidth and innerHeight: the drawing buffer in CSS pixels, as render.js measures it. */
function pageSize() {
  const canvas = env.draw.canvas("darkshapes-arena");
  const ratio = pixelRatio();
  return { width: (canvas.width || 1280) / ratio, height: (canvas.height || 720) / ratio };
}

/**
 * DarkShapes: the engine's Input as the page's events reached input.js: pointers in CSS pixels, and
 * no pointer at all while a screen is up, since the page's screens caught every click and touch on
 * them before the canvas under them could. Keys and pads went to the window either way.
 */
function pageInput() {
  const input = env.input;
  const covered = () => pointerOnScreens;
  return {
    get touchCount() { return covered() ? 0 : input.touchCount; },
    getTouch(index) {
      const touch = covered() ? null : input.getTouch(index);
      if (touch === null || touch === undefined) return null;
      const ratio = pixelRatio();
      return {
        id: touch.id, x: touch.x / ratio, y: touch.y / ratio,
        startX: touch.startX / ratio, startY: touch.startY / ratio, phase: touch.phase,
      };
    },
    get mouseX() { return input.mouseX / pixelRatio(); },
    get mouseY() { return input.mouseY / pixelRatio(); },
    isMouseDown: (button) => !covered() && input.isMouseDown(button),
    isMouseTouch: (button) => !covered() && input.isMouseTouch(button),
    isMousePressed: (button) => !covered() && input.isMousePressed(button),
    get padCount() { return input.padCount; },
    padConnected: (index) => input.padConnected(index),
    padAxis: (index, axis) => input.padAxis(index, axis),
    padButton: (index, button) => input.padButton(index, button),
    padAnyButton: (index) => input.padAnyButton(index),
    isKeyDown: (key) => input.isKeyDown(key),
  };
}

/** DarkShapes: atob, which a script has not got: base64 as a string of byte values. */
function atob(text) {
  if (text.length % 4 === 1) throw new Error("not base64");
  const bytes = decodeBase64(text + "=".repeat((4 - text.length % 4) % 4));
  if (bytes === null) throw new Error("not base64");
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return s;
}

/** DarkShapes: btoa, which a script has not got: a string of byte values as base64. */
function btoa(text) {
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c > 255) throw new Error("not a byte string");
    bytes[i] = c;
  }
  return encodeBase64(bytes);
}

let saved = {};

// ---------- challenge links (?c=<base64url{n,s,w,x}>) ----------
let challengeBeaten = false;
let lastEnd = null; // final gameover/victory event, for building challenge links

function acceptChallenge() {
  ensureAudio(); persist();
  pendingAutoStart = true;
  Screens.menuMessage("Loading the challenger's waves…");
  createRoom("challenge", { seed: world.challenge.seed }, (r) => {
    world.code = r.code;
    world.joinUrl = r.joinUrl;
    doConnect(r.code);
  }, () => {
    Screens.menuMessage("Could not start the challenge. Retry?");
  });
}

function challengeLink(end) {
  const payload = { n: Screens.getName(), s: end.score, w: end.wave, x: end.seed >>> 0 };
  const blob = btoa(JSON.stringify(payload)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${String(env.network.roomServer ?? "").replace(/\/+$/, "")}/?c=${blob}`;
}

function shareChallenge() {
  if (!lastEnd || lastEnd.score <= 0) { Screens.toast("Finish a scoring run first."); return; }
  const url = challengeLink(lastEnd);
  // DarkShapes: a script can neither share nor copy, so the link is shown, as the page showed it
  // wherever sharing and the clipboard were refused.
  Screens.toast(url, 6000);
}

// ---------- Darks Games account + social overlay ----------
// DG is the account and overlay SDKs; everything here is optional -- without an account layer every
// member is a safe no-op, and the game plays exactly as before.
let rosterCount = 0; // players in the room per the last WELCOME / roster event (incl. couch seats)

function initSocial() {
  const dg = env.dg;
  if (!dg.available) return;
  dg.on("user", (signedIn) => {
    const name = String(dg.displayName || dg.userName || "");
    if (signedIn && name && !saved.name) { Screens.setValue("name", name.slice(0, 12).toUpperCase()); persist(); }
  });
  dg.on("join", (code) => { socialJoin({ joinCode: code }); });
  dg.on("partyArrived", (isHost, code) => { onPartyArrived({ isHost, room: code || null }); });
  dg.on("leaderboard", showBoard);
  publishPresence();
}

// One publisher, derived from world + net; called on every transition that
// changes the facts (welcome, roster, phase, wave, run end, close).
function publishPresence() {
  const dg = env.dg;
  if (!dg.available) return;
  if (!net.connected || !world.code) { dg.presence({ state: "menu", detail: "", joinCode: null }); return; }
  const n = Math.max(1, rosterCount);
  const lobby = world.phase === PHASE.LOBBY;
  dg.presence({
    state: lobby ? "lobby"
      : world.phase === PHASE.INTERMISSION ? `wave ${world.wave} · drafting`
      : world.phase === PHASE.WAVE ? `wave ${world.wave}`
      : "run over",
    detail: world.challenge ? `Challenge vs ${world.challenge.n}` : lobby ? "Gathering pilots" : `Wave ${world.wave}`,
    joinCode: world.joinUrl ? world.code : null, joinable: n < MAX_PLAYERS, players: n, max: MAX_PLAYERS,
  });
}

// Deliberate leave: UltraDark had no leave path — a second connect() would
// have left the first socket driving net.onClose → auto-rejoin. Detach the
// primary + every couch seat first, then reset the run-local state.
function leaveRoom() {
  for (const seat of world.locals.splice(0)) {
    if (seat.conn?.ws) seat.conn.ws.onclose = null;
    seat.conn?.close();
    releasePad(seat.padIndex);
  }
  Screens.clearDraftLocals();
  disconnect();
  world.resumeKey = "";
  world.myId = 0;
  world.shopOffer = null;
  world.shopDoneUi = false;
  reconnects = 0;
  rosterCount = 0;
  resetForRun();
  prevPhase = PHASE.LOBBY; // so the first snapshot of the next room only fires a real transition
  world.phase = PHASE.LOBBY;
  Screens.hideShopTab();
}

// Overlay "Join" / invite accept / party room → join in place.
function socialJoin(join) {
  let code = join?.joinCode || "";
  code = String(code).toUpperCase();
  if (!/^[A-Z0-9]{4,8}$/.test(code)) return false;
  if (net.connected && world.code === code) return true;
  ensureAudio();
  if (net.connected || net.ws) leaveRoom();
  persist();
  pendingAutoStart = false;
  world.code = code;
  world.joinUrl = "";
  Screens.showScreen("screen-menu");
  Screens.menuMessage("Joining…");
  doConnect(code);
  return true;
}

// Party Launch: the host opens a room (or offers the one it is already in)
// and tells the party; members are joined through socialJoin by the overlay.
function onPartyArrived({ isHost, room }) {
  if (!isHost || room) return;
  ensureAudio(); persist();
  if (net.connected && world.code) {
    env.dg.setPartyRoom(world.code);
    return;
  }
  pendingAutoStart = false;
  Screens.menuMessage("Opening the party lobby…");
  createRoom("run", { hosted: true }, (r) => {
    world.code = r.code;
    world.joinUrl = r.joinUrl;
    doConnect(r.code);
    env.dg.setPartyRoom(r.code);
  }, (e) => {
    Screens.menuMessage("Could not open the party room. Retry?");
    Screens.toast(`Party room failed: ${String(e?.message || e)}`);
  });
}

function leaveOverlay() { // where DONE/BACK returns to
  if (net.connected && world.phase === PHASE.LOBBY) Screens.showScreen("screen-lobby");
  else if (net.connected && inGame()) Screens.hideScreens();
  else Screens.showScreen("screen-menu");
}

let boardAsked = ""; // DarkShapes: the board the leaderboard screen last asked for

function bindPageButtons() {
  // Core Shop navigation (P1)
  Screens.bind("btn-tab-shop", () => {
    if (world.shopOffer) Screens.showShop(world.shopOffer, world.myCores, world.myMods);
  });
  Screens.bind("btn-tab-draft", () => Screens.showScreen("screen-draft"));
  Screens.bind("btn-shop-ready", () => Screens.ui.onAction?.({ t: "shop_done" }));

  Screens.bind("btn-settings", () => { ensureAudio(); Screens.openSettings(settings); });
  Screens.bind("btn-settings-done", leaveOverlay);
  Screens.bind("btn-lb-done", leaveOverlay);
  Screens.bind("btn-lb", () => {
    Screens.openLeaderboard((mode, period) => {
      // GET /api/leaderboard is a Darks Games board: all time and this week are two, the daily a third.
      boardAsked = mode === "daily" ? "daily" : period === "week" ? "runs_weekly" : "runs";
      if (!env.dg.available) { Screens.renderLeaderboard([]); return; }
      env.dg.leaderboard(boardAsked, { limit: 20 });
    });
  });
  Screens.bind("btn-daily", () => {
    ensureAudio(); persist();
    pendingAutoStart = true;
    Screens.menuMessage("Summoning today's dark…");
    createRoom("daily", {}, (r) => {
      world.code = r.code;
      world.joinUrl = r.joinUrl;
      Screens.toast("🌑 DAILY DARK — same waves for everyone. First attempt counts.", 4200);
      doConnect(r.code);
    }, () => {
      Screens.menuMessage("Could not start the daily. Retry?");
    });
  });
}

/** DarkShapes: a board DG read, as the rows /api/leaderboard answered with: score, names, wave and squad. */
function showBoard(result) {
  if (!result || result.scope !== "top" || result.key !== boardAsked) return;
  Screens.renderLeaderboard(result.ok ? result.entries.map((e) => ({
    score: e.score,
    names: Array.isArray(e.meta?.names) ? e.meta.names : [e.name],
    wave: e.meta?.wave ?? 0,
    squad: e.meta?.squad ?? 1,
  })) : []);
}

let pendingAutoStart = false;
let prevPhase = PHASE.LOBBY;
let seq = 0;
let lastInput = { mx: 0, my: 0, ax: 1, ay: 0, buttons: 0 };
// action buttons are edge-sent the moment they're pressed — waiting for the
// 30Hz input timer alone costs up to 33ms before the server hears the trigger
const EDGE_BTNS = BTN.FIRE | BTN.DASH | BTN.BOMB | BTN.ABILITY | BTN.USE;
let prevPolled = 0;
let reconnects = 0;

function onAction(a) {
  ensureAudio();
  if (a.t === "ui_pilot") {
    world.myPilot = a.pilot;
    persist();
    if (net.connected) sendAction({ t: "pilot", pilot: a.pilot });
  } else if (a.t === "ui_solo" || a.t === "ui_create") {
    persist();
    pendingAutoStart = a.t === "ui_solo";
    Screens.menuMessage("Creating room…");
    createRoom("run", { hosted: a.t === "ui_create" }, (r) => {
      world.code = r.code;
      world.joinUrl = r.joinUrl;
      doConnect(r.code);
    }, (e) => {
      Screens.menuMessage(e.message === "server_full" ? "Server is full — try again shortly." : "Could not create a room. Retry?");
    });
  } else if (a.t === "ui_join") {
    persist();
    world.code = a.code;
    world.joinUrl = "";
    Screens.menuMessage("Joining…");
    doConnect(a.code);
  } else if (a.t === "ui_invite") {
    Screens.invite(world.joinUrl, world.code);
  } else if (a.t === "buy") {
    sendAction(a);
  } else if (a.t === "shop_done") {
    sendAction(a);
    world.shopDoneUi = true;
    Screens.showScreen("screen-draft");
  } else if (a.t === "start" || a.t === "again" || a.t === "bank") {
    sendAction({ t: a.t });
    if (a.t === "again") {
      resetForRun();
      Screens.clearDraftLocals();
      for (const seat of world.locals) {
        seat.mods = []; seat.stats = computeStats(PILOTS[seat.pilot], []);
        seat.offer = null; seat.grant = null; seat.pickedUi = false;
      }
      Screens.hideScreens();
    }
  } else if (a.t === "pick") {
    sendAction(a);
    sfx.pick();
  }
}

function persist() {
  env.prefs.set("darkshapes", { name: Screens.getName(), pilot: world.myPilot });
}

// A signed-in player's Darks Games identity is the engine's to carry: it hands the token to the
// official server itself, so the hello holds none and nothing waits for one.
function doConnect(code) {
  connect(code, { name: Screens.getName(), pilot: world.myPilot, resumeKey: world.resumeKey || undefined });
}

// ---------- couch co-op: extra local seats, one socket per player ----------
const MAX_LOCAL = 4; // P1 + 3 pads on one screen

function addLocalPlayer(padIndex) {
  if (!net.connected) { Screens.toast("🎮 Join a lobby first, then press START to add players."); return; }
  if (1 + world.locals.length >= MAX_LOCAL) { Screens.toast("🎮 Couch is full (4 on this screen)."); return; }
  const n = world.locals.length + 2;
  const seat = {
    id: 0, padIndex, name: `${Screens.getName().slice(0, 8)}·${n}`,
    pilot: (world.myPilot + n - 1) % PILOTS.length,
    pred: { x: ARENA_W / 2, y: ARENA_H / 2, vx: 0, vy: 0, dashT: 0, aim: 0 },
    mods: [], stats: null, lastInput: null, seq: 0, dashPrev: false,
    offer: null, grant: null, hud: null, conn: null,
  };
  seat.stats = computeStats(PILOTS[seat.pilot], []);
  claimPad(padIndex, seat);
  seat.conn = connectExtra(world.code, { name: seat.name, pilot: seat.pilot }, {
    onWelcome(w) {
      seat.id = w.id;
      world.locals.push(seat);
      const pilot = PILOTS[seat.pilot];
      Screens.toast(`🎮 ${pilot.symbol} ${seat.name} joined on controller ${padIndex + 1}${w.spectating ? " — drops in next wave" : ""}`, 3200);
      sfx.pick();
    },
    onEvent(ev) {
      if (ev.t === "draft_offer") {
        seat.offer = ev.offer;
        Screens.addDraftRow(seat, PILOTS[seat.pilot]);
        if (seat.shopOffer) Screens.addShopRow(seat, PILOTS[seat.pilot], seat.shopOffer, seat.shopCores ?? 0);
      } else if (ev.t === "shop_offer") {
        seat.shopOffer = ev.items;
        seat.shopCores = ev.cores;
        if (seat.offer) Screens.addShopRow(seat, PILOTS[seat.pilot], ev.items, ev.cores); // draft row already up
      } else if (ev.t === "shop_err") {
        Screens.toast(`🎮 ${seat.name}: ${ev.why === "poor" ? "not enough cores" : ev.why === "owned" ? "already owned" : "can't buy"}`, 1600);
      } else if (ev.t === "class_grant") {
        seat.mods.push(ev.mod);
        seat.stats = computeStats(PILOTS[seat.pilot], seat.mods);
        seat.grant = ev;
      } else if (ev.t === "error") {
        Screens.toast(ev.error === "room_full" ? "Room is full (8 max)." : "Could not join this room.");
      }
    },
    onClose() {
      const i = world.locals.indexOf(seat);
      if (i >= 0) world.locals.splice(i, 1);
      releasePad(padIndex);
      if (net.connected) Screens.toast(`🎮 ${seat.name} left`);
    },
  });
}

// ---------- net wiring ----------
function netWelcome(w) {
  if (w.proto && w.proto !== PROTO) { // a server of another version: there is no page to reload
    leaveRoom();
    Screens.showScreen("screen-menu");
    Screens.menuMessage("That server runs another version of DarkShapes.");
    return;
  }
  world.myId = w.id;
  world.code = w.code;
  world.resumeKey = w.resumeKey;
  world.joinUrl = linkFor(w.code);
  reconnects = 0;
  R.setNames(w.roster);
  nameCache.clear();
  for (const r of w.roster) nameCache.set(r.id, r.name);
  rosterCount = w.roster.length;
  if (w.phase === PHASE.LOBBY) {
    Screens.showLobby(w.code, w.roster, world.myId);
    if (pendingAutoStart) { pendingAutoStart = false; sendAction({ t: "start" }); }
  } else {
    Screens.hideScreens();
    if (w.spectating) Screens.toast("Run in progress — you drop in at the next wave.", 4000);
  }
  publishPresence();
}

function netSnapshot(s) {
  onSnapshot(s);
  if (s.phase !== prevPhase) onPhaseChange(prevPhase, s.phase);
  prevPhase = s.phase;
}

function netClose() {
  if (world.resumeKey && inGame() && reconnects < 5) {
    reconnects++;
    Screens.toast(`Connection lost — rejoining (${reconnects})…`);
    clock.setTimeout(() => doConnect(world.code), 1200 * reconnects);
    return; // presence stays as-is until the rejoin's WELCOME refreshes it
  }
  if (reconnects >= 5) { // a page could be refreshed; a game goes back to its menu
    Screens.toast("Could not rejoin. Try again from the menu.", 6000);
    leaveRoom();
    Screens.showScreen("screen-menu");
  }
  publishPresence(); // → menu
}

function onPhaseChange(from, to) {
  if (to === PHASE.WAVE) { Screens.hideScreens(); }
  if (to === PHASE.LOBBY && from !== PHASE.LOBBY) {
    Screens.showLobby(world.code, [], world.myId);
  }
  publishPresence();
}

function netEvent(ev) {
  handleEvent(ev); // world-model side effects first (patterns, mods, …)
  switch (ev.t) {
    case "kill": {
      const def = ENEMIES[ev.kind];
      R.fxKill(ev.x, ev.y, def?.color ?? "#fff", !!def?.boss);
      R.fxPopup(ev.x, ev.y, `+${ev.points}`, ev.who === world.myId ? "#ffe45b" : "#9fb4dd");
      R.addTrauma(def?.boss ? 0.6 : 0.05);
      if (def?.boss) R.hitstop(220);
      sfx.kill();
      const mi = Math.floor(world.mult);
      if (mi > (net._lastMi ?? 1)) R.gridMilestone();
      net._lastMi = mi;
      break;
    }
    case "pattern": break; // handled in game.handleEvent
    case "cleave":
      if (ev.who !== world.myId) { // own swing is drawn locally at fire cadence
        R.fxCleave(ev.x, ev.y, ev.aim, ev.r, ev.full);
        sfx.swing();
      }
      break;
    case "aura_heal":
      if (ev.who === world.myId) R.fxPopup(world.me.x, world.me.y, "+♥", "#b8ff5e");
      break;
    case "shop_offer":
      // Open the shop the moment the boss wave ends. It used to be a small tab
      // on the draft screen, and players picked their card and never saw it.
      world.shopOffer = ev.items;
      world.shopDoneUi = false;
      Screens.showShopTab(ev.cores);
      Screens.showShop(ev.items, ev.cores, world.myMods);
      break;
    case "bought": {
      const it = shopItemById(ev.mod);
      if (ev.who === world.myId && it) {
        Screens.toast(`⬡ Bought ${it.name}`, 2000);
        sfx.buy();
      }
      // couch seats: keep prediction stats in sync (stackables repeat)
      const bSeat = world.locals.find(l => l.id === ev.who);
      if (bSeat) {
        bSeat.mods.push(ev.mod);
        bSeat.stats = computeStats(PILOTS[bSeat.pilot], bSeat.mods);
      }
      break;
    }
    case "shop_err":
      Screens.toast(ev.why === "poor" ? "Not enough cores." : ev.why === "owned" ? "Already owned." : "Can't buy that.", 1800);
      break;
    case "bomb": R.fxBomb(); sfx.bomb(); break;
    case "hurt":
      if (ev.who === world.myId) { R.addTrauma(0.5); R.hitstop(50); sfx.hurt(); }
      else R.addTrauma(0.15);
      break;
    case "dash": if (ev.who === world.myId) sfx.dash(); break;
    case "ability":
      if (ev.pilot === 2 && ev.phase === 1) {
        if (ev.who === world.myId) Screens.toast("◇ Beacon placed — press Q again to warp back", 2600);
        sfx.pick();
      } else if (ev.pilot === 2 && ev.phase === 2) {
        sfx.warp();
      } else if (ev.pilot === 6) {
        sfx.freeze(); R.addTrauma(0.2);
      } else {
        sfx.ability();
      }
      break;
    case "nova": R.addTrauma(0.2); break;
    case "downed":
      Screens.banner(ev.who === world.myId ? "YOU ARE DOWN" : `${nameOf(ev.who)} IS DOWN`, true, 2200);
      if (ev.who === world.myId && ev.cause) Screens.toast(`☠ Killed by ${ev.cause}`, 3200); // death recap
      sfx.down();
      R.addTrauma(0.5);
      break;
    case "leech":
      if (ev.who === world.myId) {
        R.fxPopup(world.me.x, world.me.y, "MULTIPLIER DRAINED", "#5bffc9");
        R.addTrauma(0.15);
      }
      break;
    case "laser_warn": case "laser_fire": break; // game.handleEvent stores them
    case "doors": if (ev.open) Screens.banner("FOUNDRY DOORS OPEN — HIT IT NOW", true, 1400); break;
    case "streak": if (ev.who === world.myId) { Screens.toast("🔥 KILL STREAK — +1 BOMB"); } break;
    case "revived":
      if (!ev.solo) Screens.toast(ev.cost > 0 ? `Revived — insurance cost ${ev.cost.toLocaleString("en-US")} banked` : "Revived!");
      sfx.revive();
      break;
    case "out": Screens.banner(`${nameOf(ev.who)} IS OUT`, true); break;
    case "wave_start":
      Screens.hideScreens();
      Screens.clearDraftLocals();
      Screens.hideShopTab();
      world.shopOffer = null;
      world.shopDoneUi = false;
      world.charges = {}; // boss mechanics never outlive their wave
      for (const seat of world.locals) {
        seat.offer = null; seat.grant = null; seat.pickedUi = false;
        seat.shopOffer = null; seat.shopEls = null; seat.shopDoneUi = false;
      }
      Screens.banner(`WAVE ${ev.wave}`, false, 1600);
      sfx.wave();
      if (Number.isFinite(ev.wave)) world.wave = ev.wave;
      publishPresence();
      break;
    case "wave_end": Screens.banner("WAVE CLEAR", false, 1400); break;
    case "boss": Screens.banner(`⚠ ${ev.name}`, true, 2600); sfx.boss(); break;
    case "enrage": Screens.banner("ENRAGED", true, 1500); R.addTrauma(0.4); break;
    case "boss_down": Screens.banner("BOSS DOWN — +1 BOMB", false, 2000); world.charges = {}; break;
    // ---- raid mechanics (Naxx school) ----
    case "berserk": Screens.banner("⚠ BERSERK — KILL IT NOW", true, 3000); sfx.boss(); R.addTrauma(0.5); break;
    case "hateful":
      Screens.banner(ev.who === world.myId ? "HATEFUL CHARGE — ON YOU" : "HATEFUL CHARGE", true, 1400);
      // telegraph line from the boss to the locked point, drawn like a laser warn
      // (DarkShapes: `until` is on the client clock, as game.js's lasers are)
      world.lasers.push({ id: "hate" + ev.id, sx: ev.sx, sy: ev.sy, tx: ev.tx, ty: ev.ty, firing: false, until: clock.now() + 1500 });
      sfx.down();
      break;
    case "charge": {
      world.charges = ev.charges ?? {};
      const mine = world.charges[world.myId];
      if (mine) {
        Screens.banner("POLARITY SHIFT", true, 1500);
        Screens.toast(mine > 0 ? "You are ➕ POSITIVE — stand away from ➖" : "You are ➖ NEGATIVE — stand away from ➕", 3500);
        sfx.freeze();
      }
      break;
    }
    case "shock":
      if (ev.a === world.myId || ev.b === world.myId) { R.addTrauma(0.3); sfx.zap(); }
      break;
    case "wrapped":
      Screens.banner(ev.who === world.myId ? "YOU'RE COCOONED" : `${nameOf(ev.who)} IS COCOONED — SHOOT THEM FREE`, true, 2600);
      sfx.down();
      break;
    case "unwrapped":
      if (ev.who === world.myId) Screens.toast("Cut free — move!", 1800);
      sfx.revive();
      break;
    case "hole_burst": R.addTrauma(0.35); sfx.bomb(); break;
    case "class_grant":
      sfx.pick();
      break;
    case "pickup_got":
      if (ev.who === world.myId) {
        const c = CONSUMABLES[ev.kind];
        if (c) Screens.toast(`${c.glyph} ${c.name} — press F to use`, 2200);
        sfx.pickup();
      }
      break;
    case "consumed": {
      const c = CONSUMABLES[ev.kind];
      if (ev.who === world.myId && c) {
        Screens.banner(`${c.glyph} ${c.name.toUpperCase()}`, false, 1200);
        if (ev.kind === CK.SHIELD) R.addTrauma(0.1);
      }
      sfx.use();
      break;
    }
    case "draft_offer":
      Screens.showDraft(ev.offer, world.unbanked, true, world.lastGrant, !(world.shopOffer && !world.shopDoneUi));
      world.lastGrant = null;
      break;
    case "bank":
      sfx.bank();
      Screens.toast(`🏦 BANKED ${ev.amount.toLocaleString("en-US")}`);
      Screens.updateBank(0, false);
      break;
    case "picked": {
      if (ev.who === world.myId) {
        Screens.setDraftTitle(world.shopOffer && !world.shopDoneUi
          ? "UPGRADE LOCKED IN — FINISH IN THE ⬡ SHOP, THEN READY"
          : rosterCount > 1 ? "READY — WAITING FOR THE SQUAD" : "READY");
      }
      // keep couch seats' prediction stats in sync with their drafts
      // (no dedupe — the same mod picked/bought twice legitimately stacks)
      const seat = world.locals.find(l => l.id === ev.who);
      if (seat) {
        seat.mods.push(ev.mod);
        seat.stats = computeStats(PILOTS[seat.pilot], seat.mods);
      }
      break;
    }
    case "intermission":
      // Sent again with `ready` once the whole squad is done, so the bar shows
      // the short countdown rather than a sliver of the long wait.
      world.intermissionS = ev.seconds || 20;
      if (ev.ready) Screens.setDraftTitle(`ALL READY — NEXT WAVE IN ${Math.round(ev.seconds)}`);
      break;
    case "gameover":
      lastEnd = ev; challengeBeaten = false;
      sfx.over(); Screens.showScore(ev, false, world.challenge);
      publishPresence();
      break;
    case "victory":
      lastEnd = ev; challengeBeaten = false;
      sfx.win(); Screens.showScore(ev, true, world.challenge);
      publishPresence();
      break;
    case "ranked": // the hub's place for this pilot's account, after the run's end (server/ranked.js)
      if (lastEnd && (world.phase === PHASE.GAMEOVER || world.phase === PHASE.VICTORY)) {
        Object.assign(lastEnd, { rank: ev.rank, counted: ev.counted, mode: ev.mode });
        Screens.showScore(lastEnd, lastEnd.t === "victory", world.challenge);
      }
      break;
    case "roster":
      R.setNames(ev.roster);
      nameCache.clear();
      for (const r of ev.roster) nameCache.set(r.id, r.name);
      Screens.updateRoster(ev.roster, world.myId);
      rosterCount = ev.roster.length;
      publishPresence();
      break;
    case "error":
      // the POST that made a room answered server_full and slow_down; here the joining socket hears them
      Screens.menuMessage(ev.error === "room_full" ? "That room is full (4 max)."
        : ev.error === "server_full" ? "Server is full — try again shortly."
        : ev.error === "slow_down" ? "Could not create a room. Retry?"
        : "Room not found — it may have expired.");
      Screens.showScreen("screen-menu");
      break;
  }
}

const nameCache = new Map();
function nameOf(id) { return nameCache.get(id) ?? "ALLY"; }

// ---------- loops ----------
let fireAcc = 0;

/** DarkShapes: the page's animation frame, from the entry once a frame with the frame's seconds. */
export function updateDarkShapes(seconds) {
  if (env === null) return;
  clock.advance(seconds);
  pumpNet();
  // The page's clicks, typing and taps on its screens, which reached it between frames. Whether a screen
  // was up is read first, so the click that closes the last one is the screen's and never also a shot.
  pointerOnScreens = Screens.screenShown();
  Screens.poll();
  if (env.input.isMousePressed(0) || env.input.touchCount > 0) ensureAudio(); // pointerdown
  const dt = Math.min(0.05, seconds > 0 ? seconds : 0);
  lastInput = pollInput();
  const rising = lastInput.buttons & ~prevPolled & EDGE_BTNS;
  if (rising && net.connected && world.myId) {
    seq = (seq + 1) % 65536;
    sendInput(seq, lastInput);
  }
  prevPolled = lastInput.buttons;
  // couch co-op: unclaimed pad pressing START joins the room
  const joinPad = detectPadJoin();
  if (joinPad != null) addLocalPlayer(joinPad);
  // per-seat pad input + draft navigation
  for (const seat of world.locals) {
    seat.lastInput = pollPad(seat.padIndex);
    if (seat.lastInput && seat.id && seat.conn.ws.readyState === 1) {
      const sRise = seat.lastInput.buttons & ~(seat.prevPolled ?? 0) & EDGE_BTNS;
      if (sRise) {
        seat.seq = ((seat.seq ?? 0) + 1) % 65536;
        seat.conn.sendInput(seat.seq, seat.lastInput);
      }
      seat.prevPolled = seat.lastInput.buttons;
    }
    if (world.phase === PHASE.INTERMISSION && seat.offer && !seat.pickedUi) {
      const nav = pollPadNav(seat.padIndex);
      if (nav.left) Screens.seatDraftMove(seat, -1);
      if (nav.right) Screens.seatDraftMove(seat, 1);
      if (nav.confirm) {
        const id = Screens.seatDraftConfirm(seat);
        if (id) { seat.conn.sendAction({ t: "pick", id }); sfx.pick(); }
      }
    } else if (world.phase === PHASE.INTERMISSION && seat.pickedUi && seat.shopEls && !seat.shopDoneUi) {
      // after drafting, the seat's d-pad drives its shop row
      const nav = pollPadNav(seat.padIndex);
      if (nav.left) Screens.seatShopMove(seat, -1);
      if (nav.right) Screens.seatShopMove(seat, 1);
      if (nav.confirm) {
        const id = Screens.seatShopConfirm(seat);
        if (id === "__done") seat.conn.sendAction({ t: "shop_done" });
        else if (id) { seat.conn.sendAction({ t: "buy", id }); sfx.buy(); }
      }
    }
  }
  if (!R.isHitstopped()) game.frame(dt, lastInput);
  // local muzzle feel: flash at predicted cadence while firing (a webbed
  // pilot is silenced server-side — no flash, no sound, no predicted rail)
  if ((lastInput.buttons & BTN.FIRE) && world.myState === PS.ALIVE &&
      world.phase === PHASE.WAVE && !(world.myFlags & PF.WRAPPED)) {
    fireAcc -= dt;
    if (fireAcc <= 0) {
      const wpn = PILOTS[world.myPilot].weapon;
      fireAcc = wpn.cd / (world.myStats.fire || 1);
      if (wpn.kind === "cleave") {
        R.fxCleave(world.me.x, world.me.y, world.me.aim, wpn.arcR, world.myStats.cleave360 > 0);
        sfx.swing();
      } else {
        if (wpn.kind === "rail") game.predictRail(); // the projectile leaves WITH the flash
        R.fxMuzzle(world.me.x, world.me.y, world.me.aim, PILOTS[world.myPilot].color);
        ({ smg: sfx.smg, shotgun: sfx.boom, lance: sfx.lance, rail: sfx.rail, arc: sfx.arc }[wpn.kind] ?? sfx.shoot)();
      }
    }
  } else fireAcc = Math.max(0, fireAcc - dt); // cd keeps draining like the server's
  // live challenge check: the moment the squad's total passes the target
  if (world.challenge && !challengeBeaten && inGame() &&
      world.banked + world.unbanked > world.challenge.s) {
    challengeBeaten = true;
    Screens.banner(`⚔ ${world.challenge.n}'S SCORE FALLS`, false, 2600);
    sfx.bank();
    R.gridMilestone();
  }
  R.draw(dt);
  // intermission UI ticks
  if (world.phase === PHASE.INTERMISSION) {
    Screens.updateDraftTimer(world.phaseT / (world.intermissionS * 30)); // seconds from the latest intermission event
    Screens.updateBank(world.unbanked, true);
    Screens.updateShop(world.myCores, world.myMods);
  }
  // the 30 Hz input pump: the page's setInterval, at most one send a frame
  inputAcc += seconds > 0 ? seconds * 1000 : 0;
  if (inputAcc >= INPUT_MS) {
    inputAcc = Math.min(inputAcc - INPUT_MS, INPUT_MS);
    pumpInputs();
  }
}

function pumpInputs() {
  if (net.connected && world.myId) {
    seq = (seq + 1) % 65536;
    sendInput(seq, lastInput);
  }
  for (const seat of world.locals) {
    if (seat.id && seat.lastInput && seat.conn.ws.readyState === 1) {
      seat.seq = (seat.seq + 1) % 65536;
      seat.conn.sendInput(seat.seq, seat.lastInput);
    }
  }
}
