#!/usr/bin/env bash
# Scans what is about to be committed for things that must never reach the public repository:
# personal data, conversation in comments, game data files and local notes.
#
# Personal patterns live in the gitignored local-notes/private-patterns.txt (one extended regex
# per line) so the patterns themselves never reach the repository. The scan refuses to run
# without that file, and proves every pattern matches its own planted sample before trusting it.
#
# Usage: tools/precommit-scan.sh            (scans the staged changes)
#        tools/precommit-scan.sh --all      (scans every tracked file)
# Exit 0 = clean, 1 = something found, 2 = could not scan.
set -euo pipefail
cd "$(dirname "$0")/.."

private=local-notes/private-patterns.txt
if [[ ! -s "$private" ]]; then
    echo "precommit-scan: $private is missing or empty; refusing to report clean" >&2
    exit 2
fi

conversation='the user (said|asked|wants)|you asked|we decided|as discussed|per our|per the user'

if [[ "${1:-}" == "--all" ]]; then
    mapfile -t files < <(git ls-files)
    content() { cat -- "$1"; }
else
    mapfile -t files < <(git diff --cached --name-only --diff-filter=ACMR)
    content() { git show ":$1"; }
fi

found=0

# Positive control: each pattern must find its own sample, or the scan cannot see anything.
while IFS= read -r pattern; do
    [[ -z "$pattern" || "$pattern" == \#* ]] && continue
    sample="${pattern//\\/}"
    sample="${sample//[\^\$]/}"
    if ! printf '%s\n' "$sample" | command grep -qiE -- "$pattern"; then
        echo "precommit-scan: pattern does not match its own sample, fix it: $pattern" >&2
        exit 2
    fi
done < "$private"
if ! printf 'per our call\n' | command grep -qiE -- "$conversation"; then
    echo "precommit-scan: the conversation pattern failed its control" >&2
    exit 2
fi

for f in "${files[@]}"; do
    case "$f" in
        local-notes/*|scratch/*|snapshots/*)
            echo "precommit-scan: local-only path staged: $f"; found=1; continue ;;
    esac
    case "${f,,}" in
        *.wad|*.pk3|*.pk7|*.ipk3|*.iwad|*.lmp|*.deh|*.bex)
            echo "precommit-scan: game data file staged: $f"; found=1; continue ;;
    esac
    [[ -f "$f" ]] || continue
    if file -b --mime "$f" | command grep -q 'charset=binary'; then
        continue
    fi
    if content "$f" | command grep -n -iE -f <(command grep -vE '^(#|$)' "$private") >/dev/null; then
        echo "precommit-scan: personal data in $f:"
        content "$f" | command grep -n -iE -f <(command grep -vE '^(#|$)' "$private") | sed 's/^/    /'
        found=1
    fi
    if [[ "$f" != tools/precommit-scan.sh ]] && content "$f" | command grep -n -iE -- "$conversation" >/dev/null; then
        echo "precommit-scan: conversational wording in $f:"
        content "$f" | command grep -n -iE -- "$conversation" | sed 's/^/    /'
        found=1
    fi
done

if [[ $found -eq 0 ]]; then
    echo "precommit-scan: ${#files[@]} file(s) clean"
fi
exit $found
