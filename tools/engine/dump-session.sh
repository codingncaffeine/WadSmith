#!/usr/bin/env bash
# Session script for nested.sh: for each map, starts GZDoom with the dump add-on, waits for the dump
# to finish (or 60 seconds), then stops the engine. GZDoom gets its own HOME inside the work folder,
# so it never reads or writes the real ~/.config/gzdoom, and runs with no sound or music.
#
# Environment (set by tools/engine/dump-map.sh): WADSMITH_NESTED_WORK, WS_ENGINE, WS_IWAD,
# WS_MAPS (map names, one per line), WS_FILES (extra files, one per line; may be empty).
# Writes WORK/engine-MAP.log per map and WORK/session.txt when done.
set -u
WORK="${WADSMITH_NESTED_WORK:?}"
case "${XDG_CONFIG_HOME:-}" in "$WORK"/*) ;; *) echo "dump-session: XDG_CONFIG_HOME is not private, refusing" > "$WORK/session.txt"; exit 99;; esac
export HOME="$WORK/home"; mkdir -p "$HOME"
while IFS= read -r MAP; do
  [ -n "$MAP" ] || continue
  LOG="$WORK/engine-$MAP.log"; rm -f "$LOG"
  ARGS=(-iwad "$WS_IWAD" -file "$WORK/dump.pk3")
  while IFS= read -r f; do [ -n "$f" ] && ARGS+=(-file "$f"); done <<< "${WS_FILES:-}"
  ARGS+=(+map "$MAP" -nosound -nomusic -config "$HOME/gzdoom.ini" +logfile "$LOG" +queryiwad 0 +vid_fullscreen 0)
  "$WS_ENGINE" "${ARGS[@]}" > "$WORK/engine-stdout-$MAP.txt" 2>&1 &
  PID=$!
  for _ in $(seq 1 120); do
    if [ -f "$LOG" ] && command grep -q "WSDUMP END" "$LOG"; then break; fi
    if ! kill -0 "$PID" 2>/dev/null; then break; fi
    sleep 0.5
  done
  if kill -0 "$PID" 2>/dev/null; then kill -TERM "$PID"; sleep 1; kill -KILL "$PID" 2>/dev/null; fi
  wait "$PID" 2>/dev/null
done <<< "${WS_MAPS:-}"
echo "dump-session: done" > "$WORK/session.txt"
