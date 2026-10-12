#!/usr/bin/env bash
# Plays a project's map as the editor's Play does, in a private nested session, and checks it: the engine command comes
# from `wadsmith-cli play --print`; a stand-in home holds a GZDoom settings file that must come out unchanged (D19:
# WadSmith plays with its own copy); the engine's log must show the map started. Prints the log's lines about the map
# and its problems.
#
#   tools/engine/play-check.sh WORK_DIR PROJECT_FOLDER GAMES_FOLDER [MAP] ["X Y ANGLE" to play from there]
set -u
[ $# -ge 3 ] || { sed -n '2,8p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; PROJECT="$(cd "$2" && pwd)"; GAMES="$3"; MAP="${4:-MAP01}"; FROM="${5:-}"
rm -rf "$WORK/home" "$WORK/data"; mkdir -p "$WORK/home/.config/gzdoom" "$WORK/data"
printf '[GlobalSettings]\n# the user'"'"'s own settings: must never change\nfullscreen=true\n' > "$WORK/home/.config/gzdoom/gzdoom.ini"
BEFORE="$(sha256sum "$WORK/home/.config/gzdoom/gzdoom.ini" | cut -d' ' -f1)"
source "$ROOT/scratch/env.sh" >/dev/null 2>&1 || true
dotnet run --no-build --project "$ROOT/src/WadSmith.Cli" -c Release -- play "$PROJECT" --print "--data=$WORK/data" "--home=$WORK/home" "--games=$GAMES" "--map=$MAP" ${FROM:+"--from=$FROM"} > "$WORK/command.txt" || { echo "play-check: no command"; exit 1; }
LOG="$(command grep -A1 '^+logfile$' "$WORK/command.txt" | tail -1)"
export WS_LOG="$LOG" WS_MAP="$MAP"
"$HERE/nested.sh" "$WORK" 90 "$HERE/play-session.sh"
AFTER="$(sha256sum "$WORK/home/.config/gzdoom/gzdoom.ini" | cut -d' ' -f1)"
echo "play-check: command $(head -1 "$WORK/command.txt") … ($(wc -l < "$WORK/command.txt") lines)"
echo "play-check: the user's settings $( [ "$BEFORE" = "$AFTER" ] && echo unchanged || echo CHANGED )"
echo "play-check: WadSmith's settings $( [ -f "$WORK/data/engine/gzdoom.ini" ] && echo "at data/engine/gzdoom.ini" || echo MISSING )"
if [ -f "$LOG" ] && command grep -q "$MAP" "$LOG"; then echo "play-check: the map started ($(command grep -m1 "$MAP" "$LOG" | tr -d '\r' | cut -c1-80))"; else echo "play-check: THE MAP DID NOT START"; fi
command grep -iE "unknown|missing|error|not found|warning|WADSMITH FROM" "$LOG" 2>/dev/null | head -20 | sed 's/^/play-check: log: /'
[ "$BEFORE" = "$AFTER" ] && [ -f "$LOG" ] && command grep -q "$MAP" "$LOG"
