using System.Globalization;
using System.Text;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Models;

/// <summary>
/// Wavefront OBJ models (D16): vertices, texture corners and faces of any number of sides (fanned into triangles), grouped by
/// material; the materials' pictures come from the <c>.mtl</c> library's <c>map_Kd</c>. GZDoom ignores <c>.mtl</c> files
/// and reads each <c>usemtl</c> name as a picture beside the model, so <see cref="Write"/> names the pictures there.
/// </summary>
public static class ObjModel
{
    /// <summary>Reads the OBJ at <paramref name="path"/> with the pictures its materials name (looked for beside it); a picture that cannot be read is said in <paramref name="notes"/>.</summary>
    public static Mesh Read(string path, ICollection<string>? notes = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var name = Path.GetFileName(path);
        var positions = new List<(float X, float Y, float Z)>();
        var corners = new List<(float U, float V)>();
        var groups = new List<(string Material, List<MeshVertex> Triangles)>();
        var pictures = new Dictionary<string, string>(StringComparer.Ordinal);
        List<MeshVertex>? current = null;
        var line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            var text = raw.Trim();
            if (text.Length == 0 || text[0] == '#') continue;
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    // GZDoom's axes: OBJ's Y is up and its Z is flipped (FOBJModel::RealignVector), then MD3's Z-up order.
                    positions.Add((Number(parts[1]), -Number(parts[3]), Number(parts[2])));
                    break;
                case "vt" when parts.Length >= 2:
                    corners.Add((Number(parts[1]), parts.Length >= 3 ? 1 - Number(parts[2]) : 0));
                    break;
                case "usemtl":
                    var material = parts.Length > 1 ? text[6..].Trim() : "";
                    var at = groups.FindIndex(g => g.Material == material);
                    if (at < 0) groups.Add((material, current = []));
                    else current = groups[at].Triangles;
                    break;
                case "mtllib" when parts.Length > 1:
                    ReadLibrary(Path.Combine(folder, text[6..].Trim()), pictures);
                    break;
                case "f" when parts.Length >= 4:
                    if (current is null) groups.Add(("", current = []));
                    var face = new MeshVertex[parts.Length - 1];
                    for (var i = 1; i < parts.Length; i++) face[i - 1] = Corner(parts[i]);
                    for (var i = 1; i + 1 < face.Length; i++)
                    {
                        current.Add(face[0]);
                        current.Add(face[i]);
                        current.Add(face[i + 1]);
                    }

                    break;
            }
        }

        var surfaces = new List<MeshSurface>();
        foreach (var (material, triangles) in groups.Where(g => g.Triangles.Count > 0))
        {
            var file = pictures.GetValueOrDefault(material) ?? Guess(folder, material);
            // A model with no materials at all (common in downloads) takes the picture named like it, or the only one beside it.
            if (file is null && groups.Count == 1 && material.Length == 0 && pictures.Count == 0 && Beside(folder, Path.GetFileNameWithoutExtension(path)) is { } beside)
            {
                file = beside;
                notes?.Add($"No material names a picture, so {Path.GetFileName(beside)} beside it was used.");
            }

            surfaces.Add(new MeshSurface(material, triangles, file, file is null ? null : LoadPicture(file, notes)));
        }

        if (surfaces.Count == 0) throw new CorruptDataException($"{name} has no faces to show.");
        return new Mesh(surfaces);

        float Number(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : throw new CorruptDataException($"{name}, line {line}: \"{s}\" is not a number.");

        MeshVertex Corner(string spec)
        {
            var ids = spec.Split('/');
            var p = Index(ids[0], positions.Count) ?? throw new CorruptDataException($"{name}, line {line}: a face corner names no vertex.");
            var t = ids.Length > 1 ? Index(ids[1], corners.Count) : null;
            var (x, y, z) = positions[p];
            var (u, v) = t is { } ti ? corners[ti] : (0f, 0f);
            return new MeshVertex(x, y, z, u, v);
        }

        int? Index(string s, int count)
        {
            if (s.Length == 0) return null;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) throw new CorruptDataException($"{name}, line {line}: \"{s}\" is not an index.");
            var index = i < 0 ? count + i : i - 1;
            return index >= 0 && index < count ? index : throw new CorruptDataException($"{name}, line {line}: index {i} is past the {count} listed.");
        }
    }

    /// <summary>Each material's diffuse picture in a <c>.mtl</c> library; options before the file name (<c>-s 1 1 1</c>) are skipped.</summary>
    private static void ReadLibrary(string path, Dictionary<string, string> pictures)
    {
        if (!File.Exists(path)) return;
        var folder = Path.GetDirectoryName(path) ?? ".";
        string? material = null;
        foreach (var raw in File.ReadLines(path))
        {
            var text = raw.Trim();
            if (text.StartsWith("newmtl", StringComparison.Ordinal)) material = text[6..].Trim();
            else if (material is not null && text.StartsWith("map_Kd", StringComparison.OrdinalIgnoreCase))
            {
                var rest = text[6..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                while (rest.Count > 1 && rest[0].StartsWith('-'))
                {
                    var arguments = rest[0] switch { "-s" or "-o" or "-t" => 3, "-mm" => 2, _ => 1 };
                    rest.RemoveRange(0, Math.Min(rest.Count - 1, arguments + 1));
                }

                if (Find(folder, string.Join(' ', rest).Replace('\\', '/')) is { } file) pictures[material] = file;
            }
        }
    }

    /// <summary>A picture named like the material beside the model, when no library names one.</summary>
    internal static string? Guess(string folder, string material)
    {
        if (material.Length == 0) return null;
        foreach (var ext in new[] { "", ".png", ".tga", ".jpg", ".jpeg" })
        {
            if (Find(folder, material + ext) is { } file) return file;
        }

        return null;
    }

    private static readonly string[] PictureExtensions = [".png", ".tga", ".jpg", ".jpeg", ".bmp", ".webp"];

    /// <summary>
    /// The picture for a model that names none: one named like it ("spot.png"), else one whose name starts with its first word
    /// ("spot_texture.png" for "spot_triangulated.obj"), else the only picture in its folder; null when that is not one.
    /// </summary>
    internal static string? Beside(string folder, string stem)
    {
        var all = Directory.EnumerateFiles(folder).Where(f => PictureExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.Ordinal).ToList();
        var word = stem.Split('_', '-', ' ', '.')[0];
        return all.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
            ?? (word.Length > 0 ? all.FirstOrDefault(f => Path.GetFileName(f).StartsWith(word, StringComparison.OrdinalIgnoreCase)) : null)
            ?? (all.Count == 1 ? all[0] : null);
    }

    /// <summary>A file by a relative name, matching letter case loosely (models made on Windows name "Wood.PNG" for "wood.png").</summary>
    public static string? Find(string folder, string relative)
    {
        var path = Path.Combine(folder, relative);
        if (File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory).FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A skin picture, or null with a note when it cannot be read (the model still comes in, plain).</summary>
    public static RgbaImage? LoadPicture(string file, ICollection<string>? notes)
    {
        try
        {
            return Tga.DecodeAny(File.ReadAllBytes(file), Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException)
        {
            notes?.Add($"The picture {Path.GetFileName(file)} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The mesh as an OBJ GZDoom reads: triangles only, each surface's <c>usemtl</c> naming <paramref name="pictureOf"/>'s file
    /// (a picture beside the model) or GZDoom's own plain material when it has none. Axes are turned back to OBJ's.
    /// </summary>
    public static string Write(Mesh mesh, Func<int, string?> pictureOf)
    {
        var text = new StringBuilder("# Written by WadSmith\n");
        var c = CultureInfo.InvariantCulture;
        var start = 1;
        for (var s = 0; s < mesh.Surfaces.Count; s++)
        {
            var triangles = mesh.Surfaces[s].Triangles;
            foreach (var v in triangles) text.Append(c, $"v {v.X:R} {v.Z:R} {-v.Y:R}\n");
            foreach (var v in triangles) text.Append(c, $"vt {v.U:R} {1 - v.V:R}\n");
            text.Append("usemtl ").Append(pictureOf(s) ?? "-NOFLAT-").Append('\n');
            for (var i = 0; i < triangles.Count; i += 3) text.Append(c, $"f {start + i}/{start + i} {start + i + 1}/{start + i + 1} {start + i + 2}/{start + i + 2}\n");
            start += triangles.Count;
        }

        return text.ToString();
    }
}
