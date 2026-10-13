using System.Text.RegularExpressions;

namespace WadSmith.Core.Models;

/// <summary>
/// Block texture sets, as voxel-game packs ship them: a block's faces as pictures named <c>NAME_top</c>, <c>NAME_side</c>,
/// <c>NAME_bottom</c>, and where a face differs <c>NAME_front</c>, <c>_back</c>, <c>_left</c>, <c>_right</c>. Any one picture of
/// a set makes a cube a metre on a side, its front toward Doom's forward: a side face uses its own picture or the side's,
/// the top the top's or the side's, the bottom the bottom's, the top's or the side's.
/// </summary>
public static partial class BlockSet
{
    private static readonly string[] Faces = ["top", "side", "bottom", "front", "back", "left", "right"];

    /// <summary>The set's name and folder when <paramref name="path"/> is a picture of a block set with a side or a front and a top; null otherwise.</summary>
    public static (string Name, string Folder)? Of(string path)
    {
        var m = FaceName().Match(Path.GetFileName(path));
        if (!m.Success) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var name = m.Groups[1].Value;
        var faces = Faces.Where(f => Picture(folder, name, f) is not null).ToHashSet();
        return faces.Contains("top") && (faces.Contains("side") || faces.Contains("front")) ? (name, folder) : null;
    }

    public static Mesh Read(string path, ICollection<string>? notes = null)
    {
        var (name, folder) = Of(path) ?? throw new CorruptDataException($"{Path.GetFileName(path)} is not a picture of a block set (NAME_top and NAME_side or NAME_front beside it).");
        var pictures = new Dictionary<string, Graphics.RgbaImage?>();
        Graphics.RgbaImage? Load(string face) => pictures.TryGetValue(face, out var known) ? known : pictures[face] = Picture(folder, name, face) is { } file ? ObjModel.LoadPicture(file, notes) : null;
        Graphics.RgbaImage? Side(string face) => Load(face) ?? Load("side") ?? Load("front");

        // A metre cube standing on its base, centred: corners in model axes (forward, left, up).
        var surfaces = new List<MeshSurface>();
        void Face(string face, Graphics.RgbaImage? picture, (float, float, float) bottomLeft, (float, float, float) bottomRight, (float, float, float) topRight, (float, float, float) topLeft)
        {
            MeshVertex V((float X, float Y, float Z) p, float u, float v) => new(p.X, p.Y, p.Z, u, v);
            surfaces.Add(new MeshSurface(face, [V(bottomLeft, 0, 1), V(bottomRight, 1, 1), V(topRight, 1, 0), V(bottomLeft, 0, 1), V(topRight, 1, 0), V(topLeft, 0, 0)], null, picture));
        }

        const float h = 0.5f;
        // Each side seen from outside: the picture's right is the viewer's right (looking along d with z up, d × z), its top up.
        Face("front", Side("front"), (h, -h, 0), (h, h, 0), (h, h, 1), (h, -h, 1));
        Face("left", Side("left"), (h, h, 0), (-h, h, 0), (-h, h, 1), (h, h, 1));
        Face("back", Side("back"), (-h, h, 0), (-h, -h, 0), (-h, -h, 1), (-h, h, 1));
        Face("right", Side("right"), (-h, -h, 0), (h, -h, 0), (h, -h, 1), (-h, -h, 1));
        // From above, the picture's top toward the front.
        Face("top", Load("top") ?? Side("side"), (-h, h, 1), (-h, -h, 1), (h, -h, 1), (h, h, 1));
        Face("bottom", Load("bottom") ?? Load("top") ?? Side("side"), (-h, -h, 0), (-h, h, 0), (h, h, 0), (h, -h, 0));
        return new Mesh(surfaces);
    }

    /// <summary>The set's name, for the prop's title: "jack_o_lantern_side.png" → "jack_o_lantern".</summary>
    public static string NameOf(string path) => FaceName().Match(Path.GetFileName(path)) is { Success: true } m ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(path);

    private static string? Picture(string folder, string name, string face)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp" })
        {
            if (ObjModel.Find(folder, $"{name}_{face}{ext}") is { } file) return file;
        }

        return null;
    }

    [GeneratedRegex(@"^(.+)_(top|side|bottom|front|back|left|right)\.(png|jpe?g|tga|bmp|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex FaceName();
}
