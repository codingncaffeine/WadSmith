#!/usr/bin/env bash
# Session script for nested.sh: starts the engine (GZDoom or UZDoom) with the door check add-on on one map, waits for the
# check to finish (or about 14 minutes: a map with many doors takes a while), then stops the engine. The engine gets its
# own HOME inside the work folder, works in the work folder, and runs with no sound or music.
#
# Environment (set by tools/engine/door-check.sh): WADSMITH_NESTED_WORK, WS_ENGINE, WS_IWAD, WS_MAP,
# WS_FILES (extra files or folders, one per line). Writes WORK/engine-doors.log. Other checks set WS_PK3
# (the add-on in WORK), WS_LOG (the log's name in WORK), WS_END (the log line that ends the check) and
# WS_ARGS (more engine arguments, one per line).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "door-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"; mkdir -p "$HOME"
LOG="$WORK/${WS_LOG:-engine-doors.log}"
# The engine is given a plain name for its log: GZDoom writes the name as given, UZDoom 5 log-NAME.txt (a dot made a
# dash), both in the folder it works in. Whichever it wrote is renamed to LOG at the end.
NAME="$(basename "$LOG")"; NAME="${NAME%.*}"; NAME="${NAME//./-}"
WRITTEN=("$WORK/$NAME" "$WORK/log-$NAME.txt")
rm -f "$LOG" "${WRITTEN[@]}"
written() { for f in "${WRITTEN[@]}"; do [ -f "$f" ] && { echo "$f"; return; }; done; }
END="${WS_END:-WSDOOR END}"
ARGS=(-iwad "$WS_IWAD" -file "$WORK/${WS_PK3:-doorcheck.pk3}")
while IFS= read -r f; do [ -n "$f" ] && ARGS+=(-file "$f"); done <<< "${WS_FILES:-}"
while IFS= read -r a; do [ -n "$a" ] && ARGS+=("$a"); done <<< "${WS_ARGS:-}"
ARGS+=(+map "$WS_MAP" -nosound -nomusic -config "$HOME/gzdoom.ini" +logfile "$NAME" +queryiwad 0 +vid_fullscreen 0)
cd "$WORK"
"$WS_ENGINE" "${ARGS[@]}" > "$WORK/engine-stdout-doors.txt" 2>&1 &
PID=$!
for _ in $(seq 1 1700); do
  L="$(written)"
  if [ -n "$L" ] && command grep -q "$END" "$L"; then break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then kill -TERM "$PID"; sleep 1; kill -KILL "$PID" 2>/dev/null; fi
wait "$PID" 2>/dev/null
L="$(written)"; [ -n "$L" ] && mv -f "$L" "$LOG"
echo "door-session: done" > "$WORK/session.txt"
