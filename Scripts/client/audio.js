// WebAudio synth — zero asset files. Kill sounds climb a pentatonic run
// inside a combo window (the Peggle rule, SDD §2.10).
//
// DarkShapes: TwinStickTron's client/js/audio.js at 46baa25, ported onto the engine's
// synth. Every voice is the original's, number for number and in the original's order,
// and the kill ladder is the original's, statement for statement. What moved is what it
// stood on:
//   - a note is Audio.tone and a burst is Audio.noise (wiki/08-audio.md, Synth sounds),
//     whose recipes are blip's and noise's own: the pitch gliding exponentially to
//     max(30, freq + slide), the gain falling exponentially from vol * 0.5 to 0.001, the
//     oscillator stopped 20 ms after its ramps, and noise fading linearly from vol * 0.4
//     over floor(rate * dur) samples. Both engines render the same samples, with a
//     browser's own oscillator; the noise is seeded by its recipe rather than drawn from
//     Math.random, so a burst is the same burst every time;
//   - what it plays on is handed in: initAudio({ audio, now }) takes the engine's Audio
//     global and a clock in milliseconds, read where performance.now() was, so a test can
//     hand in a recorder and a clock of its own. Before initAudio every voice is silent, as
//     every voice was before ensureAudio made a context;
//   - setTimeout(fn, ms) is later(fn, ms): the notes fn plays are asked for at once, each
//     with a delay of ms / 1000 seconds that the engine waits out, where the original
//     played them when a timer fired;
//   - the master gain is the sfx bus, and every voice plays through it. The original ran
//     voice -> master gain (desiredVol) -> speakers; the engine runs voice -> sfx bus ->
//     master bus -> speakers, with both buses at 1 until they are set. The sfx bus at
//     desiredVol and the master bus left at 1 multiply every sample by what the master
//     gain did, which html5/tests/darkShapesClientAudio.test.js renders both ways and
//     compares. The master bus (Audio.setVolume) would multiply to the same, but it is the
//     one control over everything the engine plays, and this volume is the game's own
//     setting. A tone's volume cannot stand in for either: a tone's gain ends on 0.001
//     whatever its volume, where the original's ended on 0.001 * desiredVol, and a burst
//     is seeded by its volume;
//   - ensureAudio has nothing left to do: the engine starts its own audio.

// DarkShapes: ctx is the engine's Audio global, not an AudioContext, and clock the
// milliseconds kill() reads; initAudio hands both in. There is no master gain node: its
// part is played by the sfx bus, BUS, which every voice is sent to.
let ctx = null, clock = null, desiredVol = 0.25;
const BUS = "sfx";

// DarkShapes: what ensureAudio did when it made the context -- keep what the voices play
// on, and start at the volume already asked for -- with the engine's Audio and a clock
// handed in rather than made. The client calls it once, as it loads.
export function initAudio({ audio, now }) {
  ctx = audio;
  clock = now;
  ctx.setBusVolume(BUS, desiredVol);
}

export function ensureAudio() {
  // DarkShapes: nothing is left to do here. The engine makes its audio, starts it on the
  // player's first tap, click or key, and drops a note asked for before then, as blip
  // returned before this made a context; the export stays because the client calls it on
  // every gesture. The original also resumed a context that had stopped running since
  // (an interrupted tab on iOS); the engine does that too, on the next gesture after it stops.
}

export function setVolume(v) {
  desiredVol = v;
  if (ctx) ctx.setBusVolume(BUS, v);
}

// DarkShapes: setTimeout(fn, ms) is later(fn, ms). fn runs now, and every note it plays
// is asked for with a delay of ms / 1000 seconds, which the engine waits out itself: on
// the audio clock in a browser, to the sample, and natively on its own clock, to the
// frame, where a script has no timers at all. One difference follows: a chord is asked
// for whole, so one asked for before the player's first gesture is dropped whole, where
// the original's later notes still sounded if the context was made before they were due.
let lateBy = 0;
function later(fn, ms) {
  const was = lateBy;
  lateBy = was + ms;
  try { fn(); } finally { lateBy = was; }
}

function blip(freq, dur, type = "square", vol = 1, slide = 0) {
  if (!ctx) return;
  // DarkShapes: the oscillator, its gain, both ramps and the stop 20 ms after them are the
  // engine's tone, from the same numbers. A slide of 0 holds the pitch, as if (slide) did.
  ctx.tone({ freq, duration: dur, wave: type, volume: vol, slide, delay: lateBy / 1000, bus: BUS });
}

function noise(dur, vol = 1) {
  if (!ctx) return;
  // DarkShapes: the buffer of white noise fading linearly from vol * 0.4 is the engine's
  // burst, from the same numbers.
  ctx.noise({ duration: dur, volume: vol, delay: lateBy / 1000, bus: BUS });
}

// pentatonic kill ladder
const LADDER = [0, 3, 5, 7, 10, 12, 15, 17, 19, 22];
let comboIdx = 0, lastKillAt = 0;

// DarkShapes: wave, boss, bank, pickup, warp, buy, over and win chain their notes with
// later(fn, ms) where the original called setTimeout(fn, ms); see later.
export const sfx = {
  shoot() { blip(880, 0.05, "square", 0.25, -300); },
  kill() {
    // DarkShapes: the clock is initAudio's, so before initAudio a kill leaves the ladder
    // where it is; the original's climbed unheard before it had a context.
    if (!clock) return;
    const now = clock(); // DarkShapes: was performance.now()
    if (now - lastKillAt > 1600) comboIdx = 0;
    lastKillAt = now;
    const semi = LADDER[Math.min(comboIdx++, LADDER.length - 1)];
    blip(330 * Math.pow(2, semi / 12), 0.14, "triangle", 0.9);
    noise(0.06, 0.3);
  },
  hurt() { blip(140, 0.25, "sawtooth", 1, -60); noise(0.15, 0.8); },
  dash() { blip(520, 0.09, "sine", 0.5, 500); },
  bomb() { noise(0.6, 1); blip(60, 0.5, "sine", 1, -20); },
  ability() { blip(660, 0.2, "sine", 0.6, 300); },
  down() { blip(220, 0.6, "sawtooth", 0.9, -160); },
  revive() { blip(440, 0.3, "sine", 0.8, 220); },
  wave() { blip(392, 0.12, "square", 0.5); later(() => blip(523, 0.16, "square", 0.5), 130); },
  boss() { blip(98, 0.7, "sawtooth", 1, 30); later(() => blip(98, 0.7, "sawtooth", 1, 30), 400); },
  bank() { blip(784, 0.08, "sine", 0.6); later(() => blip(1046, 0.12, "sine", 0.6), 90); },
  pick() { blip(523, 0.1, "triangle", 0.5); },
  pickup() { blip(880, 0.07, "sine", 0.5); later(() => blip(1320, 0.09, "sine", 0.5), 70); },
  use() { blip(660, 0.12, "triangle", 0.7, 400); noise(0.08, 0.3); },
  // class-weapon voices
  smg() { blip(980, 0.03, "square", 0.18, -200); },
  boom() { noise(0.22, 0.9); blip(120, 0.18, "sawtooth", 0.8, -60); },
  swing() { noise(0.12, 0.5); blip(240, 0.14, "sine", 0.5, 240); },
  lance() { blip(700, 0.08, "triangle", 0.4, -350); },
  rail() { blip(180, 0.22, "sawtooth", 0.9, 900); noise(0.1, 0.4); },
  arc() { blip(1200, 0.05, "square", 0.3, -500); },
  zap() { blip(1500, 0.06, "square", 0.5, -900); },
  freeze() { blip(1800, 0.3, "sine", 0.6, -1200); },
  warp() { blip(300, 0.2, "sine", 0.7, 800); later(() => blip(900, 0.15, "sine", 0.5, -400), 120); },
  buy() { blip(1046, 0.08, "sine", 0.6); later(() => blip(1568, 0.14, "sine", 0.6), 90); },
  over() { blip(196, 0.5, "sawtooth", 0.9, -80); later(() => blip(147, 0.8, "sawtooth", 0.9, -50), 350); },
  win() { [523, 659, 784, 1046].forEach((f, i) => later(() => blip(f, 0.25, "triangle", 0.8), i * 140)); },
};
