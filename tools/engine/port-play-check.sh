#!/usr/bin/env bash
# Plays a project's map in a classic port the way the editor's "Play in" does, in a private nested session, and checks
# it: the command is `wadsmith-cli play --print --port=PORT` (the project exported for the port, WadSmith's own copy of
# the port's settings); a stand-in home holds the user's settings for the port, which must be copied and come out
# unchanged; a walking demo (-timedemo, in the export's format) proves the port loaded the map and ran every tic.
#
#   tools/engine/port-play-check.sh WORK_DIR PROJECT_FOLDER GAMES_FOLDER PORT [MAP] [TICS]
set -u
[ $# -ge 4 ] || { sed -n '2,8p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; PROJECT="$(cd "$2" && pwd)"; GAMES="$3"; PORT="$4"; MAP="${5:-MAP01}"; TICS="${6:-700}"
rm -rf "$WORK/home" "$WORK/data" "$WORK/userhome" "$WORK/port-output.txt" "$WORK/port-exit.txt"; mkdir -p "$WORK/home" "$WORK/data"
# The user's own settings for the port, where the port keeps them: copied once for WadSmith, never written.
case "$PORT" in
  dsda-doom) USERCFG="$WORK/userhome/.local/share/dsda-doom/dsda-doom.cfg" ;;
  chocolate-*|crispy-*) USERCFG="$WORK/userhome/.local/share/$PORT/$PORT.cfg" ;;
  *) USERCFG="$WORK/userhome/.local/share/$PORT/$PORT.cfg" ;;
esac
mkdir -p "$(dirname "$USERCFG")"; printf '# the user'"'"'s own settings: must never change\n' > "$USERCFG"
BEFORE="$(sha256sum "$USERCFG" | cut -d' ' -f1)"
source "$ROOT/scratch/env.sh" >/dev/null 2>&1 || true
XDG_DATA_HOME= dotnet run --no-build --project "$ROOT/src/WadSmith.Cli" -c Release -- play "$PROJECT" --print "--data=$WORK/data" "--home=$WORK/userhome" "--games=$GAMES" "--map=$MAP" "--port=$PORT" > "$WORK/command.txt" 2> "$WORK/lost.txt" || { echo "port-play-check: no command"; cat "$WORK/lost.txt"; exit 1; }
# The exported file is the last -file (the game's add-on and other WADs come first).
EXPORTED="$(awk 'p { last = $0; p = 0 } /^-file$/ { p = 1 } END { print last }' "$WORK/command.txt")"
case "$(basename "$EXPORTED")" in play-mbf21.wad) FORMAT=mbf21 ;; play-boom.wad) FORMAT=boom ;; *) FORMAT=vanilla ;; esac
node "$HERE/walk-demo.js" "$WORK/walk.lmp" "$FORMAT" "$MAP" 3 "$TICS" >/dev/null || exit 2
printf -- '%s\n' -timedemo "$WORK/walk.lmp" -nosound -nomusic -window >> "$WORK/command.txt"
case "$PORT" in chocolate-*|crispy-*) echo -nogui >> "$WORK/command.txt" ;; dsda-doom) echo -no_message_box >> "$WORK/command.txt" ;; esac
# Before the port runs (it saves its settings on the way out): WadSmith's copy must hold the user's.
MINE="$(command grep -F -- "/$(basename "$USERCFG")" "$WORK/command.txt" | head -1)"
COPIED="$( [ -f "$MINE" ] && cmp -s "$MINE" "$USERCFG" && echo "copied from the user's" || echo "NOT COPIED ($MINE)")"
"$HERE/nested.sh" "$WORK" 90 "$HERE/port-session.sh"
AFTER="$(sha256sum "$USERCFG" | cut -d' ' -f1)"
echo "port-play-check: $PORT, $(basename "$EXPORTED") $MAP, $FORMAT demo of $TICS tics"
echo "port-play-check: the user's settings $( [ "$BEFORE" = "$AFTER" ] && echo unchanged || echo CHANGED )"
echo "port-play-check: WadSmith's settings $COPIED"
sed 's/^/port-play-check: /' "$WORK/lost.txt"
command grep -iE "timed|error|not found" "$WORK/port-output.txt" 2>/dev/null | head -4 | sed 's/^/port-play-check: output: /'
if [ "$BEFORE" = "$AFTER" ] && [ "${COPIED#copied}" != "$COPIED" ] && command grep -qiE "timed $TICS gametics" "$WORK/port-output.txt" 2>/dev/null; then echo "port-play-check: PLAYED"; exit 0; fi
echo "port-play-check: DID NOT PLAY (see $WORK/port-output.txt)"; exit 1
