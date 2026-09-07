# UltraDark-sb

A SexyBiscuit port of **UltraDark**, the twin-stick wave shooter at
<https://ultradark.darksgames.app>. The original is a networked co-op game;
this is the same game for one pilot, built against the shared scripting
contract.

**Its numbers are the original's, not an impression of them.** The roster, the
pilots, the mods, the wave budget, the arena and above all the damage scale come
from UltraDark's own `shared/` — the first cut of this port was written from
notes instead, and played like a different game wearing the same name.

## The scale, which is the thing to understand first

A pilot has **three hit points**. A bullet does **one** damage. A Drone has one,
a Brute six, BRUTE PRIME sixty. Every contact costs a third of your health and
buys you a full second of invulnerability.

Nothing is on a hundred-point bar. That single fact decides how the whole game
feels: you are not whittling anything down and nothing is whittling you down —
each exchange is a discrete, survivable, expensive event.

## The fantasy

You are one pilot in a 2048×1152 grid that never stops sending things at you.
You do not win. You get further than last time, and then the lights go out.

## The core loop

Fight a wave → clear it → **draft one of three mods** → fight a harder wave.
Every fifth wave is a **boss**, and clearing one opens the **core shop**. There
is no final wave: bosses cycle past 25, the budget keeps climbing, and a wipe is
the only way a run ends.

From **wave 16 the dark closes in** (`WAVE.DARK_START`) — the arena dims until
muzzle flash, explosions and the boss's own rings are most of what lights it.

## What a run is made of

- **12 enemies**, arriving on the original's schedule: Drone and Mite from the
  start, Weaver at 2, Spinner 3, Brute 4, Mortar 6, Sniper 7, Ghost 9, Leech 11,
  Magnet 12, Warden 13, Forge 14. Each keeps its own colour, size, cost and rule
  — a **Brute bursts into four Mites**, a **Spinner leaves a ring of bullets**
  where it died, a **Ghost is solid only while it fires**, a **Mortar draws its
  landing circle before it lands**, and a **Leech drains your multiplier rather
  than your health**.
- **5 bosses** — BRUTE PRIME, HEXAGON PRIME, FOUNDRY (vulnerable only while its
  doors are open), NULL SHEPHERD and THE ULTRADARK — cycling for ever.
- **8 pilots**, each with their own weapon and ability: BINK (SMG, Blink Volley),
  BLAZE (Scattergun, Flame Zone), AMBER (Blaster, Beacon Warp), DAVE (Cleaver,
  Gravity Well), SPARKS (Arc Gun, Tesla Pylon), RIGG (Blaster, Auto-Turret),
  KELVIN (Chill Lance, Frost Nova), HAWK (Railgun, Triple Rail).
- **37 mods** in four families — Ballistics, Field, Chassis, Echo — with the
  rarity-3 **cursed** ones that cost you something. All stackable; nothing is
  ever deduplicated.
- **Cores**, spent in the post-boss shop. **Consumables** on the original's
  weighted table: Repair Kit, Overshield, Frenzy Core, Stasis Charge, Bomb Cell.
- A **multiplier** to ×10 that builds at +0.12 a kill, decays after three
  quiet seconds, and is **halved every time you are hit**.

## What "fun" would mean in the first playtest

1. **Wave 1 to wave 10 should feel different**, not the same wave with more of
   it. If a tester cannot name what changed, the roster is not doing its job.
2. **The draft should be a real decision.** If one card is obviously correct
   every time, the pool is wrong.
3. **Wave 16 should be a moment.**

## What this port is not

Not co-op. `Network` is a stub on both engines, so the lobby, the invite links,
the shared multiplier, the revives and the banked-score insurance are out of
scope, and boss health is the solo ×1.0 of the original's curve.

No sound: `Audio` is in the contract but there is not one audio file in the
engine repository.

Shapes are approximated. The original draws a circle, a diamond, a gear, a
crescent; the contract draws tinted rectangles, so silhouette is carried by size
and aspect while the colours are exactly the original's.

## What changed once the contract grew a viewport and a font

The first cut of this game had no text anywhere, because the scripting contract
had none: the HUD was five coloured bars floating over the ship, and a draft card
was a colour and a row of pips whose meaning only appeared in the log. `UI.panel`,
`UI.label` and `UI.width` changed what the game could be, so:

- the HUD is a real panel — wave, score, cores, multiplier, and five vitals;
- a draft card carries the mod's **name**, what it does, its family and how many
  you already hold, and can be clicked as well as keyed;
- being hit shakes the camera and flashes the screen, and a boss dying stops time
  for a moment;
- Escape pauses, and dying says so on the screen rather than only in a log;
- mouse aim is exact instead of assuming a 1280x720 window.

The old world-space HUD is gone rather than kept as an option. It was a
workaround, and workarounds should not outlive the problem.

## What it deliberately is not

Not co-op. The scripting contract's `Network` is a stub on both engines, so the
lobby, the invite links, the shared multiplier and the revives are out of scope
for this port; the run is one pilot. Not text, either — there is no font in the
contract, so the wave number, the score and the draft are all told in colour,
shape and the log.
