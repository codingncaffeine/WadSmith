#!/usr/bin/env bash
# Session script for nested.sh: runs the capture tool's editor-play pose with the engine installed (so the app's own Play
# starts the real GZDoom, inside this nested session), stops that engine once its log shows the map started, and lets the
# app read the log back. Environment: WS_ROOT (the repository), WS_GAMES (a games folder).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "app-play-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"; mkdir -p "$HOME"
source "$WS_ROOT/scratch/env.sh" >/dev/null 2>&1
rm -rf "$WORK/capture"
dotnet run --no-build --project "$WS_ROOT/tools/WadSmith.Capture" -c Release -- "$WORK/capture" "--games=$WS_GAMES" editor-play > "$WORK/capture.txt" 2>&1 &
CAP=$!
LOG="$WORK/capture/profile/data/engine/last-play.log"
for _ in $(seq 1 240); do
  if [ -f "$LOG" ] && command grep -q "MAP01" "$LOG"; then
    sleep 2
    # Only the engine this app started: its settings file lies in this work folder.
    for pid in $(pgrep -x gzdoom); do
      if tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | command grep -qF "$WORK/capture/profile/data/engine/gzdoom.ini"; then kill -TERM "$pid"; fi
    done
    break
  fi
  if ! kill -0 "$CAP" 2>/dev/null; then break; fi
  sleep 0.5
done
wait "$CAP"
echo "app-play-session: done" > "$WORK/session.txt"
