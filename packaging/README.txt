WadSmith — a map editor for Doom, Doom II, Heretic, Hexen and Strife, made for UZDoom and GZDoom.

Run it
  From the tarball: ./wadsmith
  From the .deb or the AUR package: wadsmith, or WadSmith in your applications menu.
  From the Windows zip: wadsmith.exe in the unpacked folder.

What it needs
  Your own copy of a game (or Freedoom, which WadSmith can fetch for you).
  UZDoom (or GZDoom) to play your maps: on Linux from Flathub (org.zdoom.UZDoom), the AppImage from github.com/UZDoom/UZDoom/releases, or on Arch uzdoom-bin from the AUR; on Windows the zip from the same releases, or winget install UZDoom.UZDoom. Installed somewhere else? Preferences, Engine, Choose.
  SDL2, optional: plays an object's sound when you point at it on the Objects shelf (the Windows zip includes it).

Your game files are only ever read. Your maps live in your own project folders.

Options
  wadsmith --portable DIR   keep settings, logs and the asset library in DIR
  wadsmith --version        print the version and licence
  wadsmith-cli help         the command-line tools: extract a game's pictures, sounds and music, remix a map, and more

WadSmith is free software under the GNU General Public License, version 3 or later (LICENSE).
https://github.com/codingncaffeine/WadSmith
