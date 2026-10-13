using System.Buffers.Binary;
using System.Text;

namespace WadSmith.Core.Models;

/// <summary>
/// Quake III's MD3 models (D16): each surface's first frame, its triangles and texture corners; positions are 1/64 units
/// and already in GZDoom's model axes. A surface's picture is the one its shader names, looked for beside the model by
/// its file name (MD3s store paths from the game they were made for), then a Quake III <c>.skin</c> file's line for it.
/// </summary>
public static class Md3Model
{
    public static Mesh Read(string path, ICollection<string>? notes = null)
    {
        var name = Path.GetFileName(path);
        var data = File.ReadAllBytes(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        if (data.Length < 108 || !data.AsSpan(0, 4).SequenceEqual("IDP3"u8)) throw new CorruptDataException($"{name} is not an MD3 model.");
        var skins = SkinFile(folder, Path.GetFileNameWithoutExtension(path));
        var count = Int(data, 84, name);
        var at = Int(data, 100, name);
        var surfaces = new List<MeshSurface>();
        for (var s = 0; s < count; s++)
        {
            if (at < 0 || at + 108 > data.Length || !data.AsSpan(at, 4).SequenceEqual("IDP3"u8)) throw new CorruptDataException($"{name}: surface {s + 1} is not where the file says.");
            var surface = Text(data, at + 4, 64);
            int shaders = Int(data, at + 76, name), vertices = Int(data, at + 80, name), triangles = Int(data, at + 84, name);
            int ofsTriangles = at + Int(data, at + 88, name), ofsShaders = at + Int(data, at + 92, name), ofsSt = at + Int(data, at + 96, name), ofsXyz = at + Int(data, at + 100, name);
            Check(data, ofsTriangles, triangles * 12, name);
            Check(data, ofsSt, vertices * 8, name);
            Check(data, ofsXyz, vertices * 8, name);
            var corners = new List<MeshVertex>(triangles * 3);
            for (var t = 0; t < triangles; t++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var v = Int(data, ofsTriangles + t * 12 + k * 4, name);
                    if (v < 0 || v >= vertices) throw new CorruptDataException($"{name}: a triangle of {surface} names vertex {v} of {vertices}.");
                    var p = ofsXyz + v * 8;
                    var uv = ofsSt + v * 8;
                    corners.Add(new MeshVertex(
                        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p)) / 64f, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + 2)) / 64f, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + 4)) / 64f,
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(uv)), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(uv + 4))));
                }
            }

            string? file = null;
            if (shaders > 0 && ofsShaders + 64 <= data.Length)
            {
                var shader = Text(data, ofsShaders, 64).Replace('\\', '/');
                file = ObjModel.Guess(folder, Path.GetFileNameWithoutExtension(shader)) ?? ObjModel.Find(folder, Path.GetFileName(shader));
            }

            if (file is null && skins.TryGetValue(surface, out var fromSkin)) file = ObjModel.Guess(folder, Path.GetFileNameWithoutExtension(fromSkin)) ?? ObjModel.Find(folder, Path.GetFileName(fromSkin));
            if (file is null && count == 1 && ObjModel.Beside(folder, Path.GetFileNameWithoutExtension(path)) is { } beside)
            {
                file = beside;
                notes?.Add($"The model names no picture found beside it, so {Path.GetFileName(beside)} was used.");
            }

            surfaces.Add(new MeshSurface(surface, corners, file, file is null ? null : ObjModel.LoadPicture(file, notes)));
            at += Int(data, at + 104, name);
        }

        if (surfaces.Sum(s => s.Triangles.Count) == 0) throw new CorruptDataException($"{name} has no faces to show.");
        return new Mesh(surfaces);
    }

    /// <summary>A Quake III <c>.skin</c> beside the model ("surface,path" lines): the model's own, else the default one.</summary>
    private static Dictionary<string, string> SkinFile(string folder, string stem)
    {
        var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = ObjModel.Find(folder, stem + ".skin") ?? ObjModel.Find(folder, stem + "_default.skin");
        if (file is null) return lines;
        foreach (var line in File.ReadLines(file))
        {
            var comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0 && line[(comma + 1)..].Trim() is { Length: > 0 } picture) lines[line[..comma].Trim()] = picture;
        }

        return lines;
    }

    private static int Int(byte[] data, int at, string name) =>
        at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) : throw new CorruptDataException($"{name} ends early.");

    private static void Check(byte[] data, int at, int length, string name)
    {
        if (at < 0 || length < 0 || (long)at + length > data.Length) throw new CorruptDataException($"{name} ends early.");
    }

    private static string Text(byte[] data, int at, int length)
    {
        var span = data.AsSpan(at, length);
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
    }
}
