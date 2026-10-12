#!/usr/bin/env bash
# Asks the installed GZDoom whether areas are windows: sight passes through each from one of the two
# rooms it opens onto to the other, and the player cannot walk into it from wherever the player stands in
# front of it (a step back into the room is the control; a side where the player stands nowhere, a cage
# or a ledge, is said and checked from the other). Runs headless in a private nested session
# (tools/engine/nested.sh).
#
#   tools/engine/window-check.sh WORK_DIR IWAD MAP "AREA AREA…" [FILE_OR_FOLDER…]
#
# Prints one line per area and its verdict. Exit 0 when every area listed is a window.
set -u
[ $# -ge 4 ] || { sed -n '2,8p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"; MAP="$3"; AREAS="$4"; shift 4
ENGINE="${GZDOOM:-$(command -v gzdoom || true)}"
[ -x "$ENGINE" ] || { echo "window-check: gzdoom not found (set GZDOOM=path)"; exit 2; }
FILES=""; for f in "$@"; do FILES+="$(realpath "$f")"$'\n'; done
rm -f "$WORK/windowcheck.pk3"
(cd "$HERE/windowcheck" && zip -q -X -r "$WORK/windowcheck.pk3" .) || exit 2
export WS_ENGINE="$ENGINE" WS_IWAD="$IWAD" WS_MAP="$MAP" WS_FILES="$FILES" WS_PK3=windowcheck.pk3 WS_LOG=engine-windows.log WS_END="WSWIN END"
export WS_ARGS="+set"$'\n'"wswindows"$'\n'"$AREAS"
"$HERE/nested.sh" "$WORK" 300 "$HERE/door-session.sh"
LOG="$WORK/engine-windows.log"
command grep -q "WSWIN END" "$LOG" 2>/dev/null || { echo "window-check: the engine did not finish; see $WORK/engine-stdout-doors.txt"; exit 2; }
BAD=0
while read -r _ area rest; do
  set -- $rest
  if [ "$1" = none ] || [ "$1" = rooms ]; then echo "window-check: area $area: NOT A WINDOW ($*)"; BAD=1; continue; fi
  sight=$1; in0=$2; in1=$3; c0=$4; c1=$5; sill=$6; top=$7
  why=""
  [ "$sight" = 1 ] || why+=" sight does not pass;"
  [ "$in0" = 0 ] || why+=" the player walks in from the first room;"
  [ "$in1" = 0 ] || why+=" the player walks in from the second room;"
  [ "$c0" = 1 ] || [ "$c1" = 1 ] || why+=" the player stands in front of it on neither side, so walking in was not checked;"
  if [ -n "$why" ]; then verdict="NOT A WINDOW:$why"; BAD=1
  elif [ "$c0" = 1 ] && [ "$c1" = 1 ]; then verdict="a window: seen through, not walked into"
  else verdict="a window: seen through, not walked into (the player stands in front of it on one side only)"; fi
  echo "window-check: area $area (sill $sill, top $top): $verdict"
done < <(command grep '^WSWIN [0-9-]' "$LOG")
command grep -m1 '^WSWIN BEGIN' "$LOG" | sed 's/^WSWIN BEGIN/window-check: map/; s/$/ areas listed/'
command grep -qE '^WSWIN [0-9-]' "$LOG" || { echo "window-check: no area was checked"; BAD=1; }
exit $BAD
