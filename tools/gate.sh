#!/usr/bin/env bash
# The gate before a push: the whole suite the way CI runs it, N times (default 3; use 5 after a change to the tests,
# the harness or anything concurrent). Run it in the background and keep working; push once it says GATE PASSED.
#   - on two CPUs (the CI runners have two to four; this machine's thirty-two hide the interleavings they see),
#   - with no system GZDoom (CI has none; the free game data in scratch/ci-games stands in, as CI's does),
#   - warnings as errors.
# Every run must pass. A failed run prints its failed tests; the logs are in scratch/gate/. This machine is Linux:
# what only Windows sees (paths, timing) is caught by tools/push.sh, which runs CI on both before main moves.
#   tools/gate.sh [N]
set -uo pipefail
cd "$(dirname "$0")/.."
N="${1:-3}"   # three for an ordinary push; five after a change to the tests, the harness or anything concurrent
export SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy PULSE_SERVER=unix:/nonexistent/wadsmith PIPEWIRE_REMOTE=/nonexistent/wadsmith
games="${WADSMITH_GATE_GAMES:-$PWD/scratch/ci-games}"
empty="$PWD/scratch/empty"
[ -d "$games" ] || { echo "gate: $games is missing (the free game data CI tests with)"; exit 2; }
command -v taskset > /dev/null && command -v unshare > /dev/null || { echo "gate: needs taskset and unshare"; exit 2; }
mkdir -p "$empty" scratch/gate

echo "── paths that would break the Windows job"
tools/windows-paths.sh || { echo "GATE FAILED: Windows paths"; exit 1; }

echo "── build, warnings as errors"
if ! dotnet build WadSmith.slnx -c Release -warnaserror --nologo -v q > scratch/gate/build.log 2>&1; then
    grep -E "error" scratch/gate/build.log | sort -u | head -20
    echo "GATE FAILED: the build"
    exit 1
fi

rc=0
for i in $(seq 1 "$N"); do
    log="scratch/gate/run-$i.log"
    WADSMITH_TEST_GAMES="$games" taskset -c 0,1 unshare -rm sh -c \
        "mount --bind '$empty' /usr/share/gzdoom && dotnet test tests/WadSmith.Tests -c Release --no-build" > "$log" 2>&1
    line=$(grep -E "^(Passed|Failed)!" "$log" | tail -1 | cut -c1-100)
    echo "run $i of $N: ${line:-no result, see $log}"
    if ! grep -qE "^Passed!" "$log"; then
        rc=1
        grep -E "^\s+Failed " "$log" | sed 's/^/    /'
    fi
done

if (( rc == 0 )); then echo "GATE PASSED: $N runs on 2 CPUs, CI-style"; else echo "GATE FAILED"; fi
exit $rc
