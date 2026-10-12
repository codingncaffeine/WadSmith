#!/usr/bin/env bash
# Session script for nested.sh: starts GZDoom with the door check add-on on one map, waits for the
# check to finish (or about 14 minutes: a map with many doors takes a while), then stops the engine. GZDoom gets its own HOME inside the work
# folder and runs with no sound or music.
#
# Environment (set by tools/engine/door-check.sh): WADSMITH_NESTED_WORK, WS_ENGINE, WS_IWAD, WS_MAP,
# WS_FILES (extra files or folders, one per line). Writes WORK/engine-doors.log. Other checks set WS_PK3
# (the add-on in WORK), WS_LOG (the log's name in WORK), WS_END (the log line that ends the check) and
# WS_ARGS (more engine arguments, one per line).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "door-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"; mkdir -p "$HOME"
LOG="$WORK/${WS_LOG:-engine-doors.log}"; rm -f "$LOG"
END="${WS_END:-WSDOOR END}"
ARGS=(-iwad "$WS_IWAD" -file "$WORK/${WS_PK3:-doorcheck.pk3}")
while IFS= read -r f; do [ -n "$f" ] && ARGS+=(-file "$f"); done <<< "${WS_FILES:-}"
while IFS= read -r a; do [ -n "$a" ] && ARGS+=("$a"); done <<< "${WS_ARGS:-}"
ARGS+=(+map "$WS_MAP" -nosound -nomusic -config "$HOME/gzdoom.ini" +logfile "$LOG" +queryiwad 0 +vid_fullscreen 0)
"$WS_ENGINE" "${ARGS[@]}" > "$WORK/engine-stdout-doors.txt" 2>&1 &
PID=$!
for _ in $(seq 1 1700); do
  if [ -f "$LOG" ] && command grep -q "$END" "$LOG"; then break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then kill -TERM "$PID"; sleep 1; kill -KILL "$PID" 2>/dev/null; fi
wait "$PID" 2>/dev/null
echo "door-session: done" > "$WORK/session.txt"
