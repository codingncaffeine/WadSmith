#!/usr/bin/env bash
# Measures how fast the real WadSmith window draws a 100,000-wall map while panning, inside a private
# nested KWin (no window on the desktop, private bus, throwaway profile).
#
#   tools/app/perf.sh WORK_DIR [wayland|x11] [GAMES_FOLDER] [SOAK_SECONDS]
#
# With GAMES_FOLDER holding Doom II, the map is drawn with its real textures (the default view).
# SOAK_SECONDS keeps panning that long afterwards and logs resident memory every 30 s.
#
# Results: WORK_DIR/perf.txt (frame times), WORK_DIR/screen.png, WORK_DIR/exit.txt.
set -u
# The app plays shelf sounds through SDL: runs from here never reach a speaker.
export SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy PULSE_SERVER=unix:/nonexistent/wadsmith PIPEWIRE_REMOTE=/nonexistent/wadsmith
[ $# -ge 1 ] || { sed -n '2,10p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; BACKEND="${2:-wayland}"; GAMES="${3:-}"; SOAK="${4:-0}"
DOTNET_ROOT_DIR="${DOTNET_ROOT:-$(dirname "$(command -v dotnet)")}"
dotnet build "$ROOT/tools/WadSmith.Capture" -c Release -v q --nologo > "$WORK/build.txt" 2>&1 || { cat "$WORK/build.txt"; exit 2; }
rm -rf "$WORK/out" "$WORK/screen.png" "$WORK/exit.txt" "$WORK/perf.txt"
export WS_SOAK="$SOAK" WS_GAMES="$GAMES" WS_CAPTURE="$ROOT/tools/WadSmith.Capture/bin/Release/net10.0/WadSmith.Capture.dll" WS_DOTNET_ROOT="$DOTNET_ROOT_DIR" WS_BACKEND="$BACKEND"
"$ROOT/tools/engine/nested.sh" "$WORK" $((120 + SOAK)) "$HERE/perf-session.sh"
echo "perf: $(cat "$WORK/exit.txt" 2>/dev/null || echo 'no exit record')"
command grep -E "^(perf, |soak: )" "$WORK/perf.txt" | sed 's/^/perf: /'
command grep -E "Windowing:|ERR|Crash" "$WORK/out/profile/state/logs/"*.log 2>/dev/null | sed 's/^/perf: /'
[ -f "$WORK/screen.png" ] && echo "perf: screenshot $WORK/screen.png"
