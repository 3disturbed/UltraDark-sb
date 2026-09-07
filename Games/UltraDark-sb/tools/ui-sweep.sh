#!/usr/bin/env bash
# Every UI state, at viewports it was not built at.
set -e
cd "$(dirname "$0")/.."
fail=0
for state in hangar wave boss draft dead; do
  for size in "1280 720" "3000 1600" "800 450" "2560 1080"; do
    set -- $size
    node tools/ui-shot.mjs "$state" "$1" "$2" || fail=1
  done
done
exit $fail
