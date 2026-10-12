#!/usr/bin/env bash
# Asks the installed GZDoom whether a map's secrets count: the player is put in every area the engine holds
# as a secret, standing on its floor, and the map's count of secrets found must go up by one each time. Runs
# headless in a private nested session (tools/engine/nested.sh).
#
#   tools/engine/secret-check.sh WORK_DIR IWAD MAP [FILE_OR_FOLDER…]
#
# Prints one line per secret area. Exit 0 when the map has secrets and every one counted.
set -u
[ $# -ge 3 ] || { sed -n '2,8p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"; MAP="$3"; shift 3
ENGINE="${GZDOOM:-$(command -v gzdoom || true)}"
[ -x "$ENGINE" ] || { echo "secret-check: gzdoom not found (set GZDOOM=path)"; exit 2; }
FILES=""; for f in "$@"; do FILES+="$(realpath "$f")"$'\n'; done
rm -f "$WORK/secretcheck.pk3"
(cd "$HERE/secretcheck" && zip -q -X -r "$WORK/secretcheck.pk3" .) || exit 2
export WS_ENGINE="$ENGINE" WS_IWAD="$IWAD" WS_MAP="$MAP" WS_FILES="$FILES" WS_PK3=secretcheck.pk3 WS_LOG=engine-secrets.log WS_END="WSSECRET END"
"$HERE/nested.sh" "$WORK" 300 "$HERE/door-session.sh"
LOG="$WORK/engine-secrets.log"
command grep -q "WSSECRET END" "$LOG" 2>/dev/null || { echo "secret-check: the engine did not finish; see $WORK/engine-stdout-doors.txt"; exit 2; }
BAD=0
while read -r _ area counted; do
  if [ "$counted" = 1 ]; then verdict="counted when the player stepped in"; else verdict="DID NOT COUNT ($counted)"; BAD=1; fi
  echo "secret-check: area $area: $verdict"
done < <(command grep '^WSSECRET [0-9]' "$LOG")
command grep -m1 '^WSSECRET BEGIN' "$LOG" | sed 's/^WSSECRET BEGIN \([^ ]*\) \([0-9]*\) \([0-9]*\)/secret-check: map \1: \2 secret areas, \3 secrets to find/'
command grep -m1 '^WSSECRET END' "$LOG" | sed 's/^WSSECRET END \([0-9]*\) \([0-9]*\)/secret-check: found \1 of \2/'
command grep -qE '^WSSECRET [0-9]' "$LOG" || { echo "secret-check: the map has no secret areas, so nothing was checked"; BAD=1; }
exit $BAD
