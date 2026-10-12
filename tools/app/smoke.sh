#!/usr/bin/env bash
# Starts the real WadSmith window in a private nested KWin (no window on the desktop, private bus,
# portable profile), screenshots it, and checks it quits promptly on SIGTERM.
#
#   tools/app/smoke.sh WORK_DIR [wayland|x11] [GAMES_FOLDER] [PROJECT_FOLDER]
#
# PROJECT_FOLDER is passed on the command line, so the window opens that project.
#
# Results: WORK_DIR/screen.png, WORK_DIR/exit.txt, WORK_DIR/profile/state/logs/wadsmith.log.
set -u
# The app plays shelf sounds through SDL: runs from here never reach a speaker.
export SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy PULSE_SERVER=unix:/nonexistent/wadsmith PIPEWIRE_REMOTE=/nonexistent/wadsmith
[ $# -ge 1 ] || { sed -n '2,10p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; BACKEND="${2:-wayland}"; GAMES="${3:-}"; OPEN="${4:-}"
DOTNET_ROOT_DIR="${DOTNET_ROOT:-$(dirname "$(command -v dotnet)")}"
dotnet build "$ROOT/src/WadSmith" -c Release -v q --nologo > "$WORK/build.txt" 2>&1 || { cat "$WORK/build.txt"; exit 2; }
rm -rf "$WORK/profile" "$WORK/screen.png" "$WORK/exit.txt"
mkdir -p "$WORK/profile/config"
if [ -n "$GAMES" ]; then
  printf '{ "gameFolders": [ "%s" ] }\n' "$GAMES" > "$WORK/profile/config/settings.json"
fi
export WS_OPEN="$OPEN" WS_APP="$ROOT/src/WadSmith/bin/Release/net10.0/wadsmith.dll" WS_DOTNET_ROOT="$DOTNET_ROOT_DIR" WS_BACKEND="$BACKEND"
"$ROOT/tools/engine/nested.sh" "$WORK" 60 "$HERE/smoke-session.sh"
echo "smoke: $(cat "$WORK/exit.txt" 2>/dev/null || echo 'no exit record')"
command grep -E "Windowing:|ERR|Crash" "$WORK/profile/state/logs/wadsmith.log" 2>/dev/null | sed 's/^/smoke: /'
[ -f "$WORK/screen.png" ] && echo "smoke: screenshot $WORK/screen.png"
