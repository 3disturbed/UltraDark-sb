# UltraDark-sb 2.0 — the brief

UltraDark, the neon co-op twin-stick wave shooter at ultradark.darksgames.app, on the
SexyBiscuit engine: the original's own 30 Hz server-authoritative simulation, waves,
eight pilots, forty mods, the Core Shop, eight bosses cycling for ever, rooms a friend
joins by code, Daily Dark, challenge links and ranked boards — and, over all of it, a
**3D stage**: every pilot is a MakeChibi character in the sci-fi kit, painted in their
class colour, who runs, dashes, flinches, casts and dies; the humanoid bosses are giant
chibis with faces that change with the fight; the swarm is glowing geometry; the arena
is a lit metal deck under a black sky; and from wave 16 the lights go out for real,
so your own muzzle light and the boss's glow are what you see by.

## The fantasy

You and up to seven friends are pilots dropped onto a 2048x1152 deck that never stops
sending things at you. You do not win. You get further than last time, and then the
dark closes in.

## The core loop

Fight a wave, clear it, **draft one of three mods**, fight a harder wave. Every fifth
wave is a **boss**; clearing one opens the **Core Shop** and the next wave waits for
the squad's ready check. There is no final wave.

## The scale

A pilot has **three hit points**. A bullet does **one** damage. Every contact costs a
third of your health and buys a second of invulnerability. Nothing is on a hundred-point
bar, and that is what makes every exchange a discrete, expensive event.

## What is the original's and what is new

The rules are `Scripts/shared/` and `Scripts/server/`, taken verbatim from the live
UltraDark (commit 7eee633): change a number there only because the original changed.
`Scripts/client/` is DarkShapes' port of the original client onto the engine's Draw,
UI, Input and Network, brought forward to the same commit. `Scripts/stage/` is new:
the 3D presentation, which reads the client's world model and never touches the rules.

## What "fun" means in the first playtest

1. Wave 1 to wave 10 should feel different, not the same wave with more of it.
2. The draft should be a real decision.
3. Wave 16 should be a moment — and in 3D, a *dark* one.
4. A friend should be able to join from a code and see the same fight.
