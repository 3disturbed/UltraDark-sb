// -----------------------------------------------------------------------------
// Loading the two Darks Games SDKs.
//
// They are hosted, versioned, classic scripts — not modules and not bundled —
// so a game gets fixes and new social features without rebuilding. The engine
// only ever *uses* them; it never reimplements them, because the hub's copy is
// the source of truth for the postMessage bridge and the token flow.
//
// Order matters. `dg-overlay.v1.js` strips `?dg_party` and `?dg_launch` out of
// the URL the moment it executes, so it has to run before any game code reads
// `location` — which is why the account SDK is requested first and awaited.
// -----------------------------------------------------------------------------

/** Where the hub serves the SDKs and the API. Overridable for a staging hub. */
export const DG_ORIGIN = 'https://darksgames.app';

const ACCOUNT_SDK = '/sdk/dg-account.v1.js';
const OVERLAY_SDK = '/sdk/dg-overlay.v1.js';

const loaded = new Map();

/**
 * Loads one script once, resolving when it has run.
 *
 * A failure resolves rather than rejects: a game whose player is offline, or
 * behind a filter that blocks the hub, must still start. Everything downstream
 * treats a missing SDK as "signed out", which is a state it has to handle anyway.
 */
export function loadScript(url, { document: doc = globalThis.document, timeoutMs = 8000 } = {}) {
    if (loaded.has(url)) return loaded.get(url);

    const promise = new Promise((resolve) => {
        if (!doc?.head) { resolve(false); return; }

        const existing = doc.querySelector(`script[src="${url}"]`);
        if (existing) { resolve(true); return; }

        const script = doc.createElement('script');
        script.src = url;
        script.async = false;        // preserve order between the two SDKs
        script.crossOrigin = 'anonymous';

        // A hung request must not hold the game's boot forever; a slow CDN is a
        // signed-out session, not a black screen.
        const timer = setTimeout(() => resolve(false), timeoutMs);
        script.onload = () => { clearTimeout(timer); resolve(true); };
        script.onerror = () => { clearTimeout(timer); resolve(false); };

        doc.head.append(script);
    });

    loaded.set(url, promise);
    return promise;
}

/**
 * Loads both SDKs, in order, and returns what actually arrived.
 *
 * @returns {Promise<{account: object|null, overlay: object|null}>}
 */
export async function loadDarksGamesSdks({ origin = DG_ORIGIN, scope = globalThis } = {}) {
    // Already on the page — the recommended deployment puts both tags in the HTML,
    // and the export pipeline writes them. Nothing to fetch.
    if (scope.DGAccount && scope.DGOverlay) {
        return { account: scope.DGAccount, overlay: scope.DGOverlay };
    }

    await loadScript(`${origin}${ACCOUNT_SDK}`);
    await loadScript(`${origin}${OVERLAY_SDK}`);

    return { account: scope.DGAccount ?? null, overlay: scope.DGOverlay ?? null };
}

/** Forgets what has been loaded. Tests only. */
export function resetSdkCache() { loaded.clear(); }
