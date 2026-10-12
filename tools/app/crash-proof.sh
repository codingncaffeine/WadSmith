#!/usr/bin/env bash
# Phase 2's crash proof: an edit is made, then the process is killed outright (no chance to save on
# the way out). One second after an edit, the edit must already be on disk; a kill a third of a
# second after it (inside the save delay) is the control and is expected to lose it.
#
#   tools/app/crash-proof.sh WORK_DIR
set -u
[ $# -ge 1 ] || { sed -n '2,7p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"
dotnet build "$ROOT/tools/WadSmith.Capture" -c Release -v q --nologo > "$WORK/build.txt" 2>&1 || { cat "$WORK/build.txt"; exit 2; }
status=0
for case in "1.0 kept" "0.3 lost"; do
  set -- $case; after=$1; expect=$2
  out="$WORK/after-$after"; rm -rf "$out"
  dotnet "$ROOT/tools/WadSmith.Capture/bin/Release/net10.0/WadSmith.Capture.dll" "$out" --crash-after="$after" crash-edit > "$out.txt" 2>&1
  code=$?
  map="$out/profile/crash-project/maps/MAP01.wad"
  if command grep -aq "heightfloor = 777" "$map" 2>/dev/null; then got=kept; else got=lost; fi
  echo "crash-proof: killed $after s after the edit (exit $code): edit $got (expected $expect)"
  [ "$got" = "$expect" ] || status=1
done
exit $status
