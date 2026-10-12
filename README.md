<p align="center">
  <img src="assets/branding/banner.jpg" alt="WadSmith" width="100%">
</p>

# WadSmith

A modern, Linux-native map editor for Doom, Doom II, Heretic, Hexen and Strife, built for GZDoom.

WadSmith is meant to be the editor someone who has only ever *played* these games can open and use:
plain words instead of jargon, drag and drop for everything, doors and lifts the editor understands,
and one key to play what you just built. Experienced mappers get the same tools with the teaching
switched off.

> **Status:** early development. There is nothing to download yet.

## What it will do

- **Draw rooms, not lines.** Drag a room and its walls, textures and monsters move with it; neighbours stay sealed.
- **Smart doors, lifts, stairs and teleporters** that stay editable after you place them, and that WadSmith also recognises in maps made elsewhere.
- **Drop anything on a wall:** images, animated GIFs, video clips and text become textures, sized to fit.
- **Play in one key**, or from wherever the 3D camera stands, in GZDoom.
- **Plain-language problems** with a fix button: an open room, a passage too narrow, a key locked behind its own door.
- **An asset library** built from the games you own, so every texture, sprite and sound is a file you can browse and reuse.

## Requirements

- Linux (X11 or Wayland). A Windows build will follow.
- [GZDoom](https://zdoom.org) to play maps.
- The game data from your own copy of Doom, Doom II, Heretic, Hexen or Strife, or the free
  [Freedoom](https://freedoom.github.io). WadSmith ships no game data and never changes your game files.

## Building from source

Requires the .NET 10 SDK.

```sh
git clone https://github.com/codingncaffeine/WadSmith.git
cd WadSmith
dotnet build WadSmith.slnx -c Release
dotnet test tests/WadSmith.Tests -c Release
```

## License

WadSmith is free software under the GNU General Public License, version 3 or later. See [LICENSE](LICENSE).

Doom, Heretic, Hexen and Strife are trademarks of their respective owners. WadSmith is not affiliated
with or endorsed by them.
