using System.Text.RegularExpressions;

namespace WadSmith.Core.Graphics;

/// <summary>A layer of a PBR material set, by GZDoom's auto-material folder it goes to (empty for the colour itself).</summary>
public enum PbrLayer
{
    Colour,
    Normal,
    Roughness,
    Metallic,
    Occlusion,
    Specular,
}

/// <summary>
/// PBR material sets (D42): a colour picture with its normal, roughness, metal, occlusion and specular maps, named as the
/// free libraries name them — <c>Bricks076_1K-PNG_Color</c> and <c>_NormalGL</c> (ambientCG), <c>brick_wall_001_diff_1k</c>
/// and <c>_nor_gl_1k</c>, <c>_rough_1k</c>, <c>_arm_1k</c> (Poly Haven; ARM packs occlusion, roughness and metal in red,
/// green and blue). GZDoom finds a texture's layers by name in its <c>materials/…</c> folders, and reads normal maps with
/// green up (OpenGL's way, its shader's WITH_NORMALMAP_GREEN_UP): a DirectX normal map has its green turned over.
/// </summary>
public static partial class PbrSet
{
    // The words each layer goes by, longest first so "basecolor" wins over "color".
    private static readonly (string Word, PbrLayer Layer, bool DirectX)[] Words =
    [
        ("ambientocclusion", PbrLayer.Occlusion, false), ("basecolor", PbrLayer.Colour, false), ("base_color", PbrLayer.Colour, false), ("normal_opengl", PbrLayer.Normal, false),
        ("normal_directx", PbrLayer.Normal, true), ("normalgl", PbrLayer.Normal, false), ("normaldx", PbrLayer.Normal, true), ("roughness", PbrLayer.Roughness, false),
        ("metalness", PbrLayer.Metallic, false), ("metallic", PbrLayer.Metallic, false), ("specular", PbrLayer.Specular, false), ("diffuse", PbrLayer.Colour, false),
        ("albedo", PbrLayer.Colour, false), ("nor_gl", PbrLayer.Normal, false), ("nor_dx", PbrLayer.Normal, true), ("normal", PbrLayer.Normal, false),
        ("colour", PbrLayer.Colour, false), ("color", PbrLayer.Colour, false), ("rough", PbrLayer.Roughness, false), ("metal", PbrLayer.Metallic, false),
        ("diff", PbrLayer.Colour, false), ("spec", PbrLayer.Specular, false), ("col", PbrLayer.Colour, false), ("nrm", PbrLayer.Normal, false),
        ("arm", PbrLayer.Occlusion, false), ("ao", PbrLayer.Occlusion, false),
    ];

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp"];

    /// <summary>A set's pictures: its name (the part its files share), and the file of each layer found.</summary>
    public sealed record Found(string Name, IReadOnlyDictionary<PbrLayer, (string File, bool DirectX, bool Packed)> Layers);

    /// <summary>The set <paramref name="path"/> belongs to, when it is a picture of a set that has a colour and at least one other map; null otherwise.</summary>
    public static Found? Of(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || Parse(Path.GetFileNameWithoutExtension(path)) is not { } mine) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var layers = new Dictionary<PbrLayer, (string, bool, bool)>();
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.Ordinal))
        {
            if (Parse(Path.GetFileNameWithoutExtension(file)) is not { } other || !string.Equals(other.Base, mine.Base, StringComparison.OrdinalIgnoreCase) || !string.Equals(other.Tail, mine.Tail, StringComparison.OrdinalIgnoreCase)) continue;
            if (other.Word == "arm")
            {
                // Packed: occlusion, roughness and metal from one picture, unless the set has them on their own.
                foreach (var layer in new[] { PbrLayer.Occlusion, PbrLayer.Roughness, PbrLayer.Metallic }) layers.TryAdd(layer, (file, false, true));
                continue;
            }

            // A GL normal map wins over a DirectX one; a map of its own wins over a packed one.
            if (layers.TryGetValue(other.Layer, out var had) && !had.Item3 && !(had.Item2 && !other.DirectX)) continue;
            layers[other.Layer] = (file, other.DirectX, false);
        }

        return layers.ContainsKey(PbrLayer.Colour) && layers.Count >= 2 ? new Found(Tidy(mine.Base), layers) : null;
    }

    /// <summary>
    /// The set's layers as pictures: the colour as it is, and each map in the form GZDoom reads it (green up; a packed
    /// channel spread to grey).
    /// </summary>
    public static Dictionary<PbrLayer, RgbaImage> Read(Found set)
    {
        var pictures = new Dictionary<PbrLayer, RgbaImage>();
        var decoded = new Dictionary<string, RgbaImage>(StringComparer.Ordinal);
        foreach (var (layer, (file, directX, packed)) in set.Layers)
        {
            if (!decoded.TryGetValue(file, out var image)) decoded[file] = image = Tga.DecodeAny(File.ReadAllBytes(file), Path.GetFileName(file));
            pictures[layer] = packed ? Channel(image, layer switch { PbrLayer.Occlusion => 0, PbrLayer.Roughness => 1, _ => 2 }) : directX ? GreenUp(image) : image;
        }

        return pictures;
    }

    private static RgbaImage Channel(RgbaImage image, int channel)
    {
        var grey = new RgbaImage(image.Width, image.Height);
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            var v = image.Pixels[i * 4 + channel];
            grey.Set(i % image.Width, i / image.Width, v, v, v);
        }

        return grey;
    }

    private static RgbaImage GreenUp(RgbaImage image)
    {
        var turned = new RgbaImage(image.Width, image.Height);
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            var (r, g, b, a) = (image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], image.Pixels[i * 4 + 3]);
            turned.Set(i % image.Width, i / image.Width, r, (byte)(255 - g), b, a);
        }

        return turned;
    }

    /// <summary>The file name split round its layer word: what comes before, the word, and what comes after (a resolution, say).</summary>
    private static (string Base, string Word, PbrLayer Layer, bool DirectX, string Tail)? Parse(string stem)
    {
        foreach (var (word, layer, directX) in Words)
        {
            var m = Regex.Match(stem, $@"^(.+?)[_\-. ]{Regex.Escape(word)}((?:[_\-. ].*)?)$", RegexOptions.IgnoreCase);
            if (m.Success) return (m.Groups[1].Value, word, layer, directX, m.Groups[2].Value);
        }

        return null;
    }

    /// <summary>"Bricks076_1K-PNG" → "Bricks076 1K PNG".</summary>
    private static string Tidy(string name) => Separators().Replace(name, " ").Trim();

    [GeneratedRegex(@"[_\-. ]+")]
    private static partial Regex Separators();
}
