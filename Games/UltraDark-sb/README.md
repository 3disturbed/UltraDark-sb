# UltraDark-sb

A SexyBiscuit port of **UltraDark**, the twin-stick wave shooter at
`ultradark.darksgames.app`. Read `BRIEF.md` first — it says what the game is
meant to be. This says how it is built and how to check it.

**Playable now:** <https://ultradark-sb.darksgames.app>

## Running it

```bash
cd html5 && node tools/serve.js --watch
# then open the URL it prints with ?project=/Games/UltraDark-sb/&scene=Scenes/Ultradark
```

## Checking it

```bash
node tools/check.mjs          # every gate, cheapest first
```

Individually, from `Games/UltraDark-sb/`:

| | |
|---|---|
| `node --test tools/checks.mjs` | 24 tests of real situations in the real scene |
| `node tools/draw-order.mjs` | which side of the dark every layer is on |
| `node tools/perf.mjs` | what a frame costs at the loads the game can produce |
| `node tools/soak.mjs --minutes 3` | play it headlessly and report |
| `node tools/soak.mjs --wave 14 --minutes 6` | …starting in the dark |

`tools/harness.mjs` is what the last four share: it boots the real scene with a
real `PhysicsSystem2D`, an asset loader backed by the filesystem and an input
device a bot can drive. All three matter — without physics no shot can hit
anything, without the loader the boss is an actor with no behaviour, and without
a touch stub `Input.joystickX` throws.

## How it is put together

Nine scripts, and the split is about **cost**. A `ScriptComponent` is a script
engine, so anything that exists in dozens does not get one.

| Script | What it is |
|---|---|
| `Director.js` | the run: the arena, the hangar, score, cores, the multiplier, pickups, deployables, effects, the draft, the shop, the dark, and the content half of the wave contract |
| `WaveDirector.js` | the shape of a run — budget, cadence, bosses, the stall-breaker. The `wave-director` cookie |
| `Swarm.js` | all twelve enemy kinds, as one ledger of parallel arrays. No enemy has a script |
| `ProjectilePool.js` | every projectile, pooled and swept. The `projectile-pool` cookie |
| `Boss.js` | the five bosses. One actor at a time, so one script engine is a fair trade |
| `Pilot.js` | the ship: eight pilots, mods, abilities, consumables, damage |
| `DraftBoard.js` | one-of-N cards, in screen space with real text. The `draft-picker` cookie |
| `DayNightCycle.js` | the dark. The `day-night-cycle` cookie, driven by wave rather than by a clock |
| `Hud.js` | the HUD: run stats, vitals, messages, pause and game-over. The `hud-kit` cookie |
| `Effects.js` | shake, flash, hit-stop, fade. The `screen-effects` cookie |
| `Upgrades.js` | the 24 mods and the stats they add up to. The `stacking-upgrades` cookie |
| `PickupDrops.js` | cores and consumables on the floor. The `pickup-drops` cookie |
| `CameraRig.js` | follow, with a little lead. It does **not** shake; `screen-effects` owns that |

Six of those were **baked back into the CookieJar** from this game — `wave-director`,
`projectile-pool`, `draft-picker`, `entity-ledger`, `stacking-upgrades` and `pickup-drops` — and
three more are used as they came: `day-night-cycle`, `hud-kit` and `screen-effects`.

`Swarm.js` is the worked example the `entity-ledger` cookie was generalised from; it keeps its
twelve kinds and the boss-aware `nearestX` that a generic ledger cannot have.

## Things worth knowing before changing it

**Draw order is a design decision here, not a detail.** The dark is a sprite at
`layerDepth` 0.80. Everything below it is dimmed by the night; everything above
it is a light source that survives the night — muzzle flash, explosions, the
boss's rings, the HUD, the draft. `tools/draw-order.mjs` is the gate, and it
fails on a layer that is on the wrong side, on one name drawn at two depths, and
on a sprite nobody has classified.

**Mods stack. Nothing ever deduplicates them.** `p_mods` is a list with
duplicates left in and `computeStats()` recomputes every stat from that list
whenever it changes. No stat is edited anywhere else, so a stack is always
exactly N applications.

**There is no last wave.** `bossFor` cycles for ever and the budget keeps
climbing. A wipe is the only way a run ends.

**Every tuning number is in a labelled block at the top of its script.** The
feedback loop on a prototype is "make it faster", and that should be a one-line
diff.

**The HUD and the draft are screen-space UI, not sprites.** They were world-space
before the contract had a viewport or a font, and `tools/draw-order.mjs` now fails
if one comes back into the world — five bars over the pilot's head cannot be put
on the wrong side of the dark, but they also cannot say which bar is which.

**Nothing caches an upgrade stat.** `Upgrades.js` recomputes every stat from the
list of taken mods whenever it changes, and a cross-script read of it needs a
default — an uninitialised script returns `undefined`, and every number derived
from that is silently `NaN`.

## Six bugs this game's harnesses found that nothing else could

Each of these passed `validate --strict` and the engine's own 110 tests.

1. **A wave that could not end.** A turret has speed 0 and spawned at the arena
   edge, so it never reached the player. The wave it unlocks on was a wall a
   twenty-minute run never got past. Rooted enemies now spawn *in* the fight,
   and `wave-director` has a stall-breaker.
2. **Auto-aim could not see a boss.** The boss is one actor with its own script,
   not a row in the swarm ledger, so the default aim mode could not target it.
   On a boss wave with the adds cleared there was nothing to shoot.
3. **A railgun that missed everything.** At 1750 px/s a frame is 29 px against a
   24 px capture window, so the shot stepped straight over its target at every
   range. Projectiles are swept now.
4. **A chain reaction that blew the stack.** `kill → shockwave → kill` recursed
   once per link. What that looks like is not a crash — the engine catches it —
   it is every script hook in the frame failing afterwards, so damage silently
   stops landing and a boss never dies. Blasts are queued and drained flat.
5. **An enemy standing on you was immune to your gun.** Shots left the ship 17 px
   ahead, past the collider of anything in contact. It reads as "sometimes I
   cannot kill the thing on me", and only the slow-cadence pilots ever met it.
6. **Every pilot stat was `undefined` for a run.** The pilot reads its stats from
   the upgrades script the moment it *finds* that actor, which is not the moment
   that actor is ready — and a call into an uninitialised script returns
   `undefined`, so every derived number was `NaN` with no error anywhere.

## What is deliberately not here

Not co-op: the contract's `Network` is a stub on both engines, so the lobby,
invite links, shared multiplier and revives of the original are out of scope and
the run is one pilot.

No sound. `Audio.play` and `Audio.playOneShot` are in the contract, but there is
not a single audio file anywhere in the engine repository or the jar, and a
shooter's audio is not something to fake with silence.
