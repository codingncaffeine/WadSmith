#!/usr/bin/env bash
# Plays an exported map in a classic port, in a private nested session, and says whether the port loaded it and ran it:
# a demo (tools/engine/walk-demo.js) walks the player round the map for TICS tics with -timedemo, so the port loads the
# map, draws every frame and quits by itself, printing how many tics it timed. Passes only when the port timed exactly
# the demo's tics. The port's settings and saves stay in WORK_DIR/home.
#
#   tools/engine/port-check.sh WORK_DIR PORT IWAD WAD SLOT [FORMAT vanilla|boom|mbf21] [SKILL] [TICS]
#   PORT: chocolate-doom, crispy-doom, dsda-doom, woof or nugget-doom (on the PATH). FORMAT defaults to vanilla.
set -u
[ $# -ge 5 ] || { sed -n '2,9p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; PORT="$2"; IWAD="$3"; WAD="$(cd "$(dirname "$4")" && pwd)/$(basename "$4")"; SLOT="$5"
FORMAT="${6:-vanilla}"; SKILL="${7:-3}"; TICS="${8:-700}"
PROGRAM="$(command -v "$PORT")" || { echo "port-check: $PORT is not installed"; exit 2; }
rm -rf "$WORK/home" "$WORK/port-output.txt" "$WORK/port-exit.txt"; mkdir -p "$WORK/home"
node "$HERE/walk-demo.js" "$WORK/walk.lmp" "$FORMAT" "$SLOT" "$SKILL" "$TICS" || exit 2
{
  echo "$PROGRAM"
  printf -- '%s\n' -iwad "$IWAD" -file "$WAD" -timedemo "$WORK/walk.lmp" -nosound -nomusic -window
  case "$PORT" in
    chocolate-doom|crispy-doom) printf -- '%s\n' -nogui -config "$WORK/home/port.cfg" -extraconfig "$WORK/home/port-extra.cfg" ;;
    dsda-doom) printf -- '%s\n' -no_message_box -config "$WORK/home/port.cfg" ;;
    *) printf -- '%s\n' -config "$WORK/home/port.cfg" ;;
  esac
} > "$WORK/command.txt"
"$HERE/nested.sh" "$WORK" 90 "$HERE/port-session.sh"
echo "port-check: $PORT, $(basename "$WAD") $SLOT, $FORMAT demo of $TICS tics"
echo "port-check: exit $(cat "$WORK/port-exit.txt" 2>/dev/null || echo "(none: the session did not run)")"
command grep -iE "timed|error|unknown|not found|R_|P_|W_" "$WORK/port-output.txt" 2>/dev/null | head -12 | sed 's/^/port-check: output: /'
if command grep -qiE "timed $TICS gametics" "$WORK/port-output.txt" 2>/dev/null; then echo "port-check: PLAYED"; exit 0; fi
echo "port-check: DID NOT PLAY (see $WORK/port-output.txt)"; exit 1
