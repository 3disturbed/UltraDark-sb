# The CookieJar

Reusable modules for SexyBiscuit games. One folder per **cookie**: some source, whatever assets and
scene fragments it needs, and an `AGENT.md` that says how to wire it up once it is installed.

Install one from the editor's Cookie Jar panel, or ask the assistant — `search_cookies` finds it and
`install_cookie` copies it into the open project, builds it, and hands back the instructions.

## What is in the jar

Thirteen cookies. **The `engines` field is the first thing to check** — a `csharp` cookie is no use
in a phase 1 HTML5 prototype, which is where every game starts, and a `js` cookie is not drawn in
a native build.

### JavaScript — for a phase 1 prototype

These are scripts in `Scripts/`, written against the shared scripting contract, so they run
unchanged in the browser and under Jint. Nothing to compile.

| Cookie | What it gives you |
|---|---|
| **`noise-and-hearing`** | Enemies that hunt by ear. A sound is broadcast as a *position and a radius*, and listeners walk to where it was — not to whoever made it. That one restraint is what makes running away, throwing something and standing still all real choices, without a "distraction system" existing. Includes a drop-in investigator AI with a sight cone you can slip behind. |
| **`floating-status-bars`** | Health, stamina, hunger — as bars above an actor's head, fed by naming getters on that actor's script. In world space because the contract exposes no viewport, so a script cannot find the screen edge; following the actor turns that limitation into the right answer. |
| **`day-night-cycle`** | A clock that becomes pressure. Publishes a 0–1 darkness curve and a day number for other systems to read, announces each phase once, and dims the world through an overlay pinned to the player. It deliberately decides nothing about what night *means*. |
| **`wave-director`** | Endless escalating waves, a boss on a cadence, and the stall-breaker that stops a wave being unfinishable. A wave is a *budget*, not a headcount, so it gets harder rather than longer; the budget converts into spawns no faster than they are cleared, which is a pacing decision and a frame-cost ceiling at once. It owns the shape of a run and nothing about its content. |
| **`projectile-pool`** | Hundreds of bullets in one script over a pool of reused actors — and a swept collision test, because a projectile checked only where it lands walks straight through anything narrower than its own speed. A railgun at 1750 px/s misses every target at every range, silently, and no test on either side of the engine says a word. |
| **`entity-ledger`** | Forty enemies in one script instead of forty script engines — a row per entity, actors that carry only a sprite and a collider, and the four rules that make a ledger safe: removal swaps, iterate backwards, a cascade must not recurse, and colliders exist so projectiles can find what they hit. |
| **`stacking-upgrades`** | A bag of upgrades that stack, recomputed from scratch every time and never deduplicated, so two copies are exactly two applications. Effects are a data table rather than a switch, so the cards can read what the maths reads. |
| **`pickup-drops`** | Coins and hearts on the floor: pooled, timed, blinking before they expire, and magnetised by an upgrade the collector holds. Collecting one is a single `onPickup(kind, value)`. |
| **`draft-picker`** | Stop the round and ask a question: one of N cards, chosen by number key or by walking into one. In world space because there is no viewport, and said in colour and pips because there is no font. The board knows nothing about what is on the cards. |

### C# — for the native engine

Components in `Source/`, compiled against the engine in this repository. Three of the four build on
`input-mapping`, so installing one pulls it in.

| Cookie | Requires | What it gives you |
|---|---|---|
| **`input-mapping`** | — | Keyboard, mouse and gamepad bindings a player can change, saved between runs. Rebinding, binding descriptions for on-screen prompts, and per-player device ownership so it works unchanged in couch co-op. |
| **`touch-controls`** | `input-mapping` | On-screen sticks and buttons pushed through the virtual input layer, so a controller written for a keyboard needs no changes. Not drawn in the browser build — the HTML5 runtime has its own overlay in the page. |
| **`couch-coop`** | `input-mapping` | Several players on one machine, joining by pressing a button, with a camera rig that frames everyone at once. Split-screen is separate engine work. |
| **`remote-players`** | `input-mapping` | Host or join over the network: a client that connects is spawned, possessed and scoreboarded, and remote pawns interpolate at the 20 Hz send rate. |

Each folder's `AGENT.md` is the real documentation — what it gives you, how to wire it up, what to
tune, and what it deliberately does not do. Read that before installing, not after.

## Adding a cookie

The easiest way is to build the thing in a game first, then bake it: Tools ▸ Bake Cookie from this
Project, or `bake_cookie`. That fills in the manifest, moves the source into the cookie's own
namespace and writes a starter `AGENT.md` for you to improve.

By hand, a cookie is:

```
<id>/
  cookie.json      id (kebab-case, matching the folder), name, version, summary, engines, provides
  AGENT.md         required: what it gives you and how to wire it up
  README.md        optional
  Source/          C#, in namespace Cookies.<PascalId>
  Scripts/         JavaScript, against the shared scripting contract
  Scenes/          .scene and .prefab fragments
  Assets/          textures, audio, models
  Config/          json the AGENT.md tells the caller to use
```

Rules worth knowing before you write one:

- **The id must equal the folder name**, and be kebab-case.
- **C# lives in `Cookies.<PascalId>`** and is copied verbatim. Nothing is substituted on install, so
  the file in the jar and the file in the project are byte-identical — which is what lets an
  uninstall tell a file it wrote from a file the author has since edited.
- **Asset references are cookie-relative.** Write `Assets/brick.png`; installing rewrites it to
  wherever the cookie's assets landed in the project.
- **`provides` is load-bearing**, not decoration. It makes the catalogue searchable without opening
  every file, and it is how a clash between two cookies exporting the same component name is caught
  before anything is written.
- **Everything here is compiled in CI** against the engine in this repository, so a cookie that
  stops building is a broken build rather than a surprise for whoever installs it next.
- **A JavaScript cookie declares `"engines": ["js"]`** and puts its scripts in `Scripts/`, written
  against the shared scripting contract like any other game script. There is nothing to compile,
  so the check is the validator instead: drop the scripts into a throwaway project, wire them into
  a scene the way the `AGENT.md` says to, and run
  `npm run validate -- <dir> --strict` from `html5/`. A cookie whose own documentation does not
  validate is worse than no cookie.
- **Add it to the list at the top of this file** in the same commit. A catalogue nobody can read
  without opening seven folders is the thing the `provides` field and the summaries exist to avoid.

Phase 1 is JavaScript-only and it is where every game starts, so a mechanic worth reusing is
usually worth a `js` cookie before it is worth a C# one.

A cookie is one mechanic. A whole game start is a project template, and those live in `Templates/`.
