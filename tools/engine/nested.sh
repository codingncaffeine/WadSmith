#!/usr/bin/env bash
# Runs a session script inside a private, virtual KWin with Xwayland, so nothing it starts appears on
# the desktop or reaches the desktop's D-Bus.
#
#   tools/engine/nested.sh WORK_DIR SECONDS SESSION_SCRIPT
#
# Isolation: a private session bus that activates no services (isolated-bus.conf); every XDG
# directory, the runtime one included, inside WORK_DIR or a private /tmp folder; the compositor
# refuses to start otherwise. After the run, a leak check lists any KDE config file changed in the
# real home (anything listed is a leak). KWin's own output goes to WORK_DIR/kwin.txt.
# KWin splits --exit-with-session on spaces, so the session script is reached through a space-free link.
set -u
[ $# -eq 3 ] || { sed -n '2,11p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; SECS="$2"; SESSION_SRC="$(cd "$(dirname "$3")" && pwd)/$(basename "$3")"
[ -x "$SESSION_SRC" ] || { echo "nested: $SESSION_SRC is not an executable script"; exit 2; }
command -v kwin_wayland >/dev/null 2>&1 || { echo "nested: kwin_wayland not found"; exit 2; }
command -v dbus-run-session >/dev/null 2>&1 || { echo "nested: dbus-run-session not found"; exit 2; }

LINK="/tmp/wadsmith-$(id -u)-session-$$"
ln -sfn "$SESSION_SRC" "$LINK"
K="$WORK/kwin"
rm -rf "$K"; mkdir -p "$K/config" "$K/state" "$K/data" "$K/cache"
RT="/tmp/wadsmith-$(id -u)-kwrt-$$"; mkdir -p "$RT"; chmod 700 "$RT"
printf '[Wayland]\nEnablePrimarySelection=false\n' > "$K/config/kwinrc"

export WADSMITH_NESTED_WORK="$WORK"
export XDG_CONFIG_HOME="$K/config" XDG_STATE_HOME="$K/state" XDG_DATA_HOME="$K/data" XDG_CACHE_HOME="$K/cache" XDG_RUNTIME_DIR="$RT"
unset DISPLAY WAYLAND_DISPLAY JOURNAL_STREAM
for v in XDG_CONFIG_HOME XDG_STATE_HOME XDG_DATA_HOME XDG_CACHE_HOME; do
  case "${!v}" in "$K"/*) ;; *) echo "isolation guard: $v=${!v} is outside $K, refusing"; exit 99;; esac
done
case "$XDG_RUNTIME_DIR" in /tmp/wadsmith-*-kwrt-*) ;; *) echo "isolation guard: runtime dir not private, refusing"; exit 99;; esac

START=$(date '+%Y-%m-%d %H:%M:%S')
timeout -k 5 -s TERM "$SECS" dbus-run-session --config-file="$HERE/isolated-bus.conf" -- kwin_wayland --virtual --xwayland --no-lockscreen --no-global-shortcuts --socket "wayland-wadsmith-$$" --width 1280 --height 800 --exit-with-session "$LINK" > "$K/kwin.txt" 2>&1
STATUS=$?
rm -f "$LINK"
if [ -d "$RT/doc" ]; then fusermount3 -u "$RT/doc" 2>/dev/null; fi
rm -rf "$RT"
echo "nested: compositor exit $STATUS"
echo "--- real-config leak check (anything listed is a leak) ---"
find ~/.config ~/.local/state -maxdepth 2 -newermt "$START" \( -name 'kwin*' -o -name 'kglobal*' -o -name 'kdeglobals' \) 2>/dev/null
find ~/.config/gzdoom ~/.local/share/gzdoom ~/.cache/gzdoom ~/.config/uzdoom ~/.local/share/games/uzdoom ~/.cache/uzdoom -newermt "$START" 2>/dev/null
echo "--- end leak check ---"
exit $STATUS
