#!/usr/bin/env bash
# Session script for tools/engine/nested.sh: starts WadSmith with a portable profile, lets it open its
# window, screenshots the nested screen, then asks it to quit with SIGTERM and records how it ended.
# Environment: WADSMITH_NESTED_WORK, WS_APP (the wadsmith.dll), WS_DOTNET_ROOT, WS_BACKEND (wayland|x11).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "smoke-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export DOTNET_ROOT="$WS_DOTNET_ROOT" PATH="$WS_DOTNET_ROOT:$PATH"
[ "${WS_BACKEND:-wayland}" = "x11" ] && export WADSMITH_WAYLAND=0
"$DOTNET_ROOT/dotnet" "$WS_APP" --portable "$WORK/profile" > "$WORK/app-output.txt" 2>&1 &
PID=$!
for _ in $(seq 1 60); do
  command grep -q "Windowing:" "$WORK/profile/state/logs/wadsmith.log" 2>/dev/null && break
  kill -0 "$PID" 2>/dev/null || break
  sleep 0.25
done
sleep 4
spectacle -b -n -f -o "$WORK/screen.png" > "$WORK/spectacle.txt" 2>&1
START=$(date +%s%N)
kill -TERM "$PID" 2>/dev/null
for _ in $(seq 1 40); do kill -0 "$PID" 2>/dev/null || break; sleep 0.1; done
if kill -0 "$PID" 2>/dev/null; then
  echo "still running 4 s after SIGTERM" > "$WORK/exit.txt"
  kill -KILL "$PID"
else
  wait "$PID"; CODE=$?
  echo "exited with $CODE after $(( ($(date +%s%N) - START) / 1000000 )) ms" > "$WORK/exit.txt"
fi
echo "smoke-session: done" > "$WORK/session.txt"
