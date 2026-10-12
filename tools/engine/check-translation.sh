#!/usr/bin/env bash
# Checks WadSmith's classic-to-GZDoom translation of every map in a base game against the engine.
#
#   tools/engine/check-translation.sh WORK_DIR IWAD
#
# Builds wadsmith-cli, asks the engine for its view of every map (dump-map.sh), then compares each
# with `wadsmith-cli translate --compare`. Prints one line per map and a total; exit 1 if any differ.
set -u
[ $# -eq 2 ] || { sed -n '2,8p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"
dotnet build "$ROOT/src/WadSmith.Cli" -c Release -v q --nologo > "$WORK/build.txt" 2>&1 || { cat "$WORK/build.txt"; exit 2; }
CLI=(dotnet "$ROOT/src/WadSmith.Cli/bin/Release/net10.0/wadsmith-cli.dll")
MAPS=$("${CLI[@]}" inspect "$IWAD" | awk '/^    [A-Z0-9]+ +(Doom|Hexen) format/ {print $1}' | paste -sd ',')
[ -n "$MAPS" ] || { echo "check-translation: no classic maps in $IWAD"; exit 2; }
"$HERE/dump-map.sh" "$WORK" "$IWAD" "$MAPS" > "$WORK/dump.txt" 2>&1
tail -n 3 "$WORK/dump.txt" | command grep -v "^dump-map:" || true
BAD=0; N=0
for MAP in $(tr ',' ' ' <<< "$MAPS"); do
  N=$((N + 1))
  if [ ! -f "$WORK/engine-$MAP.log" ] || ! command grep -q "WSDUMP END" "$WORK/engine-$MAP.log"; then
    echo "$MAP  no engine dump"; BAD=$((BAD + 1)); continue
  fi
  if OUT=$("${CLI[@]}" translate "$IWAD" --map="$MAP" --compare="$WORK/engine-$MAP.log" 2>&1); then
    echo "$MAP  matches"
  else
    BAD=$((BAD + 1))
    echo "$MAP  $(tail -n 1 <<< "$OUT" | sed 's/^ *//')"
    sed -n '2,6p' <<< "$OUT" | sed 's/^/      /'
  fi
done
echo "check-translation: $((N - BAD)) of $N maps match the engine."
[ $BAD -eq 0 ]
