#!/usr/bin/env bash
# Takes a screenshot of what the player sees in GZDoom from a spot: the player stands at X Y facing ANGLE
# (degrees, the game's way), and the engine's own screenshot is copied to WORK/view.png. Runs in a private
# nested session (tools/engine/nested.sh).
#
#   tools/engine/view-shot.sh WORK_DIR IWAD MAP "X Y ANGLE" [FILE_OR_FOLDER…]
set -u
[ $# -ge 4 ] || { sed -n '2,7p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"; MAP="$3"; SPOT="$4"; shift 4
ENGINE="${GZDOOM:-$(command -v gzdoom || true)}"
[ -x "$ENGINE" ] || { echo "view-shot: gzdoom not found (set GZDOOM=path)"; exit 2; }
FILES=""; for f in "$@"; do FILES+="$(realpath "$f")"$'\n'; done
rm -f "$WORK/viewcheck.pk3"
(cd "$HERE/viewcheck" && zip -q -X -r "$WORK/viewcheck.pk3" .) || exit 2
mkdir -p "$WORK/shots"; rm -f "$WORK/shots/"*.png

# One console line, so the wait holds the screenshot back until the player has stood there a while.
echo "wait 350; screenshot" > "$WORK/shot.cfg"
# A leading space: the engine takes an argument starting with "-" (a spot west of the middle) for an option.
ARGS=(+set wsview " $SPOT" +set screenshot_dir "$WORK/shots" +set screenshot_type png +set crosshair 0 +set screenblocks 12 +exec "$WORK/shot.cfg")
export WS_ENGINE="$ENGINE" WS_IWAD="$IWAD" WS_MAP="$MAP" WS_FILES="$FILES" WS_PK3=viewcheck.pk3 WS_LOG=engine-view.log WS_END="WSVIEW DONE"
export WS_ARGS="$(printf '%s\n' "${ARGS[@]}")"
"$HERE/nested.sh" "$WORK" 120 "$HERE/door-session.sh"
SHOT="$(ls -t "$WORK/shots/"*.png 2>/dev/null | head -1)"
[ -n "$SHOT" ] || { echo "view-shot: no screenshot was taken; see $WORK/engine-stdout-doors.txt"; exit 1; }
cp "$SHOT" "$WORK/view.png"
echo "view-shot: $WORK/view.png"
