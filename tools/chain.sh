#!/usr/bin/env bash
# Takes one commit from the main tree to main: a worktree of its own at that commit (so work goes on in the main tree
# meanwhile, and nothing uncommitted reaches the gate), the hands-on build from it swapped into packaging/out by rename,
# then tools/gate.sh there, then tools/push.sh for that commit. Run it in the background with the SDK on the PATH.
#   tools/chain.sh [COMMIT] [GATE_RUNS]     (default HEAD, 2)
set -uo pipefail
cd "$(dirname "$0")/.."
main=$PWD
sha=$(git rev-parse "${1:-HEAD}") || exit 2
runs=${2:-2}
tree=$main/scratch/chain-tree
mkdir -p scratch/gate

git worktree remove --force "$tree" > /dev/null 2>&1
rm -rf "$tree"
git worktree prune
git worktree add -q --detach "$tree" "$sha" || { echo "CHAIN FAILED: no worktree for ${sha:0:7}"; exit 1; }
mkdir -p "$tree/scratch/empty"
ln -s "$main/scratch/ci-games" "$tree/scratch/ci-games"

echo "── hands-on build of ${sha:0:7}"
rm -rf packaging/out-next
if ! (cd "$tree" && WADSMITH_OUT="$main/packaging/out-next" packaging/build-release.sh) > scratch/gate/handson.log 2>&1; then
    tail -15 scratch/gate/handson.log
    echo "CHAIN FAILED: the hands-on build of ${sha:0:7}"
    exit 1
fi

rm -rf packaging/out-old
[ -d packaging/out ] && mv packaging/out packaging/out-old
mv packaging/out-next packaging/out
echo "hands-on build ok: $(packaging/out/publish/wadsmith --version 2>/dev/null | head -1)"

(cd "$tree" && tools/gate.sh "$runs") || { echo "CHAIN FAILED: the gate for ${sha:0:7} (logs in $tree/scratch/gate)"; exit 1; }
tools/push.sh "$sha"
