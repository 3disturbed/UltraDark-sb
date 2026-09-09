// Menus.js -- the front end: a main menu, options, character select and dialogs.
// Attach to an empty actor and tag it "Menus".
//
// All four are one script because they are one thing: a stack of screens over
// the game, only one of which is up at a time, sharing a palette, a focus ring
// and the rule that Escape goes back. Four scripts would be four canvases whose
// paint order is whatever the scene file happens to list.
//
// Everything here is the UI tree rather than the flat surface it replaces. That
// is not a port for its own sake -- a menu is the one thing the flat API could
// not do at all. A row of eight cards that share a width, a column that centres
// itself at any window size, a button a gamepad can walk to, and a dialog that
// traps focus so the menu behind it cannot be clicked: every one of those was
// arithmetic before, and most of it was arithmetic nobody wrote.

// ===========================================================================
// Screens
// ===========================================================================
var S_NONE = 0, S_MAIN = 1, S_SELECT = 2, S_OPTIONS = 3;

// ===========================================================================
// The pilots, as the select screen shows them. Names and abilities come from
// the Pilot script so there is one roster, not two.
// ===========================================================================
var PILOT_COUNT = 8;

// ===========================================================================
// Palette and shape
// ===========================================================================
var backdrop     = "#080a0fe8";
var panelColour  = "#161920";
var cardColour   = "#1b1f29";
var cardOn       = "#2b3446";
var accent       = "#78d4c0";
var textColour   = "#e8e6e1";
var dimColour    = "#9a978f";
var warnColour   = "#ff4d4d";

var cardW = 150;
var cardH = 132;
var cardGap = 10;

// ===========================================================================
// State
// ===========================================================================
var screen = S_NONE;
var director = null, pilot = null, stage = null, fx = null, hud = null;

var built = 0;
var chosen = 0;

// The dialog is a stack of one: a confirm over a confirm is a design mistake
// rather than a thing to support.
var dialogOpen = 0;
var dialogAnswer = -1;        // -1 pending, 1 confirmed, 0 cancelled
var dialogTag = "";

// What should hold focus. Applied every frame until it does: a node that was
// hidden a moment ago has no rectangle yet -- the canvas lays out once per frame
// and a screen shown from a script is shown between two of those -- and focus
// will not go to a node with no rectangle. Asking once looks like it worked,
// because the mouse still works; the half that breaks is the pad.
var wantFocus = "";

// Options, and what they do. Session-scoped: the contract has no local storage,
// and a cloud save that only exists when somebody is signed in is the wrong
// place for "I cannot read this".
var optShake = 1.0;           // screen-shake scale
var optDark = 1.0;            // how dark the dark gets -- an accessibility dial
var optVolume = 0.8;
var optCamera = 1.0;          // how far back the 3D camera sits

function onStart() {
    resolve();
    build();
    show(S_MAIN);
}

function resolve() {
    if (!director) { var d = Scene.findFirstByTag("Director"); if (d) { director = d.getComponent("ScriptComponent"); } }
    if (!pilot)    { var p = Scene.findFirstByTag("Player");   if (p) { pilot = p.getComponent("ScriptComponent"); } }
    if (!stage)    { var s = Scene.findFirstByTag("Stage3D");  if (s) { stage = s.getComponent("ScriptComponent"); } }
    if (!fx)       { var f = Scene.findFirstByTag("Effects");  if (f) { fx = f.getComponent("ScriptComponent"); } }
    if (!hud)      { var h = Scene.findFirstByTag("Hud");      if (h) { hud = h.getComponent("ScriptComponent"); } }
}

// ===========================================================================
// Building -- one tree, three screens in it, all hidden
// ===========================================================================

function build() {
    if (built) { return; }
    built = 1;

    // Above the HUD and the draft board, below nothing. The flat UI painted in
    // whatever order scripts happened to start in; this is the order.
    UI.order = 100;

    UI.build({
        name: "menus", width: "*", height: "*",
        children: [mainScreen(), selectScreen(), optionsScreen(), dialogScreen()],
    });
}

/** A full-screen dimmed sheet with a centred column on it. */
function sheet(name, children) {
    return {
        name: name, visible: false, width: "*", height: "*",
        background: backdrop, layout: "column",
        mainAlign: "center", crossAlign: "center", gap: 18, padding: 24,
        children: children,
    };
}

function button(name, text, wide) {
    return {
        name: name, kind: "button", text: text, focusable: "yes",
        width: wide ? 300 : 190, height: 34, align: "center",
        background: cardColour, tint: textColour, scale: 2,
    };
}

function mainScreen() {
    return sheet("main", [
        { kind: "label", text: "ULTRADARK", scale: 6, tint: textColour },
        { kind: "label", text: "a SexyBiscuit port", scale: 1, tint: dimColour },
        { height: 10 },
        button("mPlay", "PLAY", true),
        button("mOptions", "OPTIONS", true),
        button("mHow", "HOW TO PLAY", true),
        { height: 8 },
        { name: "mWho", kind: "label", text: "", scale: 1, tint: dimColour },
    ]);
}

function selectScreen() {
    // Two rows of four. A grid, so the eight cards share a width and the row
    // centres itself -- the flat version hand-placed each card for one window.
    var cards = [];
    for (var i = 0; i < PILOT_COUNT; i++) {
        cards.push({
            name: "card" + i, kind: "button", focusable: "yes",
            width: cardW, height: cardH, background: cardColour,
            layout: "column", gap: 4, padding: 10, crossAlign: "stretch",
            children: [
                { name: "cardStrip" + i, height: 5, background: accent },
                { name: "cardName" + i, kind: "label", text: "", scale: 2,
                  tint: textColour, align: "center" },
                { name: "cardRole" + i, kind: "label", text: "", scale: 1,
                  tint: dimColour, align: "center", wrapText: true },
                { kind: "spacer", grow: 1 },
                { name: "cardHint" + i, kind: "label", text: "", scale: 1,
                  tint: dimColour, align: "center" },
            ],
        });
    }

    return sheet("select", [
        { kind: "label", text: "CHOOSE A PILOT", scale: 4, tint: textColour },
        { name: "selHint", kind: "label", scale: 1, tint: dimColour,
          text: "arrows or click  -  ENTER to launch  -  ESC to go back" },
        { name: "cards", layout: "grid", columns: 4, gap: cardGap,
          width: cardW * 4 + cardGap * 3, height: cardH * 2 + cardGap,
          children: cards },
        { layout: "row", gap: 12, children: [
            button("sBack", "BACK", false),
            button("sLaunch", "LAUNCH", false),
        ] },
    ]);
}

function optionsScreen() {
    return sheet("options", [
        { kind: "label", text: "OPTIONS", scale: 4, tint: textColour },
        {
            name: "optPanel", layout: "column", gap: 10, padding: 16,
            width: 460, background: panelColour, crossAlign: "stretch",
            children: [
                optionRow("optShake", "SCREEN SHAKE"),
                optionRow("optDark", "DARKNESS"),
                optionRow("optVolume", "VOLUME"),
                optionRow("optCamera", "CAMERA DISTANCE"),
                { kind: "label", scale: 1, tint: dimColour, wrapText: true,
                  text: "Darkness lowers the ceiling on how black the late waves get. "
                      + "Settings last for this session." },
            ],
        },
        button("oBack", "BACK", false),
    ]);
}

/**
 * A label, a value, and two buttons.
 *
 * Buttons rather than a slider because the tree has no slider and a fake one --
 * a bar you click at a position -- reads as a bar until somebody tries it.
 */
function optionRow(key, label) {
    return {
        layout: "row", gap: 8, crossAlign: "center",
        children: [
            { kind: "label", text: label, width: 190, scale: 1, tint: dimColour },
            { name: key + "Value", kind: "label", text: "", grow: 1,
              scale: 2, tint: textColour, align: "center" },
            { name: key + "Down", kind: "button", text: "-", focusable: "yes",
              width: 34, height: 26, scale: 2, align: "center",
              background: cardColour, tint: textColour },
            { name: key + "Up", kind: "button", text: "+", focusable: "yes",
              width: 34, height: 26, scale: 2, align: "center",
              background: cardColour, tint: textColour },
        ],
    };
}

function dialogScreen() {
    // `modal` is the whole point: focus cannot leave it, and a click outside it
    // misses rather than pressing whatever it lands on. A confirm you can click
    // straight through is not a confirm.
    return {
        name: "dialogSheet", visible: false, width: "*", height: "*",
        background: "#00000099", layout: "column",
        mainAlign: "center", crossAlign: "center", modal: true,
        children: [{
            name: "dialog", layout: "column", gap: 12, padding: 20,
            width: 440, background: panelColour, crossAlign: "stretch",
            children: [
                { name: "dlgTitle", kind: "label", text: "", scale: 3,
                  tint: textColour, align: "center" },
                { name: "dlgBody", kind: "label", text: "", scale: 1,
                  tint: dimColour, align: "center", wrapText: true },
                { layout: "row", gap: 10, mainAlign: "center", children: [
                    { name: "dlgCancel", kind: "button", text: "CANCEL", focusable: "yes",
                      width: 150, height: 32, scale: 2, align: "center",
                      background: cardColour, tint: dimColour },
                    { name: "dlgOk", kind: "button", text: "OK", focusable: "yes",
                      width: 150, height: 32, scale: 2, align: "center",
                      background: cardColour, tint: accent },
                ] },
            ],
        }],
    };
}

// ===========================================================================
// Showing and hiding
// ===========================================================================

function show(which) {
    screen = which;

    UI.find("main").visible = which === S_MAIN;
    UI.find("select").visible = which === S_SELECT;
    UI.find("options").visible = which === S_OPTIONS;

    if (which === S_SELECT) { fillCards(); }
    if (which === S_OPTIONS) { fillOptions(); }
    if (which === S_MAIN) { greet(); }

    // Focus something the moment a screen opens, so a pad or a TV remote has
    // somewhere to start. A menu that needs a mouse to give itself focus is a
    // menu a pad cannot open.
    wantFocus = which === S_MAIN ? "mPlay"
              : which === S_SELECT ? ("card" + chosen)
              : which === S_OPTIONS ? "optShakeUp" : "";
    takeFocus();

    // The game underneath is paused while any of this is up, and its HUD stands
    // down: a readout showing WAVE 0 and a full health bar through a menu is a
    // second thing to read that says nothing.
    if (director) { director.call("setMenuOpen", which === S_NONE ? 0 : 1); }
    if (hud) { hud.call("setChromeVisible", which === S_NONE ? 1 : 0); }
}

function isOpen() { return screen === S_NONE ? 0 : 1; }

/** Puts focus where this screen wants it, retrying until it lands. */
function takeFocus() {
    if (wantFocus === "") { return; }

    var focused = UI.focused;
    if (focused && focused.name === wantFocus) { wantFocus = ""; return; }

    UI.setFocus(wantFocus);
}

function greet() {
    var who = UI.find("mWho");
    if (!who) { return; }
    who.text = DG.signedIn ? ("signed in as " + DG.displayName) : "";
}

// ===========================================================================
// Frame
// ===========================================================================

function onUpdate(dt) {
    resolve();
    takeFocus();

    if (dialogOpen) { runDialog(); return; }
    if (screen === S_NONE) { return; }

    if (screen === S_MAIN) { runMain(); }
    else if (screen === S_SELECT) { runSelect(); }
    else if (screen === S_OPTIONS) { runOptions(); }
}

function runMain() {
    if (clicked("mPlay")) { show(S_SELECT); return; }
    if (clicked("mOptions")) { show(S_OPTIONS); return; }
    if (clicked("mHow")) {
        ask("how", "HOW TO PLAY",
            "WASD moves, the mouse aims, hold to fire. SHIFT dashes, SPACE is your "
            + "ability and F uses an item. Clear the wave, take a mod, and keep the "
            + "multiplier alive -- a hit halves it.");
    }
}

function runSelect() {
    // Arrows walk the grid, which is the focus system doing the work: the cards
    // are laid out, so "the one to the right" is a fact rather than an index.
    if (Input.isKeyPressed("ArrowRight")) { UI.navigate("right"); }
    if (Input.isKeyPressed("ArrowLeft"))  { UI.navigate("left"); }
    if (Input.isKeyPressed("ArrowUp"))    { UI.navigate("up"); }
    if (Input.isKeyPressed("ArrowDown"))  { UI.navigate("down"); }

    // Whatever has focus is what is selected -- one state, not two that can
    // disagree about which pilot the player is looking at.
    var focused = UI.focused;
    if (focused) {
        for (var i = 0; i < PILOT_COUNT; i++) {
            if (focused.name === "card" + i && i !== chosen) { choose(i); }
        }
    }

    for (var c = 0; c < PILOT_COUNT; c++) {
        if (clicked("card" + c)) { choose(c); launch(); return; }
        if (Input.isKeyPressed("D" + (c + 1))) { choose(c); }
    }

    if (clicked("sLaunch") || Input.isKeyPressed("Enter")) { launch(); return; }
    if (clicked("sBack") || Input.isKeyPressed("Escape")) { show(S_MAIN); }
}

function runOptions() {
    if (step("optShake", 0.25, 0, 2)) { applyOptions(); }
    if (step("optDark", 0.1, 0.3, 1)) { applyOptions(); }
    if (step("optVolume", 0.1, 0, 1)) { applyOptions(); }
    if (step("optCamera", 0.15, 0.5, 2)) { applyOptions(); }

    if (clicked("oBack") || Input.isKeyPressed("Escape")) { show(S_MAIN); }
}

/** One option's two buttons. Returns 1 when the value moved. */
function step(key, by, low, high) {
    var delta = 0;
    if (clicked(key + "Up")) { delta = by; }
    if (clicked(key + "Down")) { delta = -by; }
    if (delta === 0) { return 0; }

    var value = optionValue(key) + delta;
    if (value < low) { value = low; }
    if (value > high) { value = high; }
    setOption(key, Math.round(value * 100) / 100);
    fillOptions();
    return 1;
}

function optionValue(key) {
    if (key === "optShake") { return optShake; }
    if (key === "optDark") { return optDark; }
    if (key === "optVolume") { return optVolume; }
    return optCamera;
}

function setOption(key, value) {
    if (key === "optShake") { optShake = value; }
    else if (key === "optDark") { optDark = value; }
    else if (key === "optVolume") { optVolume = value; }
    else { optCamera = value; }
}

function fillOptions() {
    setText("optShakeValue", percent(optShake));
    setText("optDarkValue", percent(optDark));
    setText("optVolumeValue", percent(optVolume));
    setText("optCameraValue", percent(optCamera));
}

function percent(v) { return Math.round(v * 100) + "%"; }

/** Pushes the settings into the systems that own them. */
function applyOptions() {
    Audio.setVolume(optVolume);
    if (fx) { fx.call("setShakeScale", optShake); }
    if (stage) { stage.call("setDarkLimit", optDark); stage.call("setCameraScale", optCamera); }
}

// ===========================================================================
// Character select
// ===========================================================================

function fillCards() {
    if (!pilot) { return; }
    for (var i = 0; i < PILOT_COUNT; i++) {
        setText("cardName" + i, String(pilot.call("nameOfPilot", i)));
        setText("cardRole" + i, String(pilot.call("abilityOfPilot", i)));
        setText("cardHint" + i, String(i + 1));
    }
    paintCards();
}

function choose(index) {
    chosen = index;
    if (pilot) { pilot.call("setPilot", index); }
    paintCards();
}

function paintCards() {
    for (var i = 0; i < PILOT_COUNT; i++) {
        var card = UI.find("card" + i);
        var strip = UI.find("cardStrip" + i);
        if (card) { card.background = i === chosen ? cardOn : cardColour; }
        if (strip) { strip.background = i === chosen ? accent : "#333a48"; }
    }
}

function launch() {
    show(S_NONE);
    if (director) { director.call("forceLaunch"); }
}

// ===========================================================================
// Dialogs
// ===========================================================================

/**
 * Puts a question up. The answer arrives on `dialogTaken`, not as a return
 * value: a script cannot block, and a callback across the script boundary is
 * not something the contract carries.
 */
function ask(tag, title, body) {
    dialogTag = String(tag);
    dialogOpen = 1;
    dialogAnswer = -1;

    setText("dlgTitle", String(title));
    setText("dlgBody", String(body));
    UI.find("dialogSheet").visible = true;
    wantFocus = "dlgOk";
    takeFocus();
    return 1;
}

/** A dialog with one way out, for saying something rather than asking. */
function tell(tag, title, body) {
    ask(tag, title, body);
    UI.find("dlgCancel").visible = false;
    return 1;
}

function runDialog() {
    if (clicked("dlgOk") || Input.isKeyPressed("Enter")) { closeDialog(1); return; }
    if (clicked("dlgCancel") || Input.isKeyPressed("Escape")) { closeDialog(0); }
}

function closeDialog(answer) {
    dialogAnswer = answer;
    dialogOpen = 0;
    UI.find("dialogSheet").visible = false;
    UI.find("dlgCancel").visible = true;

    // Give focus back to the screen underneath, or a pad is left holding nothing.
    wantFocus = screen === S_MAIN ? "mPlay"
              : screen === S_SELECT ? ("card" + chosen)
              : screen === S_OPTIONS ? "optShakeUp" : "";
    takeFocus();
}

/** 1 confirmed, 0 cancelled, -1 still open or never asked. */
function dialogTaken(tag) {
    if (String(tag) !== dialogTag) { return -1; }
    return dialogOpen ? -1 : dialogAnswer;
}

// ===========================================================================
// Bits
// ===========================================================================

function clicked(name) {
    var node = UI.find(name);
    return node && node.clicked ? 1 : 0;
}

function setText(name, text) {
    var node = UI.find(name);
    if (node) { node.text = text; }
}

// ===========================================================================
// Called from elsewhere
// ===========================================================================

/** Puts the main menu back up -- what a run ending asks for. */
function openMain() { show(S_MAIN); return 1; }
function openSelect() { show(S_SELECT); return 1; }
function close() { show(S_NONE); return 1; }

function getScreen()   { return screen; }
function isDialogOpen(){ return dialogOpen; }
function getChosen()   { return chosen; }
function getShake()    { return optShake; }
function getDarkLimit(){ return optDark; }
function getVolume()   { return optVolume; }
function getCameraScale() { return optCamera; }
