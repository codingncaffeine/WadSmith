using System.Buffers.Binary;
using System.Text;

namespace WadSmith.Core.Models;

/// <summary>
/// Inter-Quake Models (IQM, D16): each mesh's triangles in the bind pose, with its texture corners; positions are already in
/// GZDoom's model axes. A mesh's picture is its material's name, a picture beside the model (as GZDoom loads it).
/// </summary>
public static class IqmModel
{
    private const int Position = 0, TexCoord = 1, FloatFormat = 7;

    public static Mesh Read(string path, ICollection<string>? notes = null)
    {
        var name = Path.GetFileName(path);
        var data = File.ReadAllBytes(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        if (data.Length < 124 || !data.AsSpan(0, 16).SequenceEqual("INTERQUAKEMODEL\0"u8)) throw new CorruptDataException($"{name} is not an IQM model.");
        if (U(16) != 2) throw new CorruptDataException($"{name} is IQM version {U(16)}; GZDoom reads version 2.");
        uint numText = U(28), ofsText = U(32), numMeshes = U(36), ofsMeshes = U(40), numArrays = U(44), numVertices = U(48), ofsArrays = U(52), numTriangles = U(56), ofsTriangles = U(60);

        float[]? positions = null, corners = null;
        for (var a = 0u; a < numArrays; a++)
        {
            var at = ofsArrays + a * 20;
            uint type = U(at), format = U(at + 8), size = U(at + 12), offset = U(at + 16);
            if (type is not (Position or TexCoord)) continue;
            if (format != FloatFormat || size != (type == Position ? 3u : 2u)) throw new CorruptDataException($"{name} stores its {(type == Position ? "positions" : "texture corners")} in a form GZDoom does not read.");
            Need(offset, numVertices * size * 4);
            var values = new float[numVertices * size];
            for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((int)(offset + i * 4)));
            if (type == Position) positions = values;
            else corners = values;
        }

        if (positions is null) throw new CorruptDataException($"{name} has no vertex positions.");
        Need(ofsTriangles, numTriangles * 12);
        Need(ofsMeshes, numMeshes * 24);
        var surfaces = new List<MeshSurface>();
        for (var m = 0u; m < numMeshes; m++)
        {
            var at = ofsMeshes + m * 24;
            var mesh = TextAt(U(at));
            var material = TextAt(U(at + 4));
            uint firstTriangle = U(at + 16), triangles = U(at + 20);
            if ((ulong)firstTriangle + triangles > numTriangles) throw new CorruptDataException($"{name}: the mesh {mesh} names triangles past the {numTriangles} listed.");
            var list = new List<MeshVertex>((int)triangles * 3);
            for (var t = firstTriangle; t < firstTriangle + triangles; t++)
            {
                for (var k = 0u; k < 3; k++)
                {
                    var v = U(ofsTriangles + t * 12 + k * 4);
                    if (v >= numVertices) throw new CorruptDataException($"{name}: a triangle names vertex {v} of {numVertices}.");
                    list.Add(new MeshVertex(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2], corners?[v * 2] ?? 0, corners?[v * 2 + 1] ?? 0));
                }
            }

            var file = material.Length == 0 ? null : ObjModel.Find(folder, material.Replace('\\', '/')) ?? ObjModel.Guess(folder, Path.GetFileNameWithoutExtension(material));
            if (file is null && numMeshes == 1 && ObjModel.Beside(folder, Path.GetFileNameWithoutExtension(path)) is { } beside)
            {
                file = beside;
                notes?.Add($"The model names no picture found beside it, so {Path.GetFileName(beside)} was used.");
            }

            surfaces.Add(new MeshSurface(mesh, list, file, file is null ? null : ObjModel.LoadPicture(file, notes)));
        }

        if (surfaces.Sum(s => s.Triangles.Count) == 0) throw new CorruptDataException($"{name} has no faces to show.");
        return new Mesh(surfaces);

        uint U(uint at) => at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at)) : throw new CorruptDataException($"{name} ends early.");

        void Need(uint at, uint length)
        {
            if ((ulong)at + length > (ulong)data.Length) throw new CorruptDataException($"{name} ends early.");
        }

        string TextAt(uint offset)
        {
            if (offset >= numText || ofsText + offset >= data.Length) return "";
            var span = data.AsSpan((int)(ofsText + offset));
            var end = span.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
        }
    }
}
