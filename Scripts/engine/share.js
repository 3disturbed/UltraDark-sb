// -----------------------------------------------------------------------------
// share — a link, out of the game and into a friend's hands.
//
// UltraDark's page shared a link three ways: the share sheet where there was one, the
// clipboard where there was not, and the link itself in a toast as the last resort.
// The engine's contract has neither a share sheet nor a clipboard, so DarkShapes kept
// only the last resort -- and on this engine a toast is drawn on a canvas, where nobody
// can select it. A lobby's INVITE button therefore handed the player a link they could
// only retype, and nobody could get a room going.
//
// In a browser a script is compiled in the page's own realm, so the page's navigator
// and document are within reach. This seam uses them where they exist and says so
// where they do not: a native build has none, answers "none", and the caller shows the
// link as before. Nothing here is in the contract, so everything is felt for first.
//
//   shareLink({ title, text, url }) -> "shared" | "copied" | "shown" | "none"
//   pageLink(query)                 -> this page's own address with that query, or ""
// -----------------------------------------------------------------------------

const nav = typeof navigator !== "undefined" ? navigator : null;
const doc = typeof document !== "undefined" ? document : null;
const loc = typeof location !== "undefined" ? location : null;

/**
 * The address of the page the game is running on, carrying `query` and nothing else it was
 * opened with: a page that is running the game is a page a friend's browser can run it on.
 * Empty where there is no page.
 */
export function pageLink(query) {
  if (loc === null || !/^https?:$/.test(String(loc.protocol))) return "";
  return `${loc.origin}${loc.pathname}${query ? "?" + query : ""}`;
}

/** Hands a link to the player's friend by the best way this machine has. */
export async function shareLink({ title, text, url }) {
  if (!url || nav === null || doc === null) return "none";

  // A phone's share sheet is where its owner's chats are; a desk's is a detour from Ctrl+V.
  const thumb = typeof matchMedia === "function" && matchMedia("(pointer: coarse)").matches;
  if (thumb && typeof nav.share === "function") {
    try { await nav.share({ title, text, url }); return "shared"; }
    catch (e) { if (e && e.name === "AbortError") return "shared"; /* they looked and closed it */ }
  }

  if (await copyText(url)) return "copied";

  // Refused: the press reached this script a frame after the browser saw it, and some browsers
  // only honour a copy made inside the event itself. So the link goes where it CAN be selected,
  // with a button whose own click is that event.
  showLinkBar(url);
  return "shown";
}

async function copyText(value) {
  try {
    if (nav.clipboard && typeof nav.clipboard.writeText === "function") {
      await nav.clipboard.writeText(value);
      return true;
    }
  } catch { /* no activation, no permission, or not a secure page: try the old way */ }
  try {
    const area = doc.createElement("textarea");
    area.value = value;
    area.setAttribute("readonly", "");
    area.style.cssText = "position:fixed;left:-9999px;top:0;opacity:0";
    doc.body.appendChild(area);
    area.select();
    const ok = doc.execCommand("copy") === true;
    area.remove();
    return ok;
  } catch { return false; }
}

let bar = null;

/** The link in a real text field over the game, selected, with COPY beside it. */
function showLinkBar(url) {
  if (bar !== null) bar.remove();
  bar = doc.createElement("div");
  bar.setAttribute("role", "dialog");
  bar.setAttribute("aria-label", "Room link");
  bar.style.cssText = "position:fixed;left:50%;bottom:24px;transform:translateX(-50%);z-index:2147483000;"
    + "display:flex;gap:8px;align-items:center;max-width:min(92vw,720px);padding:10px 12px;border-radius:12px;"
    + "background:rgba(8,8,16,.94);border:1px solid rgba(57,240,255,.45);box-shadow:0 10px 36px rgba(0,0,0,.55);"
    + "font:600 14px/1 system-ui,-apple-system,'Segoe UI',sans-serif;color:#eceded";

  const field = doc.createElement("input");
  field.type = "text";
  field.readOnly = true;
  field.value = url;
  field.style.cssText = "flex:1 1 auto;min-width:0;width:46ch;padding:9px 10px;border-radius:8px;border:1px solid rgba(255,255,255,.16);"
    + "background:#0a0a14;color:#39f0ff;font:500 13px/1.2 ui-monospace,Menlo,Consolas,monospace";

  const button = (label) => {
    const b = doc.createElement("button");
    b.type = "button";
    b.textContent = label;
    b.style.cssText = "flex:0 0 auto;padding:9px 14px;border-radius:8px;border:1px solid rgba(57,240,255,.5);"
      + "background:#0d2a33;color:#eafcff;font:inherit;letter-spacing:.06em;cursor:pointer";
    return b;
  };
  const copy = button("COPY");
  const close = button("✕");
  close.setAttribute("aria-label", "Close");

  const shut = () => { if (bar !== null) { bar.remove(); bar = null; } };
  copy.addEventListener("click", async () => {
    field.select();
    copy.textContent = (await copyText(url)) ? "COPIED" : "PRESS CTRL+C";
    if (copy.textContent === "COPIED") setTimeout(shut, 1100);
  });
  close.addEventListener("click", shut);
  // The game listens for keys on the window; a key meant for this field should not also fire a gun.
  bar.addEventListener("keydown", (e) => { if (e.key === "Escape") shut(); e.stopPropagation(); });
  for (const type of ["pointerdown", "pointerup", "mousedown", "mouseup", "touchstart", "touchend"]) {
    bar.addEventListener(type, (e) => e.stopPropagation());
  }

  bar.append(field, copy, close);
  doc.body.appendChild(bar);
  field.focus();
  field.select();
}
