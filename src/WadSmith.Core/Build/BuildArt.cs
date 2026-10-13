using System.Buffers.Binary;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Build;

/// <summary>One Build-engine tile: its number, size, the animation and offset word (picanm), and where its pixels are.</summary>
public sealed record BuildTile(int Number, int Width, int Height, int Picanm, string ArtFile, int Offset)
{
    /// <summary>How far the picture is drawn left of its middle (picanm bits 8–15, signed), as Build centres sprites.</summary>
    public int OffsetX => (sbyte)((Picanm >> 8) & 0xFF);

    /// <summary>How far the picture is drawn up from its bottom (picanm bits 16–23, signed).</summary>
    public int OffsetY => (sbyte)((Picanm >> 16) & 0xFF);

    /// <summary>How many tiles after this one its animation runs over (picanm bits 0–5).</summary>
    public int AnimationFrames => Picanm & 0x3F;
}

/// <summary>
/// Build-engine art (D48): the tiles of TILES000.ART … TILESnnn.ART — each file a header (version 1, tile count, first and
/// last tile number), the widths, heights and picanm words of its tiles, then each tile's pixels column by column — and the
/// game palette, the first 768 bytes of PALETTE.DAT in 6-bit colour. Index 255 is see-through, as Build draws it.
/// </summary>
public sealed class BuildArt
{
    private readonly GrpArchive _grp;
    private readonly Dictionary<int, BuildTile> _tiles = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    private BuildArt(GrpArchive grp, byte[] palette)
    {
        _grp = grp;
        PaletteRgb = palette;
        Palette = new Palette(palette);
    }

    /// <summary>The game palette in 8-bit colour, as a PLAYPAL holds one.</summary>
    public byte[] PaletteRgb { get; }

    public Palette Palette { get; }

    public IReadOnlyCollection<BuildTile> Tiles => _tiles.Values;

    public BuildTile? Tile(int number) => _tiles.GetValueOrDefault(number);

    public static BuildArt Read(GrpArchive grp)
    {
        var dat = grp.Read("PALETTE.DAT") ?? throw new CorruptDataException("The group file has no PALETTE.DAT.");
        if (dat.Length < 768) throw new CorruptDataException("PALETTE.DAT is too short for a palette.");
        var rgb = new byte[768];
        for (var i = 0; i < 768; i++) rgb[i] = (byte)Math.Min(255, dat[i] * 255 / 63);
        var art = new BuildArt(grp, rgb);

        foreach (var name in grp.Names.Where(n => n.StartsWith("TILES", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".ART", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var data = grp.Read(name)!;
            if (data.Length < 16 || BinaryPrimitives.ReadInt32LittleEndian(data) != 1) throw new CorruptDataException($"{name} is not Build art (version 1).");
            int first = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)), last = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            var count = last - first + 1;
            if (count < 0 || 16 + count * 8 > data.Length) throw new CorruptDataException($"{name}'s tile table is cut short.");
            var pixels = 16 + count * 8;
            for (var i = 0; i < count; i++)
            {
                int w = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(16 + i * 2)), h = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(16 + count * 2 + i * 2));
                var picanm = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16 + count * 4 + i * 4));
                if (w > 0 && h > 0)
                {
                    if (pixels + w * h > data.Length) throw new CorruptDataException($"{name}: tile {first + i} runs past the end of the file.");
                    art._tiles[first + i] = new BuildTile(first + i, w, h, picanm, name, pixels);
                    pixels += w * h;
                }
            }
        }

        return art;
    }

    /// <summary>A tile as a picture: its pixels column by column through the palette, index 255 see-through; its offsets as a sprite's.</summary>
    public RgbaImage Picture(BuildTile tile)
    {
        byte[] data;
        lock (_files)
        {
            if (!_files.TryGetValue(tile.ArtFile, out data!)) _files[tile.ArtFile] = data = _grp.Read(tile.ArtFile)!;
        }

        var image = new RgbaImage(tile.Width, tile.Height)
        {
            // Build centres a sprite on its middle and stands it on its bottom row; Doom's offsets count from the top left.
            OffsetX = tile.Width / 2 + tile.OffsetX,
            OffsetY = tile.Height + tile.OffsetY,
        };
        for (var x = 0; x < tile.Width; x++)
        {
            for (var y = 0; y < tile.Height; y++)
            {
                var index = data[tile.Offset + x * tile.Height + y];
                if (index == 255) continue;
                var o = (y * tile.Width + x) * 4;
                image.Pixels[o] = PaletteRgb[index * 3];
                image.Pixels[o + 1] = PaletteRgb[index * 3 + 1];
                image.Pixels[o + 2] = PaletteRgb[index * 3 + 2];
                image.Pixels[o + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>The name a tile goes by among textures: DK and its number ("DK0450").</summary>
    public static string NameOf(int number) => "DK" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
