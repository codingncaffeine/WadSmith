#!/usr/bin/env bash
# Session script for nested.sh: runs the engine command the editor's Play would run (WORK/command.txt, one argument a
# line, the program first) with HOME inside the work folder, no sound, in a window; waits until the log shows the map
# started (WS_MAP) or 40 seconds, then stops the engine.
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "play-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"
mapfile -t CMD < "$WORK/command.txt"
LOG="$WS_LOG"
rm -f "$LOG"
"${CMD[@]}" -nosound -nomusic +vid_fullscreen 0 +queryiwad 0 > "$WORK/engine-stdout-play.txt" 2>&1 &
PID=$!
for _ in $(seq 1 80); do
  if [ -f "$LOG" ] && command grep -q "$WS_MAP" "$LOG"; then sleep 3; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then kill -TERM "$PID"; sleep 1; kill -KILL "$PID" 2>/dev/null; fi
wait "$PID" 2>/dev/null
echo "play-session: done" > "$WORK/session.txt"
