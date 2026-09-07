#!/usr/bin/env bash
# frame.sh -- look at one frame of the NATIVE build.
#
# Every gate in this repository reads text. None of them can see a picture, and
# two of the worst bugs this game had were only ever visible in one: a draft card
# parked on the pilot for the whole run, because alpha 0 hides a sprite in the
# browser and not natively.
#
#   tools/frame.sh <wave> <seconds> <out.png>
#
# It copies the built game, patches the COPY so it launches itself and jumps to a
# wave (there is no keyboard on a headless box), runs it under Xvfb and reads the
# framebuffer. The build itself is never modified.
#
# Reading the result: one colour at 99% means nothing is drawing, or one thing is
# drawing over everything. (100, 149, 237) is MonoGame's clear colour -- an empty
# window.
set -euo pipefail

WAVE="${1:-3}"
SECONDS_TO_RUN="${2:-14}"
OUT="${3:-/tmp/fb/shot.png}"

GAME_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$GAME_DIR/dist/Linux_x64"
[ -d "$SRC" ] || { echo "no linux build at $SRC -- run sbengine first" >&2; exit 1; }

WORK=$(mktemp -d /var/tmp/ultradark-frame.XXXXXX)
trap 'rm -rf "$WORK"' EXIT
cp -r "$SRC/." "$WORK/"

# The patch: launch out of the hangar and jump to a wave, once, a second in.
cat >> "$WORK/Scripts/Director.js" <<PATCH

// ---- appended by tools/frame.sh; not part of the game ----------------------
var __frameArmed = 0;
var __frameClock = 0;
function onLateUpdate(dt) {
    if (__frameArmed) { return; }
    __frameClock += dt;
    if (__frameClock < 1.0) { return; }
    __frameArmed = 1;
    forceLaunch();
    if ($WAVE > 1) { forceWave($WAVE); }
}
PATCH

pkill -f "Xvfb :98" 2>/dev/null || true
rm -rf /tmp/fb98; mkdir -p /tmp/fb98
Xvfb :98 -screen 0 1280x720x24 -fbdir /tmp/fb98 >/dev/null 2>&1 &
XPID=$!
until [ -e /tmp/fb98/Xvfb_screen0 ]; do sleep 1; done

( cd "$WORK" && DISPLAY=:98 ./UltraDark > "$WORK/game.log" 2>&1 ) &
GPID=$!

for _ in $(seq 1 "$SECONDS_TO_RUN"); do sleep 1; done

cp /tmp/fb98/Xvfb_screen0 "$WORK/shot.raw"
kill $GPID 2>/dev/null || true
kill $XPID 2>/dev/null || true

python3 - "$WORK/shot.raw" "$OUT" <<'PY'
import sys
from PIL import Image
from collections import Counter
raw = open(sys.argv[1], 'rb').read()
W, H = 1280, 720
img = Image.frombytes('RGBA', (W, H), raw[:W * H * 4], 'raw', 'BGRA').convert('RGB')
img.save(sys.argv[2])
c = Counter(img.getdata())
total = W * H
print(f"  {len(c)} distinct colours")
for col, n in c.most_common(5):
    print(f"    {col}  {100 * n / total:.1f}%")
top = c.most_common(1)[0]
if top[0] == (100, 149, 237):
    print("  FAIL: MonoGame's clear colour -- the window is empty")
    sys.exit(1)
if 100 * top[1] / total > 99:
    print(f"  FAIL: {top[0]} is {100 * top[1] / total:.1f}% of the screen -- nothing is drawing, or one thing is drawing over everything")
    sys.exit(1)
PY

echo "  wrote $OUT"
grep -iE "error|exception" "$WORK/game.log" | head -5 || true
