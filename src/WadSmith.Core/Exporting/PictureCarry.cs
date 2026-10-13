using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Exporting;

/// <summary>
/// The base game an export is played over (its IWAD, and an official add-on over it): the pictures it has, for the
/// checks, and its palette, patch names and texture lists, for carrying the project's own pictures into a classic WAD.
/// </summary>
public sealed class BaseGame
{
    private readonly Lazy<TextureLibrary> _library;

    public BaseGame(ResourceStack stack)
    {
        Stack = stack;
        _library = new(() => new TextureLibrary(stack));
        Pictures = new PictureNames(n => _library.Value.FindWall(n) is not null, n => _library.Value.FindFlat(n) is not null);
    }

    public ResourceStack Stack { get; }

    /// <summary>The walls and floors the game has.</summary>
    public PictureNames Pictures { get; }

    /// <summary>The game's palette (PLAYPAL's first), or null when it has none.</summary>
    public Palette? Palette => Stack.Find("PLAYPAL") is { } p ? new Palette(p.Read().AsSpan(0, 768)) : null;

    /// <summary>A lump of the game by name (the last one loaded), or null.</summary>
    public byte[]? Lump(string name) => Stack.Find(name)?.Read();

    /// <summary>Whether any lump of the game has this name, in any namespace.</summary>
    public bool HasName(string name) => Stack.Containers.Any(c => c.Entries.Any(e => LumpName.Comparer.Equals(e.Name, name)));
}

/// <summary>What carrying added to a classic WAD: its lumps, the pictures now there, and the notes.</summary>
public sealed record CarriedPictures(IReadOnlyList<(string Name, byte[] Data)> Lumps, IReadOnlySet<string> Walls, IReadOnlySet<string> Flats, IReadOnlyList<string> Notes);

/// <summary>
/// The project's own pictures carried into a classic WAD (Phase 12): each wall picture the base game lacks becomes a patch in
/// the game's palette and a texture of that one patch, written into the game's own PNAMES and TEXTURE1 (a WAD's TEXTURE1
/// replaces the game's in every port, so the game's textures are kept in it, in their order); each floor picture becomes a
/// 64×64 flat between FF_START and FF_END, for the ports that read floors from a WAD (not vanilla).
/// </summary>
public static class PictureCarry
{
    private const int FlatSide = 64;

    public static CarriedPictures Carry(BaseGame game, ResourceContainer project, IEnumerable<string> walls, IEnumerable<string> flats, bool flatsToo)
    {
        var none = new CarriedPictures([], new HashSet<string>(), new HashSet<string>(), []);
        var wallsWanted = walls.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var flatsWanted = flatsToo ? flats.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        if (wallsWanted.Count == 0 && flatsWanted.Count == 0) return none;
        if (game.Palette is not { } palette) return none;
        var library = new TextureLibrary(new ResourceStack([.. game.Stack.Containers, project]));
        var lumps = new List<(string, byte[])>();
        var notes = new List<string>();
        var carriedWalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var carriedFlats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Walls: the game's PNAMES and TEXTURE1 with the new patches and textures after them.
        if (wallsWanted.Count > 0 && game.Lump("PNAMES") is { } pnamesData && game.Lump("TEXTURE1") is { } texture1Data)
        {
            var pnames = TextureLump.ReadPatchNames(pnamesData).ToList();
            var texture1 = TextureLump.Read(texture1Data, pnames, "TEXTURE1");
            var taken = new HashSet<string>(pnames.Select(n => n.Text), StringComparer.OrdinalIgnoreCase);
            var patches = new List<(string, byte[])>();
            var counter = 0;
            foreach (var name in wallsWanted)
            {
                if (library.GetWall(name) is not { } picture) continue;
                var patch = name;
                while (taken.Contains(patch) || game.HasName(patch)) patch = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"WSPAT{++counter:000}");
                taken.Add(patch);
                pnames.Add(NameField.FromText(patch));
                var texture = new CompositeTexture { Name = name.ToUpperInvariant(), Width = picture.Width, Height = picture.Height, Origin = "WadSmith" };
                texture.Patches.Add(new PatchPlacement(patch, 0, 0) { PatchIndex = (short)(pnames.Count - 1) });
                texture1.Textures.Add(texture);
                patches.Add((patch, DoomPicture.Encode(Quantize(picture, palette))));
                carriedWalls.Add(name);
            }

            if (carriedWalls.Count > 0)
            {
                lumps.Add(("PNAMES", TextureLump.WritePatchNames(pnames)));
                lumps.Add(("TEXTURE1", texture1.Write()));
                lumps.AddRange(patches);
            }
        }

        // Floors: 64×64 flats between FF_START and FF_END.
        var flatLumps = new List<(string, byte[])>();
        var resized = 0;
        foreach (var name in flatsWanted)
        {
            if (library.GetFlat(name) is not { } picture) continue;
            if (picture.Width != FlatSide || picture.Height != FlatSide)
            {
                picture = Resize(picture, FlatSide, FlatSide);
                resized++;
            }

            flatLumps.Add((name.ToUpperInvariant(), RawPicture.EncodeFlat(Quantize(picture, palette))));
            carriedFlats.Add(name);
        }

        if (flatLumps.Count > 0)
        {
            lumps.Add(("FF_START", []));
            lumps.AddRange(flatLumps);
            lumps.Add(("FF_END", []));
        }

        if (carriedWalls.Count + carriedFlats.Count > 0)
        {
            notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{carriedWalls.Count} wall and {carriedFlats.Count} floor pictures carried in, in the game's own colours."));
        }

        if (resized > 0) notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{resized} floor pictures resized to 64×64, the only size a classic floor has."));
        return new CarriedPictures(lumps, carriedWalls, carriedFlats, notes);
    }

    /// <summary>A picture in the game's palette: each pixel the nearest colour, mostly see-through pixels left out.</summary>
    public static PalettedImage Quantize(RgbaImage picture, Palette palette)
    {
        var p = new PalettedImage(picture.Width, picture.Height) { OffsetX = picture.OffsetX, OffsetY = picture.OffsetY };
        var cache = new Dictionary<int, byte>();
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                var (r, g, b, a) = picture.Get(x, y);
                var i = y * picture.Width + x;
                if (a < 128) continue;
                var key = (r << 16) | (g << 8) | b;
                if (!cache.TryGetValue(key, out var index)) cache[key] = index = palette.Nearest(r, g, b);
                p.Indices[i] = index;
                p.Opaque[i] = true;
            }
        }

        return p;
    }

    private static RgbaImage Resize(RgbaImage from, int width, int height)
    {
        var to = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = from.Get(x * from.Width / width, y * from.Height / height);
                to.Set(x, y, r, g, b, a);
            }
        }

        return to;
    }
}
