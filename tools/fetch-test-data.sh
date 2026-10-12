#!/usr/bin/env bash
# Downloads the free data the tests use when no game files are present (CI): Freedoom's two IWADs
# and GZDoom's own data file (gzdoom.pk3, for its translation tables). Every download is checked
# against a pinned SHA-256.
#
#   tools/fetch-test-data.sh FOLDER
#
# Then point the tests at it: WADSMITH_TEST_GAMES=FOLDER
set -euo pipefail
[ $# -eq 1 ] || { sed -n '2,9p' "$0"; exit 2; }
OUT="$1"; mkdir -p "$OUT"; cd "$OUT"

fetch() { # url file sha256
  if [ ! -f "$2" ] || ! echo "$3  $2" | sha256sum -c --status; then
    curl -sSfL --retry 3 -o "$2" "$1"
    echo "$3  $2" | sha256sum -c --quiet
  fi
}

fetch https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedoom-0.13.0.zip freedoom-0.13.0.zip 3f9b264f3e3ce503b4fb7f6bdcb1f419d93c7b546f4df3e874dd878db9688f59
unzip -o -q -j freedoom-0.13.0.zip 'freedoom-0.13.0/freedoom1.wad' 'freedoom-0.13.0/freedoom2.wad'

fetch https://github.com/ZDoom/gzdoom/releases/download/g4.14.2/gzdoom-4-14-2-windows.zip gzdoom-4-14-2-windows.zip 6d01c5bb48610cebfa9dfd0b7705a1e20bfd56032a74484ac256d8c4e3c51bee
unzip -o -q -j gzdoom-4-14-2-windows.zip gzdoom.pk3

rm -f freedoom-0.13.0.zip gzdoom-4-14-2-windows.zip
ls -l
