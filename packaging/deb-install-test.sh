#!/usr/bin/env bash
# Installs the .deb in clean Debian and Ubuntu containers (apt resolves its Depends exactly as a user's apt would),
# checks every shipped native library loads there, and launches the app on a virtual screen with
# packaging/smoke-test.sh. The in-container steps are packaging/deb-install-check.sh.
#
# Usage: packaging/deb-install-test.sh <wadsmith_*.deb> [image ...]
#   default images: Debian 12 (bookworm), Debian 13 (trixie), Ubuntu 22.04, Ubuntu 24.04
# Exit: 0 every image passed | 1 something failed | 2 no container runtime
set -uo pipefail
cd "$(dirname "$0")/.."

deb=$(realpath "${1:?usage: deb-install-test.sh <wadsmith_*.deb> [image ...]}")
shift
images=("$@")
(( ${#images[@]} )) || images=(docker.io/library/debian:bookworm docker.io/library/debian:trixie
                               docker.io/library/ubuntu:22.04 docker.io/library/ubuntu:24.04)
runtime=$(command -v podman || command -v docker || true)
[[ -n $runtime ]] || { echo "no podman/docker" >&2; exit 2; }

rc=0
for image in "${images[@]}"; do
    printf '\n######## %s\n' "$image"
    "$runtime" run --rm --network=host -e DEBIAN_FRONTEND=noninteractive \
        -v "$deb":/tmp/wadsmith.deb:ro,Z \
        -v "$PWD/packaging/smoke-test.sh":/tmp/smoke-test.sh:ro,Z \
        -v "$PWD/packaging/deb-install-check.sh":/tmp/deb-install-check.sh:ro,Z \
        "$image" /tmp/deb-install-check.sh || rc=1
done
printf '\n'
if (( rc == 0 )); then echo "DEB INSTALL TEST PASSED on ${#images[@]} images"; else echo "DEB INSTALL TEST FAILED"; fi
exit $rc
