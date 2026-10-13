using System.Buffers.Binary;
using System.Text;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Quake;

/// <summary>A wall texture of Quake's: its own name, the name it goes by among textures here, its size, and its pixels as palette indexes.</summary>
public sealed record QuakeTexture(string Original, string Name, int Width, int Height, byte[] Indexes);

/// <summary>
/// Quake's wall textures (Phase 8b.6): every texture of every level in the PAK files (each level's BSP keeps the textures it
/// uses, version 29: lump 2 holds a count, offsets, and for each texture a 16-byte name, its size and four mip levels, the
/// first full size), drawn through gfx/palette.lmp. Each goes by a name of at most eight letters, as GZDoom shortens a
/// picture's name to: Q and its own name when that fits, else Q, its first four letters and three of a hash of the whole
/// name, the same on every computer. Skies and liquids (their own engine effects) are left out.
/// </summary>
public sealed class QuakeTextures
{
    private QuakeTextures(byte[] palette, IReadOnlyList<QuakeTexture> textures)
    {
        PaletteRgb = palette;
        Textures = textures;
    }

    public byte[] PaletteRgb { get; }

    public IReadOnlyList<QuakeTexture> Textures { get; }

    /// <summary>Reads the textures of the PAK files given, the first one holding gfx/palette.lmp; a texture in several levels counts once.</summary>
    public static QuakeTextures Read(IReadOnlyList<PakArchive> paks)
    {
        var palette = paks.Select(p => p.Read("gfx/palette.lmp")).FirstOrDefault(p => p is { Length: >= 768 }) ?? throw new CorruptDataException("Quake's palette (gfx/palette.lmp) was not found in its PAK files.");
        var found = new Dictionary<string, QuakeTexture>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pak in paks)
        {
            foreach (var level in pak.Names.Where(n => n.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (pak.Read(level) is not { } bsp) continue;
                foreach (var (original, width, height, indexes) in Miptex(bsp))
                {
                    if (found.ContainsKey(original) || original.StartsWith('*') || original.StartsWith("sky", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = ShortName(original);
                    for (var n = 2; !taken.Add(name); n++) name = ShortName(original + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    found[original] = new QuakeTexture(original, name, width, height, indexes);
                }
            }
        }

        return new QuakeTextures(palette[..768], [.. found.Values.OrderBy(t => t.Original, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The textures inside a level (BSP version 29), skipping any whose pixels are not in the file.</summary>
    public static IEnumerable<(string Name, int Width, int Height, byte[] Indexes)> Miptex(byte[] bsp)
    {
        if (bsp.Length < 4 + 15 * 8 || BinaryPrimitives.ReadInt32LittleEndian(bsp) != 29) yield break;
        var at = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(4 + 2 * 8));
        var size = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(4 + 2 * 8 + 4));
        if (at <= 0 || size < 4 || at + (long)size > bsp.Length) yield break;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(at));
        for (var i = 0; i < count && 4 + i * 4 < size; i++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(at + 4 + i * 4));
            if (offset < 0) continue;
            var start = at + offset;
            if (start + 40 > bsp.Length) continue;
            var name = Encoding.ASCII.GetString(bsp, start, 16).Split('\0')[0];
            var width = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(start + 16));
            var height = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(start + 20));
            var mip0 = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(start + 24));
            if (name.Length == 0 || width is <= 0 or > 1024 || height is <= 0 or > 1024 || mip0 <= 0 || start + mip0 + (long)width * height > bsp.Length) continue;
            yield return (name, width, height, bsp.AsSpan(start + mip0, width * height).ToArray());
        }
    }

    /// <summary>The name a Quake texture goes by here: Q and its own name (letters, digits and _) when that is at most eight letters, else Q, its first four and three of a hash.</summary>
    public static string ShortName(string original)
    {
        var clean = new string([.. original.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        if (clean.Length <= 7) return "Q" + clean;
        var hash = 2166136261u;
        foreach (var c in original.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }

        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        Span<char> tail = stackalloc char[3];
        for (var i = 0; i < 3; i++)
        {
            tail[i] = digits[(int)(hash % 36)];
            hash /= 36;
        }

        return "Q" + clean[..4] + new string(tail);
    }

    /// <summary>A texture as a picture through Quake's palette.</summary>
    public RgbaImage Picture(QuakeTexture texture)
    {
        var image = new RgbaImage(texture.Width, texture.Height);
        for (var i = 0; i < texture.Indexes.Length; i++)
        {
            var index = texture.Indexes[i];
            image.Pixels[i * 4] = PaletteRgb[index * 3];
            image.Pixels[i * 4 + 1] = PaletteRgb[index * 3 + 1];
            image.Pixels[i * 4 + 2] = PaletteRgb[index * 3 + 2];
            image.Pixels[i * 4 + 3] = 255;
        }

        return image;
    }
}
