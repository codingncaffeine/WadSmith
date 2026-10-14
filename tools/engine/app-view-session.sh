#!/usr/bin/env bash
# Session script for nested.sh: runs the capture tool's editor-engine-view pose with the engine installed, so the app's
# own "Game's view" starts the real GZDoom on a private Xvfb screen inside this nested session, and waits for the pose
# to finish (the app stops the engine itself). Environment: WS_ROOT (the repository), WS_GAMES (a games folder).
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "app-view-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
# The build environment first (it reads the real HOME), then a private HOME for everything started here.
source "$WS_ROOT/scratch/env.sh" >/dev/null 2>&1
export HOME="$WORK/home"; mkdir -p "$HOME"
rm -rf "$WORK/capture"
dotnet run --no-build --project "$WS_ROOT/tools/WadSmith.Capture" -c Release -- "$WORK/capture" "--games=$WS_GAMES" editor-engine-view > "$WORK/capture.txt" 2>&1
# Only an engine this app started: its settings file lies in this work folder.
for pid in $(pgrep -x 'gzdoom|uzdoom'); do
  if tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | command grep -qF "$WORK/capture/profile/data/engine/"; then kill -TERM "$pid"; fi
done
echo "app-view-session: done" > "$WORK/session.txt"
