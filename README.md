<p align="center">
  <img src="assets/branding/banner.png" alt="WadSmith" width="100%">
</p>

# WadSmith

A modern map editor for Doom, Doom II, Heretic, Hexen and Strife, built for UZDoom and GZDoom, on Linux and Windows.

WadSmith is meant to be the editor someone who has only ever *played* these games can open and use:
plain words instead of jargon, drag and drop for everything, doors and lifts the editor understands,
and one key to play what you just built. Experienced mappers get the same tools with the teaching
switched off.

> **Status:** 0.1.0, for Linux and Windows. It is early: expect rough edges, and say what you find in [Issues](https://github.com/codingncaffeine/WadSmith/issues).

![WadSmith's start screen with Freedoom: your games on the left, and for the chosen one the ways to start a map and your maps](assets/screenshots/start.png)

## What it does

- **Remix or start fresh.** Pick any map of a game you own and make it yours, or start a new one; your game files are never changed.
- **Draw rooms, not lines.** Rooms of any shape, with symmetry, rounded corners and smart guides; drag a room and its walls, textures and monsters move with it, and the rooms beside it stay sealed.
- **Plan and 3D side by side.** A 3D view matched to GZDoom's own picture, where you can drop textures and objects and move floors and ceilings; Tab swaps to it alone.
- **Smart doors, lifts, stairs, windows, switches, secrets, keys, teleporters, exits, lights, platforms and water** made with a click, still editable afterwards, and recognised in maps made elsewhere.
- **Every game in its own terms.** Doors that slide aside or swing as Hexen's do, Strife's sliding doors, each game's own door pictures and keys, Hexen's heroes, and hubs whose maps keep their state.
- **Play in one key**, or from wherever the 3D view stands, after a check that offers to fix what would break the game.
- **Plain-language problems** with a fix button, the way from start to exit step by step, and a meter that says whether the ammunition is enough for the monsters.
- **Learn as you go:** a guided first map, hands-on Guide chapters, "What is this?" for anything you point at, examples from the games' own maps, and presets such as Low, Normal, Tall and Huge in place of numbers. Veterans switch the teaching off and get classic names and Doom Builder keys.
- **Free community assets on the shelves.** Pick Realm667 on the Objects shelf for over a thousand free monsters, weapons, items and props, or the community's texture packs on the Textures shelf; drag one in and it is downloaded and copied into your project with its credits.
- **Export for any port.** One file for GZDoom, or a WAD for vanilla, limit-removing, Boom or MBF21 ports with a list of exactly what the map loses, and Play in Chocolate, Crispy or dsda-doom.
- **Never lose work.** Every change is kept as you go, with versions to go back to.

Coming next: travel back and forth between the maps of a hub, ambushes and sequences without scripting, and a share bundle.

## Install

- **Arch Linux:** `wadsmith-bin` from the AUR.
- **Debian and Ubuntu:** the `.deb` from the [latest release](https://github.com/codingncaffeine/WadSmith/releases/latest), then `sudo apt install ./wadsmith_*_amd64.deb`.
- **Any Linux:** the tarball from the latest release, unpacked anywhere; run `./wadsmith`.
- **Windows:** the `win-x64` zip from the latest release, unpacked anywhere; run `wadsmith.exe`.

WadSmith needs [UZDoom](https://github.com/UZDoom/UZDoom) (or GZDoom, the engine it continues) to play maps, and SDL2 (optional) plays an object's sound when you point at it. It finds UZDoom however it was installed: the Flatpak (`org.zdoom.UZDoom`), the AppImage, the AUR's `uzdoom-bin`, or on Windows the zip, winget (`UZDoom.UZDoom`) or Scoop; **Preferences → Engine** picks another or points at one anywhere.

## Getting started

1. Start WadSmith. It finds the games you have installed; with none, it offers to fetch Freedoom.
2. On a game, choose **Remix a map** to start from one of its maps, **New map** for one lit room, or **Guided first map** for ten minutes of steps beside you.
3. Draw with the **Room** tool on the plan, drop textures and monsters from the **Textures** and **Objects** shelves, and watch it all in the 3D view.
4. Press **F9** to play it in UZDoom (or GZDoom). Everything is kept as you go.

The [wiki](https://github.com/codingncaffeine/WadSmith/wiki) covers every tool, shortcut and setting.

## Requirements

- Linux (X11 or Wayland) or Windows 10/11 (64-bit).
- [UZDoom](https://github.com/UZDoom/UZDoom) or [GZDoom](https://zdoom.org) to play maps.
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

## Credits

The free community assets on the shelves are the work of the people and sites that share them. WadSmith downloads one only
when you ask for it, and keeps its authors' credits, its page and its permissions in your project's `credits` folder; the
Share check on Export lists them.

- [Realm667](https://www.realm667.com) - the community's repository of monsters, weapons, items, props and textures, each
  by its own authors.
- [The idgames archive](https://www.doomworld.com/idgames/), reached through [Doomworld](https://www.doomworld.com)'s public
  API and downloaded from its mirrors at [Michigan Tech's Linux Users Group](https://mirrors.lug.mtu.edu/idgames/),
  [youfailit.net](https://youfailit.net/pub/idgames/) and [infania.net](https://ftpmirror1.infania.net/pub/idgames/) - the
  home of the texture packs.
- [The Doom Wiki](https://doomwiki.org) - its list of the community's texture packs, such as OTEX, cc4-tex and GothicTX,
  each by its own authors.
- [Freedoom](https://freedoom.github.io) and [Blasphemer](https://github.com/Blasphemer/blasphemer) - free games under the
  BSD licence, which WadSmith can fetch for you.

WadSmith is built with [.NET](https://dotnet.microsoft.com), [Avalonia](https://avaloniaui.net),
[SkiaSharp](https://github.com/mono/SkiaSharp) and the [MVVM Toolkit](https://github.com/CommunityToolkit/dotnet), plays
sounds through [SDL2](https://www.libsdl.org), and plays maps in [UZDoom](https://github.com/UZDoom/UZDoom) and [GZDoom](https://zdoom.org).

## License

WadSmith is free software under the GNU General Public License, version 3 or later. See [LICENSE](LICENSE).

Doom, Heretic, Hexen and Strife are trademarks of their respective owners. WadSmith is not affiliated
with or endorsed by them.
