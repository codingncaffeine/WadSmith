## What's Fixed

- **A project with Hexen's cleric in it loads again** — Bringing in Hexen's cleric (or anything with a picture of the same kind) wrote one of its pictures under a name WadSmith then could not read back, and the whole project's game failed to load: the 3D view showed everything pink, the shelves were empty, and the map's settings lost the game's own sky. Such pictures are written the way GZDoom expects now, and a project that already has one is put right when it opens.
- **Made maps with outdoor areas always have a sky** — A map from "Make me a map" with rooms open to the sky now names the sky its game shows there, so it never comes out without one.
