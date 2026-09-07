# UltraDark-sb

A SexyBiscuit port of **UltraDark**, the twin-stick wave shooter that runs at
`ultradark.darksgames.app`. The original is a networked co-op game; this is the
same fantasy rebuilt as a single-pilot run against the shared scripting
contract, which has no transport and no text.

## The fantasy

You are one pilot in a grid that never stops sending things at you. You do not
win. You get further than last time, and then the lights go out.

## The core loop

Fight a wave → clear it → **draft one of three mods** → fight a harder wave.
Every fifth wave is a **boss**, and clearing one opens the **core shop** before
the next wave starts. There is no final wave: bosses cycle, the scaling keeps
climbing, and a wipe is the only way a run ends.

From **wave 16 the dark closes in** — the arena dims until only muzzle flash,
explosions and your own hull light it. That is the title, and it is the thing
the whole build is arranged around: draw order is a design decision here, not a
detail.

## What a run is made of

- **8 pilots**, each with its own weapon and one ability: BINK (SMG), BLAZE
  (shotgun), AMBER (beacon warp + heal), DAVE (melee cleave tank), SPARKS
  (chain lightning), RIGG (turrets), KELVIN (freeze), HAWK (railgun).
- **12 enemy kinds** — chasers, rushers, spitters, snipers that charge a beam,
  phasing ghosts, wardens that shield their neighbours, forges that build more,
  magnets that drag you in, leeches, bruisers, swarmlings, turrets.
- **5 bosses** — BRUTE PRIME, HEX PRIME, FOUNDRY, NULL SHEPHERD and THE
  ULTRADARK, which turns the lights off entirely.
- **24 mods** drafted three at a time, all stackable. Duplicates are the point;
  nothing is ever deduplicated.
- **Cores** (the currency bosses drop) spent in a shop of small, cheap,
  stackable upgrades.
- **Consumables** — repair, overshield, frenzy, stasis, bomb — carried three at
  a time and dropped by the chunky enemies.
- A **kill multiplier** you build by killing and lose by being hit, and
  **Overdrive** when it maxes.

## What "fun" would mean in the first playtest

Three things, in this order:

1. **Wave 1 to wave 10 should feel different**, not the same wave with more
   health. If a tester cannot name what changed, the roster is not doing its job.
2. **The draft should be a real decision.** If one card is obviously correct
   every time, the pool is wrong.
3. **Wave 16 should be a moment.** The dark arriving should change how the
   tester plays — slower, closer, using the muzzle flash — not just make it
   harder to see.

Everything else is tuning. Every number that matters is in a labelled block at
the top of its script.

## What it deliberately is not

Not co-op. The scripting contract's `Network` is a stub on both engines, so the
lobby, the invite links, the shared multiplier and the revives are out of scope
for this port; the run is one pilot. Not text, either — there is no font in the
contract, so the wave number, the score and the draft are all told in colour,
shape and the log.
