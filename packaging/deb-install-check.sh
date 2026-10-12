#!/usr/bin/env bash
# Runs INSIDE a clean Debian/Ubuntu container (packaging/deb-install-test.sh mounts it with the .deb and smoke-test.sh):
# installs the package with apt, checks every shipped native binary resolves its libraries, then launches it on a
# virtual X screen with smoke-test.sh. Exit 0 = everything passed.
set -uo pipefail
apt-get update -qq >/dev/null
if ! apt-get install -y -q /tmp/wadsmith.deb xvfb xauth libarchive-tools > /tmp/apt.log 2>&1; then
    tail -25 /tmp/apt.log
    exit 1
fi
. /etc/os-release
echo "installed $(dpkg-query -W -f='${Package} ${Version}' wadsmith) on $PRETTY_NAME"

problems=0
[ -x /usr/bin/wadsmith ] || { echo "FAIL /usr/bin/wadsmith missing"; problems=1; }
[ -f /usr/share/applications/io.github.codingncaffeine.WadSmith.desktop ] || { echo "FAIL no desktop entry"; problems=1; }

# Every shipped native binary must resolve its libraries, symbol versions included, on this distro. Exception: .NET's
# LTTng trace provider, which the runtime loads only when LTTng is installed.
for f in /usr/lib/wadsmith/wadsmith /usr/lib/wadsmith/*.so*; do
    case $(basename "$f") in libcoreclrtraceptprovider.so) continue ;; esac
    bad=$(ldd "$f" 2>&1 | grep -E "not found|version .* not found" || true)
    if [ -n "$bad" ]; then
        echo "FAIL $(basename "$f") does not load here:"
        echo "$bad" | sed 's/^/        /'
        problems=1
    fi
done
[ $problems = 1 ] || echo "ok   every shipped library resolves"

# The installed command, not only the payload: /usr/bin/wadsmith --version.
v=$(/usr/bin/wadsmith --version 2>&1 | head -1)
case $v in WadSmith*) echo "ok   /usr/bin/wadsmith --version: $v" ;; *) echo "FAIL /usr/bin/wadsmith --version said: $v"; problems=1 ;; esac

xvfb-run -a /tmp/smoke-test.sh /tmp/wadsmith.deb || problems=1
exit $problems
