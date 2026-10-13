## What's New

- **Your WADs are all you need** — WadSmith now looks where GZDoom's own settings look, inside ZIP files (taking a base game out when one is in there), and around any folder you point it at: the folder, the one above it and the ones beside it. A large collection is read once and remembered.
- **Pick a game file** — Point WadSmith straight at DOOM2.WAD, HERETIC.WAD or a ZIP holding one, on the start screen or in Preferences.
- **Your maps, on the game they are for** — Each game's tile has "Your maps": open one, rename it, copy it or delete it (into a Deleted folder, never gone). Maps whose game is not found right now are listed under the tiles.
- **The tools say how** — Pick Room and it says both ways to draw: drag a rectangle, or click corner by corner and double-click the last corner to finish; the Door and Window tools say what to click. While the Guide is open, the card shows the same line.
- **Which base game would you like your map to use?** — The start screen asks the question a first map starts with, over your games.

## What's Fixed

- **A door between two rooms is a door in a wall** — On an opening wider than a door, the Door tool now makes a standard door in the middle and closes the rest with wall either side; a dragged stretch is the door, with wall beside it. "Wall it off" on an opening closes it entirely. A first map used to end with a door standing alone in a gap.
- **Wall above the door** — In a room taller than the door's picture, the picture used to show again above the door (it hangs from the door's top edge and repeats upward). The Door tool now builds a doorway as tall as the door: a short frame either side of it with the wall above, as Doom's own doors are built. Doors made before this stay as they are; make one again to get the frame.
- **The exit switch is a switch, not a wall of them** — "Make it the exit switch" now takes a piece of the wall as wide as the switch picture, where you clicked the wall (its middle when it was selected another way), and the rest stays wall; the Guide's exit step says so. The whole wall used to be tiled with exit switches.
- **Where two rooms meet is an opening, and it says so** — Rooms drawn against each other open into each other; the plan now draws that line dashed, the tutorial explains it as it happens, "What is this?" and the Inspector name it an opening and offer "Make it a door" and "Make it a window" right there, and drawing a shape along walls already there is answered with how rooms and doors work.
- **Back in the Guide** — A Back button returns to the step before, to read it again.
- **A room drawn against a slanted wall shares it** — A corner that landed a hair off an existing wall used to leave two walls lying along each other, so no door could go between the rooms. Now the corner goes onto the wall and the rooms share that stretch.
- **The guided first map checks the rooms touch** — Its room step waits for a room that shares a wall with the first and says what to do when the new room does not; what WadSmith refuses shows on the Guide's card, not only in the status bar.
- **The Guide's buttons stay readable** — With a long game name the button row ran off the card; it wraps now.
- **Door and Window tools say "click on a wall"** — A click that lands on nothing no longer does nothing in silence, and the tools reach twice as far for a wall.

- **"No game" no longer means "get Freedoom"** — When a folder you added holds no base game, the start screen says what it did hold (WADs, archives, map files by name) and what a base game is; Freedoom is offered last, not first.
- **Flatpak GZDoom** — Its data file is found, so the Objects shelf has its monsters and items; the games folder Debian uses is looked in as well.
