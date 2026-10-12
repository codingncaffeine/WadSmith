# Golden images

Reference pictures the tests compare WadSmith's output with, pixel for pixel.

- `freedoom1-E1M1-512.png` is `wadsmith-cli render2d freedoom1.wad --map=E1M1 --size=512`. The map
  belongs to Freedoom (https://freedoom.github.io), distributed under the BSD 3-Clause licence:

  Copyright © 2001-2024 Contributors to the Freedoom project. All rights reserved.
  Redistribution and use in source and binary forms, with or without modification, are permitted
  provided that the following conditions are met: redistributions of source code must retain the
  above copyright notice, this list of conditions and the following disclaimer; redistributions in
  binary form must reproduce the above copyright notice, this list of conditions and the following
  disclaimer in the documentation and/or other materials provided with the distribution; neither
  the name of the Freedoom project nor the names of its contributors may be used to endorse or
  promote products derived from this software without specific prior written permission.
  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
  IMPLIED WARRANTIES ARE DISCLAIMED.

- `freedoom2-MAP01-3d-overview.png` and `freedoom2-MAP01-3d-start.png` are the 3D view's pictures of Freedoom 2's
  MAP01 (same licence as above), 320 by 200: the whole map from above, and the player start at eye height. They were
  drawn with Mesa's software renderer (as CI draws them); other GPUs differ slightly at edges, so the test compares
  on average (measured 0.15–0.29 per channel between NVIDIA and Mesa; the limit is 1.5).

To regenerate after an intended change: the 2D one with the command above; the 3D ones with
`WADSMITH_WRITE_GOLDENS=1 __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/50_mesa.json LIBGL_ALWAYS_SOFTWARE=1 dotnet test tests/WadSmith.Tests --filter Freedoom2Map01LooksAsItDid`.
Pictures of commercial games are never stored here.
