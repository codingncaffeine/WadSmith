#!/usr/bin/env bash
# Session script for nested.sh: starts GZDoom with the door check add-on on one map, waits for the
# check to finish (or 90 seconds), then stops the engine. GZDoom gets its own HOME inside the work
# folder and runs with no sound or music.
#
# Environment (set by tools/engine/door-check.sh): WADSMITH_NESTED_WORK, WS_ENGINE, WS_IWAD, WS_MAP,
# WS_FILES (extra files or folders, one per line). Writes WORK/engine-doors.log.
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "door-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"; mkdir -p "$HOME"
LOG="$WORK/engine-doors.log"; rm -f "$LOG"
ARGS=(-iwad "$WS_IWAD" -file "$WORK/doorcheck.pk3")
while IFS= read -r f; do [ -n "$f" ] && ARGS+=(-file "$f"); done <<< "${WS_FILES:-}"
ARGS+=(+map "$WS_MAP" -nosound -nomusic -config "$HOME/gzdoom.ini" +logfile "$LOG" +queryiwad 0 +vid_fullscreen 0)
"$WS_ENGINE" "${ARGS[@]}" > "$WORK/engine-stdout-doors.txt" 2>&1 &
PID=$!
for _ in $(seq 1 180); do
  if [ -f "$LOG" ] && command grep -q "WSDOOR END" "$LOG"; then break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then kill -TERM "$PID"; sleep 1; kill -KILL "$PID" 2>/dev/null; fi
wait "$PID" 2>/dev/null
echo "door-session: done" > "$WORK/session.txt"
