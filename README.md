# UltraDark-sb

UltraDark — the neon co-op twin-stick wave shooter at <https://ultradark.darksgames.app> —
on the SexyBiscuit engine, with a 3D stage of MakeChibi pilots over the original's own
simulation. Read `BRIEF.md` first: it says what the game is. This says how it is built and
how to check it.

**Playable:** <https://ultradark-sb.darksgames.app> (the web export), and from the fleet box's
editor at `https://editor.darksgames.app/UltraDark-sb/play/`.

## How it is put together

Three layers, and the line between them is the point.

| Folder | What it is | Where it came from |
|---|---|---|
| `Scripts/shared/`, `Scripts/server/` | the rules: the 30 Hz server-authoritative simulation, 17 enemies, 8 pilots, 40 mods, the Core Shop, the eight-boss cycle, rooms, ranked boards | UltraDark's own files at `7eee633`, **verbatim** — change a number here only because the original changed |
| `Scripts/client/`, `Scripts/engine/` | the client on the engine's contract: the world model, the 2D neon renderer on `Draw`, the screens on `UI`, input, the synth, the sockets over `Network` | the engine's `TwinStick2D` template (DarkShapes), brought forward to the same commit |
| `Scripts/stage/` | the 3D stage: chibi pilots and bosses, the swarm as geometry, the lit deck, the camera, the dark, the projection the 2D layers are drawn through | new here |

`Scripts/DarkShapes.js` is the one script the scene runs. Whichever machine is the session's
authority serves the rooms (a solo run, a listen host, a dedicated server); every machine plays.

**The stage reads and never writes.** `stage/stage.js` takes the client's world model once a frame
(after `game.frame`), mirrors it into 3D actors, and hands `render.js` a projection so the bullets,
tracers, the shroud, the tags and the popups land on the 3D things. Nothing in the simulation or
the client knows the stage exists, and Settings → VIEW → CLASSIC 2D is the whole original picture.

Units: 32 arena pixels to the metre (`stage/units.js`). Game x → world X, game y → world Z.

## Running it

From the engine checkout beside this folder (`/srv/darksgames/engine` on the fleet box):

```bash
node html5/tools/validate.js ../projects/UltraDark-sb --strict
node html5/tools/serve.js --projects ../projects          # then /UltraDark-sb/play/
```

Two developer launch parameters: `?solo=1` presses SOLO RUN as the page opens; `?wave=N` jumps a
**solo** run to wave N once it is up (never a shared room). `?stagedebug=1` prints the rig.

## Checking it

```bash
npm test                      # node --test tests/*.test.mjs
```

| Test | What it proves |
|---|---|
| `tests/stage.test.mjs` | the whole game headless on the real engine host: the hangar (one pilot, then a roster of eight), a solo run to wave one, one body per enemy in the snapshot, the pilot built and armed and facing the aim, every one of the eight bosses on the deck, the sun down from wave sixteen, no leaks, the 2D view a setting away |
| `tests/sim.test.mjs` | the original's bot harness over this project's linked program: solo and a squad, the deep waves, the first five bosses in order, and the raid bosses on the eight-boss cycle |
| `tests/smoke.test.mjs` | the template smoke: 120 frames, no host, no script error |

`tests/helpers/boot.mjs` is the harness: `EngineHost` headless with a `DiskAssetManager`, the
entry script with a probe appended (press SOLO RUN, read the world model and the stage, jump the
authority's simulation to a wave, seat bots), and the host's `InputManager` proxied so a check can
hold keys and the mouse.

**No gate here can see a picture.** The visual check is a headless Chromium on the box (see the
memory notes) opening `/UltraDark-sb/play/?solo=1&wave=N`; under the software renderer it runs at
about two frames a second and the engine clamps each frame's delta, so wait a minute for a wave to
be a wave. A native frame is `dist/Linux_x64/UltraDark --param solo=1 --param wave=6` under Xvfb.

## Deploying

The web export is the game: `node html5/tools/export.js ../projects/UltraDark-sb --pwa --dg
ultradark-sb --out <dir> --no-zip`, rsynced into `/srv/darksgames/games/ultradark-sb/public/`
(nginx serves it; no restart). The page stamp and the service worker's cache both carry the commit.
A multiplayer export needs the two relay locations on the vhost (`= /ws` and `/api/rooms` to
`sb-rooms` on 3901) as the arena's has, and the hub's catalogue entry needs the join fields.

Native builds: `sbengine --project ../projects/UltraDark-sb --all --config release`; publish with
`--upload` and the slug's token (`BuildSettings.json`).

## Things worth knowing before changing it

- **A built chibi faces its actor's forward, −Z.** The rig is laid out facing +Z and the engine's
  builder hangs it on the actor under a half-turn (engine `7912b373`). `units.js`'s `yawForAim`
  turns that front along a game aim angle. It was written for a +Z face a few minutes before that
  commit was pulled, and 2.0.0 shipped with every pilot backwards, firing out of their own spine —
  past a test that checked the actor's rotation, which is only the formula read back.
  `tests/stage.test.mjs` now measures the body: the hip line, the face, and where the muzzle hangs.
- **The sky's rim light follows the camera's pitch** (`arena.js`, `setViewPitch`). The rim is
  (1 − N·V)³ and no material opts out of it, so from the hangar's shallow pitch the whole floor took
  it and the game opened on a pale blue room. It is nearly out in the hangar and full in the fight.
- **The invite link is this page's own address with `?room=CODE`** (`client/net.js`, `linkFor`), never
  the room server's. The relay writes every link as `http://<host>/j/CODE`, and an exported build is
  static files that name each other relatively: served at `/j/CODE` the page asks for
  `/j/engine/...`, is handed itself, and the friend sees an empty page. The hub's Join button already
  used the query form; the vhost also redirects `/j/CODE` to `/?room=CODE` for links of the old shape.
- **Sharing and copying go through `engine/share.js`**, not the contract, which has neither. A toast
  is drawn on a canvas and cannot be selected, so 2.0.0's INVITE gave the player a link to retype. A
  script is compiled in the page's realm, so the seam feels for `navigator`/`document`: share sheet on
  a phone, clipboard on a desk, a real text field with COPY where the browser refuses both, and
  "none" natively, where the link is still shown. `tests/share.test.mjs` fakes each of those pages.
- **`ClearColour` in ProjectSettings.json is read by neither engine** as of engine `7912b373`: the
  browser parses it and `loadProject` drops it, and the C# reader has no case for it. The stage
  covers the whole frame, so it only shows before the scripts have built the stage.
- **The stage's projection is the engine's.** `camera.js` projects with a vertical field of view
  and the engine's conventions; a headless check compared it to `Camera3D.worldToScreen` to the pixel.
- **Startwave keeps the last boss alive.** A jumped wave adds a boss beside the one already there;
  the test asserts arrivals, not counts.
- **Under the software renderer stage time runs slow.** Not a bug in the game: the browser's frame
  takes half a second and the engine clamps the delta.
- **Content is the original's.** `shared/` and `server/` are read from UltraDark's own tree; the
  eight-boss cycle, rank pricing and the ready check are theirs.
