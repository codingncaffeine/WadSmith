#!/usr/bin/env bash
# Asks the installed GZDoom how it reads maps: line actions, arguments, activation, flags and ids,
# and sector types, flags and damage, as it holds them after loading. Runs headless in a private
# nested session (tools/engine/nested.sh).
#
#   tools/engine/dump-map.sh WORK_DIR IWAD MAP[,MAP...] [EXTRA_FILE...]
#
# Writes WORK_DIR/engine-MAP.log per map. Needs gzdoom (or GZDOOM=path), kwin_wayland,
# dbus-run-session and zip.
set -u
[ $# -ge 3 ] || { sed -n '2,9p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"; MAPS="$(tr ',' '\n' <<< "$3")"; shift 3
ENGINE="${GZDOOM:-$(command -v gzdoom || true)}"
[ -x "$ENGINE" ] || { echo "dump-map: gzdoom not found (set GZDOOM=path)"; exit 2; }
FILES=""; for f in "$@"; do FILES+="$(realpath "$f")"$'\n'; done
rm -f "$WORK/dump.pk3"
(cd "$HERE/dump" && zip -q -X -r "$WORK/dump.pk3" .) || exit 2
COUNT=$(command grep -c . <<< "$MAPS")
export WS_ENGINE="$ENGINE" WS_IWAD="$IWAD" WS_MAPS="$MAPS" WS_FILES="$FILES"
"$HERE/nested.sh" "$WORK" $((60 + COUNT * 45)) "$HERE/dump-session.sh"
FAILED=0
while IFS= read -r MAP; do
  if command grep -q "WSDUMP END" "$WORK/engine-$MAP.log" 2>/dev/null; then
    echo "dump-map: $MAP: $(command grep -c '^WSL ' "$WORK/engine-$MAP.log") lines, $(command grep -c '^WSS ' "$WORK/engine-$MAP.log") sectors"
  else
    echo "dump-map: $MAP: the engine did not finish the dump; see $WORK/engine-stdout-$MAP.txt"
    FAILED=1
  fi
done <<< "$MAPS"
exit $FAILED
