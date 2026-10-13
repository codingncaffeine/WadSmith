#!/usr/bin/env bash
# Builds the Linux release artifacts into packaging/out:
#   WadSmith-<ver>-linux-x64.tar.gz   self-contained, extract anywhere, run ./wadsmith (and ./wadsmith-cli)
#   wadsmith_<ver>_amd64.deb          system install (/usr/lib/wadsmith, /usr/bin/wadsmith)
# The version is VersionPrefix in Directory.Build.props. Check every artifact with packaging/smoke-test.sh
# and the .deb with packaging/deb-install-test.sh before it is released.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
# WADSMITH_OUT builds somewhere else (a copy swapped in by rename while the usual one is running).
OUT=${WADSMITH_OUT:-packaging/out}
PUB=$OUT/publish

rm -rf "$OUT" && mkdir -p "$PUB"

# Stale obj/ output can publish XAML that was never compiled: every release starts from clean trees.
for p in src/WadSmith src/WadSmith.Cli src/WadSmith.Editor src/WadSmith.Core; do rm -rf "$p/bin" "$p/obj"; done

echo "── publish v$VER (self-contained linux-x64)"
dotnet publish src/WadSmith/WadSmith.csproj -c Release -r linux-x64 --self-contained true \
    -p:VersionPrefix="$VER" -o "$PUB" -v q --nologo
# The command-line tool beside it, sharing the runtime and the WadSmith libraries (`wadsmith --help` points to it).
dotnet publish src/WadSmith.Cli/WadSmith.Cli.csproj -c Release -r linux-x64 --self-contained true \
    -p:VersionPrefix="$VER" -o "$PUB" -v q --nologo

# The views must be compiled into the app; a publish that fell back to loading XAML at run time is not shipped.
if ! grep -qa CompiledAvaloniaXaml "$PUB/wadsmith.dll"; then
    echo "!! wadsmith.dll carries no compiled XAML: refusing to package." >&2
    exit 3
fi

cp LICENSE "$PUB/LICENSE"
cp packaging/README.txt "$PUB/README.txt"
cp src/WadSmith/Assets/icon.png "$PUB/wadsmith.png"

echo "── tarball"
tar -C "$PUB" -czf "$OUT/WadSmith-$VER-linux-x64.tar.gz" .

echo "── deb"
DEB=$OUT/debroot
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/wadsmith" "$DEB/usr/bin" "$DEB/usr/share/applications" \
         "$DEB/usr/share/icons/hicolor/512x512/apps" "$DEB/usr/share/doc/wadsmith"
cp -a "$PUB/." "$DEB/usr/lib/wadsmith/"
rm -f "$DEB/usr/lib/wadsmith/README.txt" "$DEB/usr/lib/wadsmith/LICENSE" "$DEB/usr/lib/wadsmith/wadsmith.png"
cp packaging/README.txt "$DEB/usr/share/doc/wadsmith/README.txt"
cp LICENSE "$DEB/usr/share/doc/wadsmith/copyright"
cat > "$DEB/usr/bin/wadsmith" <<'WRAP'
#!/bin/sh
exec /usr/lib/wadsmith/wadsmith "$@"
WRAP
cat > "$DEB/usr/bin/wadsmith-cli" <<'WRAP'
#!/bin/sh
exec /usr/lib/wadsmith/wadsmith-cli "$@"
WRAP
chmod 755 "$DEB/usr/bin/wadsmith" "$DEB/usr/bin/wadsmith-cli"
cp src/WadSmith/Assets/icon.png "$DEB/usr/share/icons/hicolor/512x512/apps/wadsmith.png"
cp packaging/io.github.codingncaffeine.WadSmith.desktop "$DEB/usr/share/applications/"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: wadsmith
Version: $VER
Section: games
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libice6, libsm6, libegl1, libgl1, libwayland-client0, libxkbcommon0, zlib1g
Recommends: libsdl2-2.0-0
Suggests: gzdoom
Maintainer: WadSmith <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/WadSmith
Description: Map editor for Doom, Heretic, Hexen and Strife, made for GZDoom
 WadSmith remixes any map of the games you own or starts a new one, with a 3D
 view beside the plan, smart doors, lifts, stairs and windows, a live Problems
 panel, and Play straight into GZDoom. It never changes your game files.
 .
 GZDoom is needed to play maps (install it from your distribution or Flatpak);
 SDL2 plays an object's sound when you point at it on the shelf.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/wadsmith_${VER}_amd64.deb" > /dev/null
rm -rf "$DEB"

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
