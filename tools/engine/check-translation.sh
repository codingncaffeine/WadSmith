#!/usr/bin/env bash
# Checks WadSmith's classic-to-GZDoom translation of every map in a base game against the engine.
#
#   tools/engine/check-translation.sh [--compare-only] WORK_DIR IWAD
#
# Builds wadsmith-cli, asks the engine for its view of every map (dump-map.sh; skipped with
# --compare-only when the logs are already in WORK_DIR), then compares each with
# `wadsmith-cli translate --compare`. A map the engine itself patches by checksum (its
# compatibility tables) is reported separately: those patches apply to the original only.
# Exit 1 if any other map differs.
set -u
ONLY_COMPARE=0
if [ "${1:-}" = "--compare-only" ]; then ONLY_COMPARE=1; shift; fi
[ $# -eq 2 ] || { sed -n '2,11p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; IWAD="$(realpath "$2")"
dotnet build "$ROOT/src/WadSmith.Cli" -c Release -v q --nologo > "$WORK/build.txt" 2>&1 || { cat "$WORK/build.txt"; exit 2; }
CLI=(dotnet "$ROOT/src/WadSmith.Cli/bin/Release/net10.0/wadsmith-cli.dll")
MAPS=$("${CLI[@]}" inspect "$IWAD" | awk '/^    [A-Z0-9]+ +(Doom|Hexen) format/ {print $1}' | paste -sd ',')
[ -n "$MAPS" ] || { echo "check-translation: no classic maps in $IWAD"; exit 2; }
if [ $ONLY_COMPARE -eq 0 ]; then
  "$HERE/dump-map.sh" "$WORK" "$IWAD" "$MAPS" > "$WORK/dump.txt" 2>&1
fi
MATCH=0; PATCHED=0; BAD=0; N=0
for MAP in $(tr ',' ' ' <<< "$MAPS"); do
  N=$((N + 1))
  if [ ! -f "$WORK/engine-$MAP.log" ] || ! command grep -q "WSDUMP END" "$WORK/engine-$MAP.log"; then
    echo "$MAP  no engine dump"; BAD=$((BAD + 1)); continue
  fi
  OUT=$("${CLI[@]}" translate "$IWAD" --map="$MAP" --compare="$WORK/engine-$MAP.log" 2>&1)
  case $? in
    0) MATCH=$((MATCH + 1)); echo "$MAP  matches" ;;
    3) PATCHED=$((PATCHED + 1)); echo "$MAP  differs only on a map the engine patches" ;;
    *) BAD=$((BAD + 1)); echo "$MAP  $(tail -n 1 <<< "$OUT" | sed 's/^ *//')"; sed -n '2,6p' <<< "$OUT" | sed 's/^/      /' ;;
  esac
done
echo "check-translation: $MATCH of $N maps match the engine exactly; $PATCHED are maps the engine patches; $BAD differ otherwise."
[ $BAD -eq 0 ]
