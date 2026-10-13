#!/usr/bin/env bash
# Session script for nested.sh: runs a classic port's command (WORK/command.txt, one argument a line, the program first)
# with HOME inside the work folder, until it quits on its own (a -timedemo run ends with the demo) or WS_SECONDS (60) pass;
# its output goes to WORK/port-output.txt and its exit code to WORK/port-exit.txt.
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "port-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"
mkdir -p "$HOME"
mapfile -t CMD < "$WORK/command.txt"
"${CMD[@]}" > "$WORK/port-output.txt" 2>&1 &
PID=$!
for _ in $(seq 1 $(( ${WS_SECONDS:-60} * 2 ))); do
  kill -0 "$PID" 2>/dev/null || break
  sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then echo "still running after ${WS_SECONDS:-60} s" > "$WORK/port-exit.txt"; kill -TERM "$PID"; sleep 1; kill -KILL "$PID" 2>/dev/null; wait "$PID" 2>/dev/null
else wait "$PID"; echo "$?" > "$WORK/port-exit.txt"; fi
echo "port-session: done" > "$WORK/session.txt"
