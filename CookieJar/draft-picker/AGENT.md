# Draft Picker

Stop the round and ask a question. One of N cards, chosen by number key or by walking into it.

## The two problems, and the one answer

The shared scripting contract exposes no viewport and no font.

**No viewport** means a script cannot find the screen edge, so a card cannot be pinned to a corner
or centred on the display. **No font** means a card cannot be labelled with what it does.

Both are answered by putting the question in the world. The row is laid out in front of the actor
it follows, which is correct at every resolution, on a phone, and wherever the camera is; and a
card says what it is in **a colour and a row of pips**, which is a number you can read at a glance
across an arena. Colour for the family, pips for the member: "orange, three pips" is a name.

The words go in the log. That is not a workaround for the font — a player reads the names once
while learning and reads the colours thereafter.

## Wiring it up

```
Draft   (tag "Draft")   Scripts/DraftBoard.js
```

```js
var board = Scene.findFirstByTag("Draft").getComponent("ScriptComponent");

// open, dress, and let the player answer
board.call("open", 3);
board.call("setCard", 0, 226, 70,  70,  1);    // red family, one pip
board.call("setCard", 1, 110, 210, 120, 3);    // green family, three pips
board.call("setCard", 2, 255, 205, 80,  2);    // gold family, two pips

// then, each frame, until it answers
var picked = board.call("getPicked");          // -1 until something is chosen
if (picked >= 0) {
    grant(myCards[picked]);
    board.call("close");
}
```

The board knows nothing about what is on the cards. It shows N slots, it takes an answer, it goes
away. Everything about what a card *means* stays in the caller.

## Two ways to answer

**Number keys.** `1` to `6`, in slot order.

**Walking into a card.** After `walkInDelay` (0.45s, so you cannot pick one on the way in), being
within `walkInRadius` of a card chooses it. That is what makes the board work on a pad and on a
phone, where there are no number keys at all — and it is the more physical of the two, because
choosing becomes a move rather than a keystroke.

Both are on at once and neither needs configuring.

## Always give it a way out

The board will sit open for ever if nobody chooses. **That is the caller's problem to solve**, and
the answer is a timeout that takes the first card:

```js
waveTimer -= dt;
if (waveTimer <= 0) { grant(myCards[0]); board.call("close"); }
```

A run that can stall on a menu is a run that will.

## Hiding: `active`, never a transparent tint

Every actor the board will ever need is built once in `onStart` and then shown and hidden.
Creating them on `open` would churn the scene at the exact moment the player is looking straight
at it.

**How they are hidden matters, and it is the one thing in this cookie that will bite you.** Alpha 0
hides a sprite in the browser and does **not** reliably hide one in the native renderer. A board
hidden that way leaves a white card sitting in the world for the whole run — invisible in every
test, invisible in the web build, and the first thing you see in a native one. That is exactly how
it shipped the first time.

`setVisible` toggles `active`, which means the same thing to both engines. If you add a sprite to
this cookie, hide it the same way.

## Draw order

`depthBorder` (0.94), `depthCard` (0.95) and `depthPip` (0.97) put the row in front of everything,
including any darkness overlay. The border is *behind* the card on purpose, so a slightly larger
sprite reads as a rim; the chosen one goes white before `close()` so the pick is visibly registered
rather than the row just vanishing.

If your game has its own overlay, make sure these three stay above it. A draft card dimmed by the
night is a question the player cannot read.

## Tuning

`cardW` (118), `cardH` (150), `cardGap` (26) and `boardY` (−230) are the shape and where it sits.
`pipSize`, `pipGap`, `pipRow` (5 per row before wrapping) and `pipTop` are the pip block. `maxCards`
(6) is how many slots are built at start.

Every actor the board will ever need is built once in `onStart` and then shown and hidden.
Creating them on `open` would churn the scene at the exact moment the player is looking straight
at it.

## What it does not do

- **No text, no icons, no art.** A card is a coloured rectangle and some pips. Give a slot a
  texture yourself if you have one.
- **No rarity, no weighting, no pool.** It does not choose what the cards are; it shows what you
  hand it.
- **No pause.** `Time.timeScale` and whether the world keeps moving while the board is open are
  the caller's business. In UltraDark the pilot is frozen and the arena is not.
- **No cost, no currency.** A shop is this plus a price you check before granting — which is
  exactly how UltraDark's core shop is built, on the same board.
