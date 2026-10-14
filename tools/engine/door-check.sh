#!/usr/bin/env bash
# Asks the installed GZDoom whether a map's doors open and its lifts go down: every wall with a door
# action (its own walls, or a switch naming its tag), and every wall with a lift action, is used (or crossed) as the player would,
# and the door's ceiling or the lift's floor read 2 seconds later; a sliding or swinging door (a polyobject) by how far its
# walls' corners moved, and whether they came back. Runs headless in a private nested session (tools/engine/nested.sh).
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
  if [ "$area" = -1 ]; then echo "door-check: wall $wall: NAMES A TAG NO AREA HAS, so it opens nothing"; BAD=1; continue; fi
  if awk -v b="$before" -v a="$after" -v f="$floor" 'BEGIN { exit !(a > b + 16 && b <= f + 0.5) }'; then verdict=opened
  elif awk -v b="$before" -v f="$floor" 'BEGIN { exit !(b > f + 16) }'; then verdict="already open (a door that stays open)"
  else verdict="DID NOT OPEN"; BAD=1; fi
  echo "door-check: wall $wall, door area $area: floor $floor, ceiling $before -> $after, used $used: $verdict"
done < <(command grep '^WSDOOR [0-9]' "$LOG")
while read -r _ wall area before after used; do
  if awk -v b="$before" -v a="$after" 'BEGIN { exit !(a < b - 8) }'; then verdict="went down"; else verdict="DID NOT GO DOWN"; BAD=1; fi
  echo "door-check: wall $wall, lift area $area: floor $before -> $after, used $used: $verdict"
done < <(command grep '^WSLIFT [0-9]' "$LOG")
command grep -m1 '^WSDOOR BEGIN' "$LOG" | sed 's/^WSDOOR BEGIN/door-check: map/; s/$/ walls that open doors, lower lifts or teleport/'
while read -r _ wall x0 y0 x1 y1 used; do
  if awk -v a="$x0" -v b="$y0" -v c="$x1" -v d="$y1" 'BEGIN { exit !((c-a)*(c-a) + (d-b)*(d-b) > 256) }'; then verdict="sent the player to ($x1, $y1)"; else verdict="DID NOT SEND THE PLAYER ANYWHERE"; fi
  echo "door-check: wall $wall, teleporter: used $used: $verdict"
done < <(command grep '^WSTELE [0-9]' "$LOG")
# Sliding and swinging doors (polyobjects): how far their walls moved 2 seconds after the use, and whether they came back.
while read -r _ wall moved used wx wy; do
  if awk -v m="$moved" 'BEGIN { exit !(m > 16) }'; then verdict="opened (its polyobject moved $moved, on average by $wx, $wy)"; else verdict="DID NOT OPEN (moved $moved)"; BAD=1; fi
  shut=$(command grep "^WSPOLYSHUT $wall " "$LOG" | awk '{ print ($3 < 0.5) ? "shut again after " $4 " tics" : "DID NOT SHUT (still " $3 " off)" }')
  case "$shut" in *"DID NOT"*) BAD=1;; esac
  echo "door-check: wall $wall, sliding or swinging door: used $used: $verdict${shut:+, $shut}"
done < <(command grep '^WSPOLY [0-9]' "$LOG")
if ! command grep -qE '^WS(DOOR|LIFT|TELE|POLY) [0-9]' "$LOG"; then echo "door-check: no wall opens a door, lowers a lift or teleports, so nothing was checked"; BAD=1; fi
exit $BAD
