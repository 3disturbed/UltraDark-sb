// -----------------------------------------------------------------------------
// EngineInfo — what engine this is, and what it is running on.
//
// The mirror of SexyBiscuit.Engine/EngineInfo.cs. VERSION here and <Version> in
// SexyBiscuit.Engine.csproj are pinned equal by html5/tests/graphics.test.js.
//
// There were four version strings agreeing by accident: the editor derived one from
// the assembly, the CookieJar's project context and the editor's project file each
// hard-coded '1.0.0', and this file's ancestor exported its own -- and no <Version>
// existed in any csproj, so the derived one was MSBuild's default. Four sources that
// happen to match are one silent divergence away from a cookie being refused, or
// accepted, for the wrong reason.
// -----------------------------------------------------------------------------

/** The engine version, as major.minor.patch. */
export const VERSION = '1.0.0';

/** The engine's name, for a title bar or a status line. */
export const NAME = 'SexyBiscuit';

/** Which renderer this build talks to. Resolved at run time: WebGL2, or 2D only. */
export function rendererName(gl = null) {
    return gl ? 'WebGL2' : 'Canvas 2D';
}

/** The browser and platform, as a player would recognise them. */
export function platformName() {
    const nav = globalThis.navigator;
    if (!nav) return 'Node';

    // userAgentData is the only non-deprecated source, and most browsers lack it, so
    // the user-agent string stays the fallback rather than the other way round.
    const brand = nav.userAgentData?.brands?.find((b) => !/Not.?A.?Brand/i.test(b.brand));
    const browser = brand ? `${brand.brand} ${brand.version}` : browserFromUserAgent(nav.userAgent ?? '');
    const platform = nav.userAgentData?.platform ?? nav.platform ?? 'Web';

    return `${platform} · ${browser}`;
}

/** The one-line summary the graphics menu shows at its foot, without the preset. */
export function statusLine(gl = null) {
    return `${NAME} ${VERSION} · ${rendererName(gl)} · ${platformName()}`;
}

function browserFromUserAgent(ua) {
    // Order matters: every one of these strings contains the ones below it.
    if (/Edg\//.test(ua)) return 'Edge';
    if (/OPR\//.test(ua)) return 'Opera';
    if (/Firefox\//.test(ua)) return 'Firefox';
    if (/Chrome\//.test(ua)) return 'Chrome';
    if (/Safari\//.test(ua)) return 'Safari';
    return 'Unknown browser';
}
