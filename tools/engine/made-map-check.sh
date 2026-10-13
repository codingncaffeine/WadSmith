#!/usr/bin/env bash
# Plays maps made by "Make me a map" in GZDoom, each in its own private nested session through play-check.sh, and says
# which started cleanly: one per game family and style, an episode, and any project folders given after the games folder
# (such as one made on the page with other games' things carried in). Run it only after the desktop's KWin maps no
# deleted libraries (log out and in after an update), and never on the desktop itself.
#
#   tools/engine/made-map-check.sh WORK_DIR GAMES_FOLDER [PROJECT_FOLDER ...]
set -u
[ $# -ge 2 ] || { sed -n '2,7p' "$0"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; GAMES="$2"; shift 2
source "$ROOT/scratch/env.sh" >/dev/null 2>&1 || true
CLI=(dotnet run --no-build --project "$ROOT/src/WadSmith.Cli" -c Release --)

# game style seed slot [more generate options]
CASES=(
  "doom2 base grim-keep MAP01"
  "doom2 caves misty-cavern MAP01"
  "doom2 castle molten-foundry MAP01 --size=large --bosses=2"
  "doom2 mixed eerie-works MAP03 --maps=3"
  "heretic mixed haunted-keep E1M1"
  "hexen castle ancient-sanctum MAP01"
  "strife base rusty-depot MAP01"
)
results=()
for c in "${CASES[@]}"; do
  read -r game style seed slot rest <<< "$c"
  project="$WORK/projects/$game-$style-$seed"
  rm -rf "$project"
  # shellcheck disable=SC2086
  "${CLI[@]}" generate "--project=$project" "--game=$game" "--style=$style" "--seed=$seed" $rest > "$WORK/$game-$style-$seed.generate.txt" 2>&1 || { results+=("FAILED TO MAKE $game $style $seed"); continue; }
  if "$HERE/play-check.sh" "$WORK/play-$game-$style-$seed" "$project" "$GAMES" "$slot" > "$WORK/$game-$style-$seed.play.txt" 2>&1; then
    results+=("started   $game $style $seed $slot ($(command grep -c "play-check: log:" "$WORK/$game-$style-$seed.play.txt") log lines about errors or warnings)")
  else
    results+=("NOT CLEAN $game $style $seed $slot (see $WORK/$game-$style-$seed.play.txt)")
  fi
done

for project in "$@"; do
  name="$(basename "$project")"
  slot="$(command grep -o '"slot": *"[A-Z0-9]*"' "$project/wadsmith.json" | head -1 | command grep -o '[A-Z0-9]*"$' | tr -d '"')"
  if "$HERE/play-check.sh" "$WORK/play-$name" "$project" "$GAMES" "${slot:-MAP01}" > "$WORK/$name.play.txt" 2>&1; then
    results+=("started   $name ${slot:-MAP01} ($(command grep -c "play-check: log:" "$WORK/$name.play.txt") log lines about errors or warnings)")
  else
    results+=("NOT CLEAN $name (see $WORK/$name.play.txt)")
  fi
done

printf 'made-map-check: %s\n' "${results[@]}"
! printf '%s\n' "${results[@]}" | command grep -qv '^started'
