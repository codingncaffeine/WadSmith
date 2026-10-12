#!/usr/bin/env bash
# Session script for tools/engine/nested.sh: runs the capture tool's real-window speed test (a
# 100,000-wall map panned back and forth), screenshots the screen while it pans, and records the
# frame times. Environment: WADSMITH_NESTED_WORK, WS_CAPTURE (WadSmith.Capture.dll), WS_DOTNET_ROOT,
# WS_BACKEND (wayland|x11), WS_GAMES (a games folder, optional).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "perf-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export DOTNET_ROOT="$WS_DOTNET_ROOT" PATH="$WS_DOTNET_ROOT:$PATH"
[ "${WS_BACKEND:-wayland}" = "x11" ] && export WADSMITH_WAYLAND=0
"$DOTNET_ROOT/dotnet" "$WS_CAPTURE" "$WORK/out" --real ${WS_GAMES:+"--games=$WS_GAMES"} --soak="${WS_SOAK:-0}" perf > "$WORK/perf.txt" 2>&1 &
PID=$!
for _ in $(seq 1 240); do
  command grep -q "perf: measuring" "$WORK/perf.txt" 2>/dev/null && break
  kill -0 "$PID" 2>/dev/null || break
  sleep 0.25
done
sleep 1.5
spectacle -b -n -f -o "$WORK/screen.png" > "$WORK/spectacle.txt" 2>&1
wait "$PID"
echo "capture exited with $?" > "$WORK/exit.txt"
echo "perf-session: done" > "$WORK/session.txt"
