#!/usr/bin/env bash
# Every map of a game through WadSmith and back out: each is remixed (into GZDoom's format, as the editor keeps it) and
# exported for a port; the game's own maps lose nothing in their own format, so every map the report lists something for
# is a finding (or one of GZDoom's own repairs to the original, which the note names). With PORT, each export is also
# played in that port in a private nested session (tools/engine/port-check.sh: a walking demo for Doom, warp and run for
# the others).
#
#   tools/engine/export-sweep.sh WORK_DIR IWAD FOR [PORT] [DEMO_FORMAT]
#   FOR: vanilla, limit-removing, boom or mbf21. DEMO_FORMAT: vanilla (default), boom, mbf21 or none.
set -u
[ $# -ge 3 ] || { sed -n '2,10p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$2"; FOR="$3"; PORT="${4:-}"; DEMO="${5:-vanilla}"
source "$ROOT/scratch/env.sh" >/dev/null 2>&1 || true
CLI=(dotnet run --no-build --project "$ROOT/src/WadSmith.Cli" -c Release --)
GAMES="$(dirname "$IWAD")"
# The game's maps: each marker followed by THINGS.
mapfile -t MAPS < <(node -e '
const b = require("fs").readFileSync(process.argv[1]); const n = b.readInt32LE(4), d = b.readInt32LE(8); const names = [];
for (let i = 0; i < n; i++) names.push(b.toString("latin1", d + i * 16 + 8, d + i * 16 + 16).replace(/\0.*$/, ""));
names.forEach((x, i) => { if (names[i + 1] === "THINGS" && /^(E\dM\d|MAP\d\d)$/.test(x)) console.log(x); });' "$IWAD")
clean=0; lossy=0; played=0; failed=0
for m in "${MAPS[@]}"; do
  p="$WORK/$m"; rm -rf "$p"
  "${CLI[@]}" remix "$IWAD" "--map=$m" "--out=$p" > "$WORK/$m.remix.txt" 2>&1 || { echo "sweep: $m: REMIX FAILED"; lossy=$((lossy + 1)); continue; }
  out="$("${CLI[@]}" export "$p" "--for=$FOR" "--out=$WORK/$m.wad" "--games=$GAMES" --verbose 2>&1 | grep -v '^Wrote')"
  if [ "$(echo "$out" | grep -v '^Note:')" = "Nothing is lost." ]; then clean=$((clean + 1)); else lossy=$((lossy + 1)); echo "sweep: $m:"; echo "$out" | sed 's/^/    /'; fi
  if [ -n "$PORT" ]; then
    if "$HERE/port-check.sh" "$WORK/play-$m" "$PORT" "$IWAD" "$WORK/$m.wad" "$m" "$DEMO" > "$WORK/$m.play.txt" 2>&1; then played=$((played + 1)); else failed=$((failed + 1)); echo "sweep: $m: DID NOT PLAY in $PORT ($(command grep -hiE 'not found|unknown|error' "$WORK/play-$m/port-output.txt" | head -1))"; fi
  fi
done
echo "sweep: $(basename "$IWAD") for $FOR: ${#MAPS[@]} maps, $clean lose nothing, $lossy lose something${PORT:+; in $PORT: $played played, $failed did not}"
