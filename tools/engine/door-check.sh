#!/usr/bin/env bash
# Asks the installed GZDoom whether a map's doors open: every wall with a door action naming no tag is
# used as the player would use it, and the door's ceiling read 2 seconds later. Runs headless in a
# private nested session (tools/engine/nested.sh).
#
#   tools/engine/door-check.sh WORK_DIR IWAD MAP FILE_OR_FOLDER...
#
# Prints one line per wall: wall, door area, floor, ceiling before, ceiling after, whether the engine
# took the use; and "opened" or "DID NOT OPEN". Exit 0 when every door opened from every wall.
set -u
[ $# -ge 4 ] || { sed -n '2,9p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"; MAP="$3"; shift 3
ENGINE="${GZDOOM:-$(command -v gzdoom || true)}"
[ -x "$ENGINE" ] || { echo "door-check: gzdoom not found (set GZDOOM=path)"; exit 2; }
FILES=""; for f in "$@"; do FILES+="$(realpath "$f")"$'\n'; done
rm -f "$WORK/doorcheck.pk3"
(cd "$HERE/doorcheck" && zip -q -X -r "$WORK/doorcheck.pk3" .) || exit 2
export WS_ENGINE="$ENGINE" WS_IWAD="$IWAD" WS_MAP="$MAP" WS_FILES="$FILES"
"$HERE/nested.sh" "$WORK" 120 "$HERE/door-session.sh"
LOG="$WORK/engine-doors.log"
command grep -q "WSDOOR END" "$LOG" 2>/dev/null || { echo "door-check: the engine did not finish; see $WORK/engine-stdout-doors.txt"; exit 2; }
BAD=0
while read -r _ wall area floor before after used; do
  if awk -v b="$before" -v a="$after" -v f="$floor" 'BEGIN { exit !(a > b + 16 && b <= f + 0.5) }'; then verdict=opened; else verdict="DID NOT OPEN"; BAD=1; fi
  echo "door-check: wall $wall, door area $area: floor $floor, ceiling $before -> $after, used $used: $verdict"
done < <(command grep '^WSDOOR [0-9]' "$LOG")
command grep -m1 '^WSDOOR BEGIN' "$LOG" | sed 's/^WSDOOR BEGIN/door-check: map/; s/$/ walls that open doors/'
exit $BAD
