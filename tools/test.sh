#!/usr/bin/env bash
# Builds and runs the tests. Game-data tests use the folders in WADSMITH_TEST_GAMES; when it is
# unset, the folders listed one per line in local-notes/test-games.txt (not committed) are used.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -z "${WADSMITH_TEST_GAMES:-}" && -f local-notes/test-games.txt ]]; then
    WADSMITH_TEST_GAMES="$(command grep -v '^#' local-notes/test-games.txt | paste -sd ':')"
    export WADSMITH_TEST_GAMES
fi
dotnet build WadSmith.slnx -c Release -warnaserror --nologo -v q
dotnet test tests/WadSmith.Tests -c Release --no-build "$@"
