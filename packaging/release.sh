#!/usr/bin/env bash
# One command from a green main to a published release: the checks, the artifacts, every smoke test, the tag, the
# GitHub release and the AUR package. It stops at the first failure, and nothing outward (the tag, the release, the
# AUR push) happens before every local check has passed.
#
#   packaging/release.sh            the version is VersionPrefix in Directory.Build.props;
#                                   packaging/RELEASE-NOTES-<version>.md is the release's text
#
# Before it: tools/gate.sh, then push. Needs gh (logged in), podman or docker, xvfb-run, dbus-run-session, makepkg and
# an SSH key for aur.archlinux.org. Takes about half an hour, most of it the container test, CI and the Windows build
# (.github/workflows/windows-release.yml, started here for the commit released and smoke-run on its Windows runner).
set -euo pipefail
cd "$(dirname "$0")/.."
VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
TAG="v$VER"
TARBALL="packaging/out/WadSmith-$VER-linux-x64.tar.gz"
DEB="packaging/out/wadsmith_${VER}_amd64.deb"
ZIP="packaging/out/WadSmith-$VER-win-x64.zip"
NOTES="packaging/RELEASE-NOTES-$VER.md"
AUR=scratch/aur-wadsmith-bin
say() { printf '\n── %s\n' "$*"; }

say "checks for $TAG"
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "there are uncommitted changes"; exit 1; }
git fetch -q origin
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "HEAD is not origin/main: run tools/gate.sh, then push, then this"; exit 1; }
if git rev-parse -q --verify "refs/tags/$TAG" > /dev/null; then echo "the tag $TAG already exists"; exit 1; fi
[ -f "$NOTES" ] || { echo "no $NOTES"; exit 1; }
tools/precommit-scan.sh --all > /dev/null || { echo "precommit-scan found something"; exit 1; }
tools/identity-audit.sh > /dev/null || { echo "identity-audit found something"; exit 1; }
gh auth status > /dev/null 2>&1 || { echo "gh is not logged in"; exit 1; }
echo "ok"

say "CI for $(git rev-parse --short HEAD)"
# tools/push.sh ran CI on this very commit (on the gate branch) before main moved: a green run there is enough, and the
# same run started again on main is not waited for.
for i in $(seq 1 60); do
    s=$(gh run list --commit "$(git rev-parse HEAD)" --json status,conclusion -q '(map(select(.conclusion == "success")) | .[0]) // .[0] | "\(.status) \(.conclusion)"' 2>/dev/null || true)
    case "$s" in
        "completed success") echo "green"; break ;;
        "completed "*) echo "CI: $s"; exit 1 ;;
    esac
    [ "$i" = 60 ] && { echo "CI did not finish in 30 minutes"; exit 1; }
    sleep 30
done

say "the Windows build of $(git rev-parse --short HEAD), started on CI"
SHA=$(git rev-parse HEAD)
STARTED=$(date -u +%Y-%m-%dT%H:%M:%SZ)
gh workflow run windows-release.yml --ref main -f sha="$SHA"
WIN=""
for i in $(seq 1 24); do
    WIN=$(gh run list --workflow windows-release.yml --event workflow_dispatch --json databaseId,headSha,createdAt -q "[.[] | select(.headSha == \"$SHA\" and .createdAt >= \"$STARTED\")][0].databaseId // empty" 2>/dev/null || true)
    [ -n "$WIN" ] && break
    sleep 5
done
[ -n "$WIN" ] || { echo "the Windows build did not start"; exit 1; }
echo "run $WIN"

say "artifacts"
packaging/build-release.sh

say "the Windows zip"
for i in $(seq 1 60); do
    s=$(gh run view "$WIN" --json status,conclusion -q '"\(.status) \(.conclusion)"' 2>/dev/null || true)
    case "$s" in
        "completed success") echo "built and smoke-run on Windows"; break ;;
        "completed "*) echo "the Windows build: $s (gh run view $WIN --log-failed)"; exit 1 ;;
    esac
    [ "$i" = 60 ] && { echo "the Windows build did not finish in 30 minutes"; exit 1; }
    sleep 30
done
gh run download "$WIN" -n windows-zip -D packaging/out
[ -f "$ZIP" ] || { echo "no $ZIP in the run's artifact"; exit 1; }
# Listed once: grep -q stops at its match, and under pipefail the listing it cut off would count as a failure.
listing=$(unzip -l "$ZIP")
for f in wadsmith.exe wadsmith-cli.exe SDL2.dll README-SDL.txt LICENSE.txt; do
    grep -q " $f$" <<< "$listing" || { echo "$ZIP has no $f"; exit 1; }
done
echo "ok: $(du -h "$ZIP" | cut -f1)"

say "smoke: the tarball and the .deb, on a private screen and bus"
xvfb-run -a dbus-run-session --config-file=packaging/isolated-bus.conf -- packaging/smoke-test.sh "$TARBALL" "$DEB"

say "smoke: the AUR package, built from the local tarball"
rm -rf scratch/aur-build && mkdir -p scratch/aur-build
cp packaging/aur/PKGBUILD "$TARBALL" scratch/aur-build/
(cd scratch/aur-build && sed -i 's|^source=.*|source=("WadSmith-$pkgver-linux-x64.tar.gz")|; s|^sha256sums=.*|sha256sums=("SKIP")|; s|^pkgver=.*|pkgver='"$VER"'|' PKGBUILD \
    && makepkg -f --nodeps > makepkg.log 2>&1) || { tail -20 scratch/aur-build/makepkg.log; exit 1; }
xvfb-run -a dbus-run-session --config-file=packaging/isolated-bus.conf -- packaging/smoke-test.sh scratch/aur-build/wadsmith-bin-"$VER"-1-x86_64.pkg.tar.zst

say "the .deb installed in clean Debian and Ubuntu containers"
packaging/deb-install-test.sh "$DEB"

say "tag $TAG"
git tag -a "$TAG" -m "WadSmith $VER"
git push -q origin "$TAG"

say "GitHub release"
gh release create "$TAG" --title "WadSmith $VER" --notes-file "$NOTES" "$TARBALL" "$DEB" "$ZIP"
gh release view "$TAG" --json url -q .url

say "AUR: wadsmith-bin $VER-1"
sha=$(sha256sum "$TARBALL" | cut -d' ' -f1)
sed -i "s|^pkgver=.*|pkgver=$VER|; s|^pkgrel=.*|pkgrel=1|; s|^sha256sums=.*|sha256sums=('$sha')|" packaging/aur/PKGBUILD
[ -d "$AUR/.git" ] || git clone -q ssh://aur@aur.archlinux.org/wadsmith-bin.git "$AUR"
git -C "$AUR" config user.name codingncaffeine
git -C "$AUR" config user.email codingncaffeine@users.noreply.github.com
cp packaging/aur/PKGBUILD "$AUR/PKGBUILD"
(cd "$AUR" && makepkg --printsrcinfo > .SRCINFO && git add PKGBUILD .SRCINFO && git commit -q -m "Update to $VER-1" && git push -q origin HEAD:master)

say "the AUR package as a user builds it: a fresh clone, the tarball downloaded from the release"
rm -rf scratch/aur-user
git clone -q https://aur.archlinux.org/wadsmith-bin.git scratch/aur-user
(cd scratch/aur-user && makepkg -f --nodeps > makepkg.log 2>&1 && ls -1 ./*.pkg.tar.zst) || { tail -20 scratch/aur-user/makepkg.log; exit 1; }
xvfb-run -a dbus-run-session --config-file=packaging/isolated-bus.conf -- packaging/smoke-test.sh scratch/aur-user/wadsmith-bin-"$VER"-1-x86_64.pkg.tar.zst

say "the checksum, kept in the repository"
git add packaging/aur/PKGBUILD
git commit -q -m "AUR: the $VER tarball's checksum" || true
git push -q origin main

say "released $TAG"
sha256sum "$TARBALL" "$DEB" "$ZIP"
