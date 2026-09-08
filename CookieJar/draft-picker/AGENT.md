# Draft Picker

Stop the round and ask a question: one of N cards, in screen space, with real text.

## What changed in 2.0

This used to be world-space sprites labelled with a **colour and a row of pips**, because the
scripting contract had no viewport and no font — a card could not be put on the screen and could
not be given a name. The `UI` globals removed both problems, so a card says what it is in words,
and it can be clicked.

The old walk-into-a-card selection is gone with it. It existed because number keys do not work on
a phone and there was nothing else to hit; a button works better and everywhere. Anything clever
about the old version was a workaround, and workarounds should not outlive the problem.

## Wiring it up

```
Draft   (tag "Draft")   Scripts/DraftBoard.js
```

```js
var board = Scene.findFirstByTag("Draft").getComponent("ScriptComponent");

board.call("setHeading", "DRAFT");
board.call("setHint", "1 2 3, or click a card");
board.call("open", 3);

board.call("setCard", 0, "HEAVY SLUG", "+12% damage", "OFFENCE", "#e24646");
//                    slot  title       what it does   group      strip colour
board.call("setFooter", 0, "owned x2");

// then, each frame, until it answers
var picked = board.call("getPicked");        // -1 until something is chosen
if (picked >= 0) { grant(myCards[picked]); board.call("close"); }
```

The board knows nothing about what is on the cards. It shows N slots, takes an answer, and goes
away; everything about what a card *means* stays in the caller.

## Say what taking it again would do

A card has four lines for a reason. The title is what it is, the body is what it does, the third
line groups it, and the **footer is the one that makes a stacking game legible**:

```js
board.call("setFooter", i, "owned x2");            // a draft
board.call("setFooter", i, "26 cores   owned x1"); // a shop
```

Without it a player taking their third damage mod has no idea it is their third.

`setEnabled(slot, 0)` dims a card they cannot afford **without hiding what it is** — seeing the
thing you cannot buy is most of what makes a currency interesting.

## Always give it a way out

The board will sit open for ever if nobody chooses. **That is the caller's problem**, and the
answer is a timeout that takes the first card:

```js
waveTimer -= dt;
if (waveTimer <= 0) { grant(myCards[0]); board.call("close"); }
```

A run that can stall on a menu is a run that will.

## Two ways to answer

**Number keys** `1`–`9`, in slot order. **Clicking**, via a transparent `UI.button` covering each
card — which is also how it works on a phone, where there are no number keys at all. Both are on
at once and neither needs configuring. Hovering lightens a card, because otherwise nothing says it
is clickable.

## Tuning

`cardW` (210), `cardH` (168) and `cardGap` (18) are the shape; `headingY`, `cardY` and `hintY` are
offsets from the middle of the viewport, so the row re-centres itself when the window changes size.
`titleScale`, `bodyScale` and `headingScale` are whole multiples of the 5×7 cell — fractional
scales are rounded, because a bitmap font at 1.5× is mush.

Every element is built once at start and then shown and hidden with `visible`. Creating them on
`open` would allocate at the exact moment the player is looking straight at it.

## What it does not do

- **Two body lines, not a paragraph.** `setCard` still takes `line1` and `line2` because a card
  reads better as two short lines than one wrapped one. A node can wrap (`wrapText: true`) if you
  would rather it did.
- **No rarity, no weighting, no pool.** It does not choose what the cards are; it shows what you
  hand it.
- **No pause.** Whether the world keeps moving while the board is open is the caller's business.
  In UltraDark the pilot is frozen and the arena is not.
- **No cost or currency.** A shop is this plus a price you check before granting — which is
  exactly how UltraDark's core shop is built, on this same board.
- It calls `UI.clear()` in `onDestroy`, which drops **this script's** elements only.
