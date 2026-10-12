namespace WadSmith.Core.Graphics;

/// <summary>
/// A picture in 8-bit RGBA, not premultiplied, rows top to bottom. The offsets are Doom's: how far
/// the picture's origin sits from its left edge and its top edge (a sprite's feet, a weapon's
/// position on screen).
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A picture needs a width and a height.");
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>R, G, B, A for each pixel.</summary>
    public byte[] Pixels { get; }

    public int OffsetX { get; set; }

    public int OffsetY { get; set; }

    public void Set(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        var i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    public (byte R, byte G, byte B, byte A) Get(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public bool HasTransparency()
    {
        for (var i = 3; i < Pixels.Length; i += 4)
        {
            if (Pixels[i] != 255) return true;
        }

        return false;
    }
}

/// <summary>
/// A picture as palette indices, the way Doom stores patches and flats. <see cref="Opaque"/> marks
/// which pixels are drawn (a patch's gaps are transparent; a flat has none).
/// </summary>
public sealed class PalettedImage
{
    public PalettedImage(int width, int height)
    {
        Width = width;
        Height = height;
        Indices = new byte[width * height];
        Opaque = new bool[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Indices { get; }

    public bool[] Opaque { get; }

    public int OffsetX { get; set; }

    public int OffsetY { get; set; }

    public RgbaImage ToRgba(Palette palette)
    {
        var img = new RgbaImage(Width, Height) { OffsetX = OffsetX, OffsetY = OffsetY };
        for (var i = 0; i < Indices.Length; i++)
        {
            if (!Opaque[i]) continue;
            var (r, g, b) = palette[Indices[i]];
            var o = i * 4;
            img.Pixels[o] = r;
            img.Pixels[o + 1] = g;
            img.Pixels[o + 2] = b;
            img.Pixels[o + 3] = 255;
        }

        return img;
    }
}

/// <summary>A game's 256-colour palette (the first one in PLAYPAL).</summary>
public sealed class Palette
{
    private readonly byte[] _rgb;
    private readonly Dictionary<int, byte> _nearest = [];
    private readonly Lock _gate = new();

    public Palette(ReadOnlySpan<byte> rgb768)
    {
        if (rgb768.Length < 768) throw new CorruptDataException("A palette needs 768 bytes (256 colours of red, green and blue).");
        _rgb = rgb768[..768].ToArray();
    }

    public (byte R, byte G, byte B) this[int index] => (_rgb[index * 3], _rgb[index * 3 + 1], _rgb[index * 3 + 2]);

    /// <summary>A grey ramp, for files that bring no palette of their own.</summary>
    public static Palette Greyscale()
    {
        var rgb = new byte[768];
        for (var i = 0; i < 256; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)i;
        return new Palette(rgb);
    }

    /// <summary>The palette entry closest to a colour (by weighted distance, red and blue counted less than green).</summary>
    public byte Nearest(byte r, byte g, byte b)
    {
        var key = (r << 16) | (g << 8) | b;
        lock (_gate)
        {
            if (_nearest.TryGetValue(key, out var hit)) return hit;
        }

        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < 256; i++)
        {
            int dr = _rgb[i * 3] - r, dg = _rgb[i * 3 + 1] - g, db = _rgb[i * 3 + 2] - b;
            var d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
                if (d == 0) break;
            }
        }

        lock (_gate)
        {
            _nearest[key] = (byte)best;
        }

        return (byte)best;
    }
}
