using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Models;

/// <summary>
/// glTF 2.0 models, binary (<c>.glb</c>) or text (<c>.gltf</c> with embedded or external buffers): every mesh the default
/// scene places, through its nodes' transforms, as triangles grouped by material, with each material's base colour picture
/// (its colour factor applied) or its colour alone. GZDoom reads no glTF, so the importer writes the result as an OBJ. glTF
/// fixes its axes and units — metres, Y up, the front facing +Z — so the model is turned to face Doom's forward (+X).
/// Animation and skins are not read here: the bind pose stands.
/// </summary>
public static class GltfModel
{
    private const uint Json = 0x4E4F534A, Bin = 0x004E4942;

    public static Mesh Read(string path, ICollection<string>? notes = null)
    {
        var name = Path.GetFileName(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var data = File.ReadAllBytes(path);
        byte[]? binary = null;
        ReadOnlyMemory<byte> json;
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("glTF"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != 2) throw new CorruptDataException($"{name} is glTF version {BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))}; WadSmith reads version 2.");
            json = default;
            for (var at = 12; at + 8 <= data.Length;)
            {
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                if (length < 0 || at + 8L + length > data.Length) throw new CorruptDataException($"{name} ends early.");
                if (type == Json) json = data.AsMemory(at + 8, length);
                else if (type == Bin) binary = data.AsSpan(at + 8, length).ToArray();
                at += 8 + length;
            }

            if (json.IsEmpty) throw new CorruptDataException($"{name} has no glTF description.");
        }
        else
        {
            json = data;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CorruptDataException($"{name} is not a glTF model: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("extensionsRequired", out var required) && required.GetArrayLength() > 0)
            {
                throw new CorruptDataException($"{name} needs {string.Join(", ", required.EnumerateArray().Select(e => e.GetString()))}, which WadSmith does not read yet (often compression: export it again without it).");
            }

            var buffers = Array(root, "buffers").Select((b, i) => Buffer(b, i)).ToList();
            var views = Array(root, "bufferViews");
            var accessors = Array(root, "accessors");
            var materials = Array(root, "materials");
            var textures = Array(root, "textures");
            var images = Array(root, "images");
            var meshes = Array(root, "meshes");
            var nodes = Array(root, "nodes");

            byte[] Buffer(JsonElement buffer, int index)
            {
                if (!buffer.TryGetProperty("uri", out var uri)) return binary ?? throw new CorruptDataException($"{name}: buffer {index} has no data.");
                return Load(uri.GetString() ?? "");
            }

            byte[] Load(string uri)
            {
                if (uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    var comma = uri.IndexOf(',', StringComparison.Ordinal);
                    return comma > 0 ? Convert.FromBase64String(uri[(comma + 1)..]) : throw new CorruptDataException($"{name} holds a data address with no data.");
                }

                var file = ObjModel.Find(folder, Uri.UnescapeDataString(uri).Replace('\\', '/')) ?? throw new CorruptDataException($"{name} needs {uri}, which is not beside it.");
                return File.ReadAllBytes(file);
            }

            ReadOnlySpan<byte> View(int index, out int stride)
            {
                var view = At(views, index, "buffer view");
                var buffer = buffers[Int(view, "buffer")];
                var offset = Int(view, "byteOffset", 0);
                var length = Int(view, "byteLength");
                stride = Int(view, "byteStride", 0);
                if (offset < 0 || length < 0 || (long)offset + length > buffer.Length) throw new CorruptDataException($"{name} ends early.");
                return buffer.AsSpan(offset, length);
            }

            // An accessor's values as floats (normalized integers scaled to 0..1), size components each.
            float[] Floats(int index, int size)
            {
                var accessor = At(accessors, index, "accessor");
                var count = Int(accessor, "count");
                var kind = Int(accessor, "componentType");
                var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();
                var values = new float[count * size];
                if (!accessor.TryGetProperty("bufferView", out var viewIndex)) return values;
                var span = View(viewIndex.GetInt32(), out var stride);
                var component = kind switch { 5126 or 5125 => 4, 5123 or 5122 => 2, _ => 1 };
                var offset = Int(accessor, "byteOffset", 0);
                if (stride == 0) stride = component * size;
                for (var i = 0; i < count; i++)
                {
                    for (var c = 0; c < size; c++)
                    {
                        var at = offset + i * stride + c * component;
                        if (at + component > span.Length) throw new CorruptDataException($"{name} ends early.");
                        var s = span[at..];
                        values[i * size + c] = kind switch
                        {
                            5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
                            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
                            5121 => normalized ? s[0] / 255f : s[0],
                            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(s),
                            5120 => normalized ? Math.Max((sbyte)s[0] / 127f, -1) : (sbyte)s[0],
                            5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                            _ => throw new CorruptDataException($"{name}: accessor {index} has components of type {kind}."),
                        };
                    }
                }

                return values;
            }

            // Each material's picture: its base colour texture with its colour factor, or the factor alone.
            var pictures = new Dictionary<int, RgbaImage>();
            RgbaImage Picture(int material)
            {
                if (pictures.TryGetValue(material, out var known)) return known;
                RgbaImage? image = null;
                var factor = new[] { 1f, 1f, 1f, 1f };
                if (material >= 0 && material < materials.Count && materials[material].TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var f) && f.GetArrayLength() == 4) factor = [.. f.EnumerateArray().Select(v => v.GetSingle())];
                    if (pbr.TryGetProperty("baseColorTexture", out var t) && Int(t, "index") is var ti && ti >= 0 && ti < textures.Count && textures[ti].TryGetProperty("source", out var source))
                    {
                        var img = At(images, source.GetInt32(), "image");
                        try
                        {
                            var bytes = img.TryGetProperty("bufferView", out var bv) ? View(bv.GetInt32(), out _).ToArray() : Load(img.GetProperty("uri").GetString() ?? "");
                            image = Tga.DecodeAny(bytes, $"{name}'s picture {source.GetInt32()}");
                        }
                        catch (CorruptDataException ex)
                        {
                            notes?.Add($"A picture of {name} could not be read: {ex.Message}");
                        }
                    }
                }

                if (image is null)
                {
                    image = new RgbaImage(4, 4);
                    for (var i = 0; i < 16; i++) image.Set(i % 4, i / 4, 255, 255, 255);
                }

                if (factor.Take(3).Any(v => v < 0.999f))
                {
                    for (var y = 0; y < image.Height; y++)
                    {
                        for (var x = 0; x < image.Width; x++)
                        {
                            var (r, g, b, a) = image.Get(x, y);
                            image.Set(x, y, (byte)(r * factor[0]), (byte)(g * factor[1]), (byte)(b * factor[2]), a);
                        }
                    }
                }

                return pictures[material] = image;
            }

            // The scene's nodes, each mesh placed by its node's transform, then turned into GZDoom's model axes:
            // glTF (x left, y up, z forward) → (forward, left, up).
            var groups = new Dictionary<int, List<MeshVertex>>();
            var sceneIndex = root.TryGetProperty("scene", out var s0) ? s0.GetInt32() : 0;
            var scenes = Array(root, "scenes");
            IEnumerable<int> roots = scenes.Count > sceneIndex && scenes[sceneIndex].TryGetProperty("nodes", out var top) ? top.EnumerateArray().Select(e => e.GetInt32()) : Enumerable.Range(0, nodes.Count);
            var visited = new HashSet<int>();
            foreach (var r in roots) Walk(r, Matrix4x4.Identity);

            void Walk(int index, Matrix4x4 parent)
            {
                if (!visited.Add(index)) return;
                var node = At(nodes, index, "node");
                var world = Local(node) * parent;
                if (node.TryGetProperty("mesh", out var m)) AddMesh(At(meshes, m.GetInt32(), "mesh"), world);
                if (node.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray()) Walk(child.GetInt32(), world);
                }
            }

            void AddMesh(JsonElement mesh, Matrix4x4 world)
            {
                foreach (var primitive in Array(mesh, "primitives"))
                {
                    var mode = Int(primitive, "mode", 4);
                    if (mode is not (4 or 5 or 6)) continue;
                    var attributes = primitive.GetProperty("attributes");
                    if (!attributes.TryGetProperty("POSITION", out var p)) continue;
                    var positions = Floats(p.GetInt32(), 3);
                    var uv = attributes.TryGetProperty("TEXCOORD_0", out var t) ? Floats(t.GetInt32(), 2) : null;
                    var count = positions.Length / 3;
                    var order = primitive.TryGetProperty("indices", out var ix) ? Floats(ix.GetInt32(), 1).Select(v => (int)v).ToArray() : [.. Enumerable.Range(0, count)];
                    var material = Int(primitive, "material", -1);
                    if (!groups.TryGetValue(material, out var list)) groups[material] = list = [];
                    void Corner(int i)
                    {
                        if (i < 0 || i >= count) throw new CorruptDataException($"{name}: a triangle names vertex {i} of {count}.");
                        var v = Vector3.Transform(new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]), world);
                        list.Add(new MeshVertex(v.Z, v.X, v.Y, uv?[i * 2] ?? 0, uv?[i * 2 + 1] ?? 0));
                    }

                    for (var k = 0; k + 2 < order.Length; k += mode == 4 ? 3 : 1)
                    {
                        var (a, b, c) = mode switch
                        {
                            5 => k % 2 == 0 ? (order[k], order[k + 1], order[k + 2]) : (order[k + 1], order[k], order[k + 2]),
                            6 => (order[0], order[k + 1], order[k + 2]),
                            _ => (order[k], order[k + 1], order[k + 2]),
                        };
                        if (mode == 6 && k + 2 >= order.Length) break;
                        Corner(a);
                        Corner(b);
                        Corner(c);
                    }
                }
            }

            var surfaces = new List<MeshSurface>();
            foreach (var (material, triangles) in groups.OrderBy(g => g.Key))
            {
                if (triangles.Count == 0) continue;
                var title = material >= 0 && material < materials.Count && materials[material].TryGetProperty("name", out var mn) ? mn.GetString() ?? $"material {material}" : $"material {material}";
                surfaces.Add(new MeshSurface(title, triangles, null, Picture(material)));
            }

            if (surfaces.Count == 0) throw new CorruptDataException($"{name} has no faces to show.");
            return new Mesh(surfaces);
        }

        static List<JsonElement> Array(JsonElement e, string property) => e.TryGetProperty(property, out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray()] : [];

        JsonElement At(List<JsonElement> list, int index, string what) => index >= 0 && index < list.Count ? list[index] : throw new CorruptDataException($"{name} names {what} {index}, which it does not have.");

        static int Int(JsonElement e, string property, int otherwise = -1) => e.TryGetProperty(property, out var v) && v.TryGetInt32(out var i) ? i : otherwise;
    }

    /// <summary>A node's own transform: its matrix, or its translation, rotation and scale (row vectors, as System.Numerics multiplies).</summary>
    private static Matrix4x4 Local(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m) && m.GetArrayLength() == 16)
        {
            var v = m.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            // glTF stores columns first; System.Numerics' rows are glTF's columns.
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }

        float[] Read(string property, float[] otherwise) => node.TryGetProperty(property, out var p) ? [.. p.EnumerateArray().Select(e => e.GetSingle())] : otherwise;
        var t = Read("translation", [0, 0, 0]);
        var r = Read("rotation", [0, 0, 0, 1]);
        var s = Read("scale", [1, 1, 1]);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3])) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }
}
