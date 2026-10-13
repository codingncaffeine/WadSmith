#!/usr/bin/env bash
# Paths that only work on Linux: a '/' cut, split or trimmed off a string that is a file-system path breaks the Windows
# CI job (2026-10-12: a test cut a project folder at "/Cave copy"; the engine's "$HOME/mywads" became home/mywads).
# Use Path.Combine, Path.GetFileName or Path.DirectorySeparatorChar instead. Entries inside archives always use '/',
# so a line about one says so: `// archive path`; a '/' that is no path separator at all (an OBJ face corner, "1/2/3") says `// not a path`.
#   tools/windows-paths.sh
cd "$(dirname "$0")/.."
hits=$(grep -rnE "(IndexOf|LastIndexOf|Split|TrimEnd|TrimStart|EndsWith|StartsWith)\(('/'\)|\"/\"|\"/[^/\"][^\"]*\")" src tests tools --include=*.cs \
    | grep -v "/obj/\|/bin/" | grep -v "// archive path\|// not a path")
if [ -n "$hits" ]; then
    echo "$hits"
    echo "WINDOWS PATHS: a '/' is cut from a string above; use Path.Combine / Path.GetFileName / Path.DirectorySeparatorChar, or mark an archive path with // archive path (a slash that is no path separator: // not a path)"
    exit 1
fi
echo "windows paths: clean"
