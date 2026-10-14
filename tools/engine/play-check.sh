#!/usr/bin/env bash
# Plays a project's map as the editor's Play does, in a private nested session, and checks it: the engine command comes
# from `wadsmith-cli play --print` (the engine found first, UZDoom before GZDoom, or WS_ENGINE=PATH); a stand-in home
# holds GZDoom and UZDoom settings files that must come out unchanged (D19: WadSmith plays with its own copy); the
# engine's log must show the map started. Prints the log's lines about the map and its problems. WS_PROBE=1 adds
# tools/engine/probe, which prints the engine's view of the map (a "PROBE" line).
#
#   tools/engine/play-check.sh WORK_DIR PROJECT_FOLDER GAMES_FOLDER [MAP] ["X Y ANGLE" to play from there]
set -u
[ $# -ge 3 ] || { sed -n '2,9p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; PROJECT="$(cd "$2" && pwd)"; GAMES="$3"; MAP="${4:-MAP01}"; FROM="${5:-}"
rm -rf "$WORK/home" "$WORK/data"; mkdir -p "$WORK/home/.config/gzdoom" "$WORK/home/.config/uzdoom" "$WORK/data"
for INI in gzdoom/gzdoom.ini uzdoom/uzdoom.ini; do
  printf '[GlobalSettings]\n# the user'"'"'s own settings: must never change\nfullscreen=true\n' > "$WORK/home/.config/$INI"
done
SUMS() { cat "$WORK/home/.config/gzdoom/gzdoom.ini" "$WORK/home/.config/uzdoom/uzdoom.ini" | sha256sum | cut -d' ' -f1; }
BEFORE="$(SUMS)"
source "$ROOT/scratch/env.sh" >/dev/null 2>&1 || true
dotnet run --no-build --project "$ROOT/src/WadSmith.Cli" -c Release -- play "$PROJECT" --print "--data=$WORK/data" "--home=$WORK/home" "--games=$GAMES" "--map=$MAP" ${FROM:+"--from=$FROM"} ${WS_ENGINE:+"--engine=$WS_ENGINE"} > "$WORK/command.txt" || { echo "play-check: no command"; exit 1; }
# The engine is given a plain name for its log and works in WadSmith's engine folder, as the editor starts it.
LOGNAME="$(command grep -A1 '^+logfile$' "$WORK/command.txt" | tail -1)"
LOGDIR="$WORK/data/engine"
# WS_PROBE=1: also load tools/engine/probe, which prints the engine's view of the map (title, next map, sky, music, compat flags).
[ -n "${WS_PROBE:-}" ] && printf -- '-file\n%s\n' "$HERE/probe" >> "$WORK/command.txt"
export WS_LOG_DIR="$LOGDIR" WS_LOG_NAME="$LOGNAME" WS_MAP="$MAP"
"$HERE/nested.sh" "$WORK" 90 "$HERE/play-session.sh"
LOG="$LOGDIR/$LOGNAME"; [ -f "$LOGDIR/log-$LOGNAME.txt" ] && LOG="$LOGDIR/log-$LOGNAME.txt"
AFTER="$(SUMS)"
echo "play-check: command $(head -1 "$WORK/command.txt") … ($(wc -l < "$WORK/command.txt") lines)"
echo "play-check: the user's settings $( [ "$BEFORE" = "$AFTER" ] && echo unchanged || echo CHANGED )"
SETTINGS="$(command grep -A1 '^-config$' "$WORK/command.txt" | tail -1)"
echo "play-check: WadSmith's settings $( [ -f "$SETTINGS" ] && echo "at ${SETTINGS#"$WORK/"}" || echo "MISSING ($SETTINGS)" )"
if [ -f "$LOG" ] && command grep -q "$MAP" "$LOG"; then echo "play-check: the map started ($(command grep -m1 "$MAP" "$LOG" | tr -d '\r' | cut -c1-80))"; else echo "play-check: THE MAP DID NOT START"; fi
command grep -iE "unknown|missing|error|not found|warning|WADSMITH FROM|PROBE" "$LOG" 2>/dev/null | head -20 | sed 's/^/play-check: log: /'
[ "$BEFORE" = "$AFTER" ] && [ -f "$LOG" ] && command grep -q "$MAP" "$LOG"
