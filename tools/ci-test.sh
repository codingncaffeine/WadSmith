#!/usr/bin/env bash
# The unit tests as CI runs them. A test that fails is run once more on its own; when it passes then, the job passes and
# the test is named as flaky (a warning on the run and a line in its summary), never hidden. A run that did not finish
# (a crash or a hang) and a run with more than five failures are real breakage and are not run again.
#   tools/ci-test.sh [RESULTS_DIR]
set -uo pipefail
cd "$(dirname "$0")/.."
results=${1:-TestResults}
mkdir -p "$results"
args=(tests/WadSmith.Tests -c Release --no-build --blame-hang --blame-hang-timeout 2m --logger "console;verbosity=normal" --results-directory "$results")

dotnet test "${args[@]}" --logger "trx;LogFileName=first.trx" 2>&1 | tee "$results/first.log"
first=${PIPESTATUS[0]}
[ "$first" -eq 0 ] && exit 0

if grep -qiE 'test run (was )?aborted|testhost process (crashed|exited)|inactivity time' "$results/first.log"; then
    echo "The test run did not finish: not run again."
    exit "$first"
fi

# The failed tests by method (a theory's cases run again together); names in TRX are XML-escaped, cut before any '('.
trx="$results/first.trx"
[ -f "$trx" ] || { echo "No test results were written: not run again."; exit "$first"; }
mapfile -t failed < <(grep -o '<UnitTestResult [^>]*outcome="Failed"[^>]*>' "$trx" | sed -n 's/.* testName="\([^"(]*\).*/\1/p' | sort -u)
if [ "${#failed[@]}" -eq 0 ] || [ "${#failed[@]}" -gt 5 ]; then
    echo "${#failed[@]} failed tests: not run again."
    exit "$first"
fi

filter=""
for name in "${failed[@]}"; do filter+="FullyQualifiedName=$name|"; done
echo "── running again on their own: ${failed[*]}"
dotnet test "${args[@]}" --filter "${filter%|}" --logger "trx;LogFileName=again.trx" 2>&1 | tee "$results/again.log"
again=${PIPESTATUS[0]}
[ "$again" -eq 0 ] || exit "$again"

for name in "${failed[@]}"; do
    echo "::warning title=Flaky test::$name failed, then passed when run again on its own"
    [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "- Flaky: \`$name\` failed, then passed when run again on its own" >> "$GITHUB_STEP_SUMMARY"
done
exit 0
