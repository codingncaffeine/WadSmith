#!/usr/bin/env bash
# Asks the installed GZDoom whether a map's doors open and its lifts go down: every wall with a door
# action naming no tag, and every wall with a lift action, is used (or crossed) as the player would,
# and the door's ceiling or the lift's floor read 2 seconds later. Runs headless in a private nested
# session (tools/engine/nested.sh).
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
"$HERE/nested.sh" "$WORK" 900 "$HERE/door-session.sh"
LOG="$WORK/engine-doors.log"
command grep -q "WSDOOR END" "$LOG" 2>/dev/null || { echo "door-check: the engine did not finish; see $WORK/engine-stdout-doors.txt"; exit 2; }
BAD=0
while read -r _ wall area floor before after used; do
  if awk -v b="$before" -v a="$after" -v f="$floor" 'BEGIN { exit !(a > b + 16 && b <= f + 0.5) }'; then verdict=opened
  elif awk -v b="$before" -v f="$floor" 'BEGIN { exit !(b > f + 16) }'; then verdict="already open (a door that stays open)"
  else verdict="DID NOT OPEN"; BAD=1; fi
  echo "door-check: wall $wall, door area $area: floor $floor, ceiling $before -> $after, used $used: $verdict"
done < <(command grep '^WSDOOR [0-9]' "$LOG")
while read -r _ wall area before after used; do
  if awk -v b="$before" -v a="$after" 'BEGIN { exit !(a < b - 8) }'; then verdict="went down"; else verdict="DID NOT GO DOWN"; BAD=1; fi
  echo "door-check: wall $wall, lift area $area: floor $before -> $after, used $used: $verdict"
done < <(command grep '^WSLIFT [0-9]' "$LOG")
command grep -m1 '^WSDOOR BEGIN' "$LOG" | sed 's/^WSDOOR BEGIN/door-check: map/; s/$/ walls that open doors or lower lifts/'
if ! command grep -qE '^WS(DOOR|LIFT) [0-9]' "$LOG"; then echo "door-check: no wall opens a door or lowers a lift, so nothing was checked"; BAD=1; fi
exit $BAD
