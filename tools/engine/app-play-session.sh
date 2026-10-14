#!/usr/bin/env bash
# Session script for nested.sh: runs the capture tool's editor-play pose with the engine installed (so the app's own Play
# starts the real engine, UZDoom or GZDoom, inside this nested session), stops that engine once its log shows the map
# started, and lets the app read the log back. Environment: WS_ROOT (the repository), WS_GAMES (a games folder).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "app-play-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
# The build environment first (it reads the real HOME), then a private HOME for everything started here.
source "$WS_ROOT/scratch/env.sh" >/dev/null 2>&1
export HOME="$WORK/home"; mkdir -p "$HOME"
rm -rf "$WORK/capture"
dotnet run --no-build --project "$WS_ROOT/tools/WadSmith.Capture" -c Release -- "$WORK/capture" "--games=$WS_GAMES" editor-play > "$WORK/capture.txt" 2>&1 &
CAP=$!
# The engine's log in WadSmith's engine folder: last-play from GZDoom, log-last-play.txt from UZDoom 5. A map has started
# when its title line is there ("E1M1 - Outer Prison", "MAP01 - Entryway").
DIR="$WORK/capture/profile/data/engine"
written() { for f in "$DIR/last-play" "$DIR/log-last-play.txt"; do [ -f "$f" ] && { echo "$f"; return; }; done; }
for _ in $(seq 1 240); do
  LOG="$(written)"
  if [ -n "$LOG" ] && command grep -qE '^(MAP[0-9][0-9]|E[0-9]M[0-9]) - ' "$LOG"; then
    sleep 2
    # Only the engine this app started: its settings file lies in this work folder.
    for pid in $(pgrep -x 'gzdoom|uzdoom'); do
      if tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | command grep -qF "$WORK/capture/profile/data/engine/"; then kill -TERM "$pid"; fi
    done
    break
  fi
  if ! kill -0 "$CAP" 2>/dev/null; then break; fi
  sleep 0.5
done
wait "$CAP"
echo "app-play-session: done" > "$WORK/session.txt"
