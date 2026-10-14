#!/usr/bin/env bash
# The gate before a push: the whole suite the way CI runs it, N times (default 2).
# Run it in the background and keep working; push once it says GATE PASSED.
#   - on two CPUs (the CI runners have two to four; this machine's thirty-two hide the interleavings they see),
#   - with no system GZDoom or UZDoom data (CI has none; the free game data in scratch/ci-games stands in, as CI's does),
#   - warnings as errors.
# Every run must pass. A failed run prints its failed tests; the logs are in scratch/gate/. This machine is Linux:
# what only Windows sees (paths, timing) is caught by tools/push.sh, which runs CI on both before main moves.
#   tools/gate.sh [N]
set -uo pipefail
cd "$(dirname "$0")/.."
N="${1:-2}"   # two for every push: repeats catch only what fails now and then, and CI on Windows checks again
export SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy PULSE_SERVER=unix:/nonexistent/wadsmith PIPEWIRE_REMOTE=/nonexistent/wadsmith WADSMITH_OFFLINE=1
games="${WADSMITH_GATE_GAMES:-$PWD/scratch/ci-games}"
empty="$PWD/scratch/empty"
[ -d "$games" ] || { echo "gate: $games is missing (the free game data CI tests with)"; exit 2; }
command -v taskset > /dev/null && command -v unshare > /dev/null || { echo "gate: needs taskset and unshare"; exit 2; }
mkdir -p "$empty" scratch/gate

# The tests add the game data as a user adds a folder, and the folders beside and above an added one are searched too:
# beside scratch/ci-games that is all of scratch/, where other games lie (a Blasphemer zip made Blasphemer the first
# game, 2026-10-14). As on CI, whose data sits alone in the runner's temp folder, the tests get it in a folder of its own.
alone="$PWD/scratch/gate-games/games"
mkdir -p "$alone"
ln -sfn "$(cd "$games" && pwd)" "$alone/ci-games"
games="$alone/ci-games"

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
    # The engines' data folders installed here are covered by an empty one; a mount that fails stops the run.
    WADSMITH_TEST_GAMES="$games" taskset -c 0,1 unshare -rm sh -c \
        "for d in /usr/share/gzdoom /usr/share/games/uzdoom /usr/share/uzdoom; do if [ -d \"\$d\" ]; then mount --bind '$empty' \"\$d\" || exit 97; fi; done; dotnet test tests/WadSmith.Tests -c Release --no-build" > "$log" 2>&1
    line=$(grep -E "^(Passed|Failed)!" "$log" | tail -1 | cut -c1-100)
    echo "run $i of $N: ${line:-no result, see $log}"
    if ! grep -qE "^Passed!" "$log"; then
        rc=1
        grep -E "^\s+Failed " "$log" | sed 's/^/    /'
    fi
done

if (( rc == 0 )); then echo "GATE PASSED: $N runs on 2 CPUs, CI-style"; else echo "GATE FAILED"; fi
exit $rc
