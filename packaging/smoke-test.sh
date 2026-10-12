#!/usr/bin/env bash
# Smoke-tests a built WadSmith artifact: unpacks it, checks the payload, then LAUNCHES it with a private profile on a
# virtual X screen and requires it to open its window, stay up, and quit cleanly on SIGTERM. A packaging slip (a
# narrowed copy, uncompiled XAML, a missing native library) installs cleanly and only shows when the app runs.
#
# Usage: packaging/smoke-test.sh <artifact> [...]     a release .tar.gz, a .deb, or an Arch .pkg.tar.zst
# On a desktop machine run it as:
#   xvfb-run -a dbus-run-session --config-file=packaging/isolated-bus.conf -- packaging/smoke-test.sh <artifact>
# (a private screen, then a private bus that starts no services, so nothing reaches the desktop); it refuses a real display.
# Exit: 0 all passed | 1 a check failed | 2 could not run
set -uo pipefail

MIN_ASSEMBLIES=150
STAY_SECONDS=10

fail() { printf '  FAIL  %s\n' "$*"; FAILED=1; }
ok()   { printf '  ok    %s\n' "$*"; }

# Never the user's screen, Wayland session or sound: Xvfb only, nothing audible.
case "${DISPLAY:-}" in :9[0-9]*|:[1-9][0-9][0-9]*) ;; *) [ -n "${XVFB_OK:-}" ] || { echo "smoke-test: run under xvfb-run (DISPLAY=${DISPLAY:-none})"; exit 2; } ;; esac
unset WAYLAND_DISPLAY
export WADSMITH_WAYLAND=0 SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy PULSE_SERVER=unix:/nonexistent/wadsmith PIPEWIRE_REMOTE=/nonexistent/wadsmith

smoke_one() {
    local artifact="$1" tmp payload
    FAILED=0
    printf '\n== %s ==\n' "$(basename "$artifact")"
    [[ -f $artifact ]] || { fail "no such file"; return 1; }
    tmp=$(mktemp -d) || return 2
    trap 'rm -rf "$tmp"; trap - RETURN' RETURN

    case $artifact in
        *.tar.gz|*.pkg.tar.zst|*.pkg.tar.xz) bsdtar -xf "$artifact" -C "$tmp" 2>/dev/null || tar -xf "$artifact" -C "$tmp" ;;
        *.deb) dpkg-deb -x "$artifact" "$tmp" ;;
        *) fail "unrecognised artifact type"; return 1 ;;
    esac || { fail "could not unpack"; return 1; }

    payload=$(find "$tmp" -name wadsmith -type f -perm -u+x -printf '%h\n' | grep -v '/usr/bin$' | head -1)
    [[ -n $payload ]] || { fail "no wadsmith apphost in the artifact"; return 1; }
    ok "payload at ${payload#"$tmp"}/"

    local n f
    n=$(find "$payload" -maxdepth 1 -name '*.dll' | wc -l)
    if (( n < MIN_ASSEMBLIES )); then fail "only $n managed assemblies (expected >= $MIN_ASSEMBLIES)"; else ok "$n managed assemblies"; fi
    for f in wadsmith.dll WadSmith.Core.dll WadSmith.Editor.dll wadsmith.runtimeconfig.json wadsmith.deps.json \
             wadsmith-cli wadsmith-cli.dll wadsmith-cli.runtimeconfig.json wadsmith-cli.deps.json \
             libcoreclr.so libhostfxr.so libSkiaSharp.so libHarfBuzzSharp.so; do
        [[ -f $payload/$f ]] || fail "$f missing"
    done
    (( FAILED )) || ok "core files present"
    grep -qa CompiledAvaloniaXaml "$payload/wadsmith.dll" && ok "views compiled into wadsmith.dll" || fail "wadsmith.dll has no compiled XAML"

    local version
    version=$("$payload/wadsmith" --version 2>&1 | head -1)
    [[ $version == WadSmith* ]] && ok "--version: $version" || fail "--version said: $version"
    version=$("$payload/wadsmith-cli" --version 2>&1 | head -1)
    [[ $version == *0.* ]] && ok "wadsmith-cli --version: $version" || fail "wadsmith-cli --version said: $version"
    "$payload/wadsmith-cli" help > "$tmp/cli-help.txt" 2>&1 && grep -q extract "$tmp/cli-help.txt" && ok "wadsmith-cli lists its commands" || fail "wadsmith-cli help: $(head -3 "$tmp/cli-help.txt" | tr '\n' ' ')"

    # Launch: a private profile, wait for the window, stay up, quit on SIGTERM.
    local profile=$tmp/profile log pid code
    mkdir -p "$profile"
    log=$profile/state/logs/wadsmith.log
    XDG_CONFIG_HOME=$tmp/xdg/config XDG_DATA_HOME=$tmp/xdg/data XDG_CACHE_HOME=$tmp/xdg/cache XDG_STATE_HOME=$tmp/xdg/state \
        "$payload/wadsmith" --portable "$profile" > "$tmp/out.txt" 2>&1 &
    pid=$!
    for _ in $(seq 1 120); do
        grep -q "Windowing:" "$log" 2>/dev/null && break
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.25
    done
    if grep -q "Windowing:" "$log" 2>/dev/null; then ok "window opened ($(grep -m1 -o 'Windowing:.*' "$log"))"; else fail "no window: $(tail -5 "$tmp/out.txt" "$log" 2>/dev/null | tr '\n' ' ')"; fi
    sleep "$STAY_SECONDS"
    if kill -0 "$pid" 2>/dev/null; then
        ok "still running after ${STAY_SECONDS} s"
        kill -TERM "$pid"
        for _ in $(seq 1 50); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
        if kill -0 "$pid" 2>/dev/null; then fail "still running 5 s after SIGTERM"; kill -KILL "$pid"; else wait "$pid"; code=$?; ok "quit on SIGTERM (exit $code)"; fi
    else
        wait "$pid"; code=$?
        fail "exited by itself (code $code): $(tail -5 "$tmp/out.txt" | tr '\n' ' ')"
    fi
    if grep -E " (ERR|FTL) " "$log" > "$tmp/errors.txt" 2>/dev/null; then fail "errors in the log:"; sed 's/^/        /' "$tmp/errors.txt" | head -10; else ok "no errors in the log"; fi
    return $FAILED
}

[[ $# -ge 1 ]] || { sed -n '2,10p' "$0"; exit 2; }
rc=0
for a in "$@"; do smoke_one "$(realpath "$a")" || rc=1; done
printf '\n'; if (( rc == 0 )); then echo "SMOKE TEST PASSED"; else echo "SMOKE TEST FAILED"; fi
exit $rc
