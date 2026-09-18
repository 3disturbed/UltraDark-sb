// -----------------------------------------------------------------------------
// The invite link, and how it leaves the game.
//
// 2.0.0's INVITE button drew the room server's link -- http://<host>/j/CODE -- in a toast on a
// canvas. Nobody could copy it, and a friend who retyped it got an empty page: an exported build's
// files name each other relatively, so served at /j/CODE the page asked for /j/engine/... and was
// handed itself. The owner could not get a multiplayer room going. engine/share.js is the cure, and
// it reads the page's globals when it loads, so each case here imports a fresh copy under its own.
//
//     node --test tests/share.test.mjs
// -----------------------------------------------------------------------------

import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { projectRoot } from "./helpers/engine.mjs";

const file = pathToFileURL(path.join(projectRoot, "Scripts/engine/share.js")).href;
let copy = 0;

/** share.js as a page with these globals would load it; they are taken away again afterwards. */
async function under(globals, run) {
  const had = {};
  for (const [name, value] of Object.entries(globals)) {
    had[name] = Object.getOwnPropertyDescriptor(globalThis, name);
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
  }
  try { return await run(await import(`${file}?copy=${++copy}`)); }
  finally {
    for (const name of Object.keys(globals)) {
      if (had[name]) Object.defineProperty(globalThis, name, had[name]); else delete globalThis[name];
    }
  }
}

/** A document that records what was put on it: enough of one for the link bar. */
function fakeDocument({ execCopies = false } = {}) {
  const make = (tag) => ({
    tag, style: {}, children: [], attrs: {}, listeners: {}, value: "", textContent: "",
    setAttribute(k, v) { this.attrs[k] = v; },
    addEventListener(type, fn) { this.listeners[type] = fn; },
    append(...kids) { this.children.push(...kids); },
    appendChild(kid) { this.children.push(kid); return kid; },
    remove() { this.removed = true; },
    select() { this.selected = true; },
    focus() { this.focused = true; },
  });
  const body = make("body");
  return { body, createElement: make, execCommand: (what) => what === "copy" && execCopies };
}

const page = (href) => { const u = new URL(href); return { protocol: u.protocol, origin: u.origin, pathname: u.pathname, href }; };

test("with no page under it -- a native build -- the seam says so and changes nothing", async () => {
  await under({ navigator: undefined, document: undefined, location: undefined }, async (share) => {
    assert.equal(share.pageLink("room=ABCD"), "", "no page, no page link: the caller keeps the room server's");
    assert.equal(await share.shareLink({ title: "t", text: "x", url: "https://x/?room=ABCD" }), "none");
  });
});

test("the link is the page the game is running on, https and all, with the room in its query", async () => {
  await under({ location: page("https://ultradark-sb.darksgames.app/?solo=1&wave=9") }, async (share) => {
    const link = share.pageLink("room=FSA7F5");
    assert.equal(link, "https://ultradark-sb.darksgames.app/?room=FSA7F5", "what it was opened with is not passed on");
    assert.ok(!/\/j\//.test(link), "never the /j/CODE shape: a static build cannot be served from there");
  });
  await under({ location: page("https://editor.darksgames.app/UltraDark-sb/play/") }, async (share) => {
    assert.equal(share.pageLink("room=ABCD"), "https://editor.darksgames.app/UltraDark-sb/play/?room=ABCD",
      "a page under a path keeps its path, so the editor's play page invites to itself");
  });
  await under({ location: { protocol: "file:", origin: "null", pathname: "/tmp/index.html" } }, async (share) => {
    assert.equal(share.pageLink("room=ABCD"), "", "a file on disk is not a link anyone else can open");
  });
});

test("on a desk the link goes to the clipboard", async () => {
  const written = [];
  const navigator = { clipboard: { writeText: async (v) => { written.push(v); } }, share: async () => { throw new Error("a desk does not open the sheet"); } };
  await under({ navigator, document: fakeDocument(), location: page("https://a.example/"), matchMedia: () => ({ matches: false }) }, async (share) => {
    assert.equal(await share.shareLink({ title: "UltraDark", text: "come", url: "https://a.example/?room=ABCD" }), "copied");
    assert.deepEqual(written, ["https://a.example/?room=ABCD"]);
  });
});

test("on a phone it goes to the share sheet, and closing the sheet is not a failure", async () => {
  const shared = [];
  const navigator = { share: async (what) => { shared.push(what); }, clipboard: { writeText: async () => { throw new Error("unused"); } } };
  await under({ navigator, document: fakeDocument(), location: page("https://a.example/"), matchMedia: () => ({ matches: true }) }, async (share) => {
    assert.equal(await share.shareLink({ title: "UltraDark", text: "come", url: "https://a.example/?room=ABCD" }), "shared");
    assert.equal(shared[0].url, "https://a.example/?room=ABCD");
  });
  const closing = { share: async () => { const e = new Error("closed"); e.name = "AbortError"; throw e; } };
  await under({ navigator: closing, document: fakeDocument(), location: page("https://a.example/"), matchMedia: () => ({ matches: true }) }, async (share) => {
    assert.equal(await share.shareLink({ title: "t", text: "x", url: "https://a.example/?room=ABCD" }), "shared");
  });
});

test("a browser that refuses the copy gets the link in a field it can be copied from", async () => {
  const document = fakeDocument({ execCopies: false });
  const navigator = { clipboard: { writeText: async () => { throw new Error("NotAllowedError"); } } };
  await under({ navigator, document, location: page("https://a.example/"), matchMedia: () => ({ matches: false }) }, async (share) => {
    assert.equal(await share.shareLink({ title: "t", text: "x", url: "https://a.example/?room=ABCD" }), "shown");
    const bar = document.body.children.find((el) => el.attrs.role === "dialog" && !el.removed);
    assert.ok(bar, "a bar went over the game");
    const field = bar.children.find((el) => el.tag === "input");
    assert.equal(field.value, "https://a.example/?room=ABCD", "holding the link");
    assert.equal(field.selected, true, "already selected, so Ctrl+C is all it takes");
    const copyButton = bar.children.find((el) => el.tag === "button" && el.textContent === "COPY");
    assert.ok(copyButton && copyButton.listeners.click, "with a COPY button whose own click the browser will honour");
    // The legacy path succeeding inside that click closes the matter.
    document.execCommand = (what) => what === "copy";
    await copyButton.listeners.click();
    assert.equal(copyButton.textContent, "COPIED");
  });
});
