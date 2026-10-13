using System.Text;
using System.Text.Json;
using WadSmith.Core;
using WadSmith.Core.Graphics;
using WadSmith.Core.Models;

namespace WadSmith.Tests;

/// <summary>glTF 2.0 models (Phase 11): written here to the specification, read with their nodes' transforms, materials and pictures, turned to face Doom's forward.</summary>
public sealed class GltfModelTests
{
    /// <summary>
    /// A GLB of one quad in glTF's axes (x left, y up, z forward): 2 m wide (x from -1 to 1), 3 m tall, standing at z = 0.5,
    /// facing +z; red from an embedded PNG. A second quad lies flat with a blue colour factor and no picture. When
    /// <paramref name="turned"/>, its node is a quarter turn about y and 10 m along x.
    /// </summary>
    internal static string WriteGlb(string folder, bool turned = false)
    {
        Directory.CreateDirectory(folder);
        var red = new RgbaImage(2, 2);
        for (var i = 0; i < 4; i++) red.Set(i % 2, i / 2, 255, 0, 0);
        var png = ImageCodec.EncodePng(red);
        float[] positions = [1, 0, 0.5f, -1, 0, 0.5f, -1, 3, 0.5f, 1, 3, 0.5f, 1, 0, -1, -1, 0, -1, -1, 0, -2, 1, 0, -2];
        float[] uvs = [0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1];
        ushort[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var bin = new MemoryStream();
        var w = new BinaryWriter(bin);
        foreach (var f in positions) w.Write(f);
        foreach (var f in uvs) w.Write(f);
        foreach (var i in indices) w.Write(i);
        var pngAt = (int)bin.Length;
        w.Write(png);
        while (bin.Length % 4 != 0) w.Write((byte)0);
        var node = turned
            ? new Dictionary<string, object> { ["mesh"] = 0, ["translation"] = new[] { 10f, 0, 0 }, ["rotation"] = new[] { 0f, MathF.Sqrt(0.5f), 0, MathF.Sqrt(0.5f) } }
            : new Dictionary<string, object> { ["mesh"] = 0 };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = new[] { node },
            ["meshes"] = new[] { new { primitives = new object[] { new { attributes = new { POSITION = 0, TEXCOORD_0 = 1 }, indices = 2, material = 0 }, new { attributes = new { POSITION = 0, TEXCOORD_0 = 1 }, indices = 3, material = 1 } } } },
            ["materials"] = new object[] { new { name = "Front", pbrMetallicRoughness = new { baseColorTexture = new { index = 0 } } }, new { name = "Floor", pbrMetallicRoughness = new { baseColorFactor = new[] { 0f, 0, 1, 1 } } } },
            ["textures"] = new[] { new { source = 0 } },
            ["images"] = new[] { new { bufferView = 3, mimeType = "image/png" } },
            ["accessors"] = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = 8, type = "VEC3" },
                new { bufferView = 1, componentType = 5126, count = 8, type = "VEC2" },
                new { bufferView = 2, componentType = 5123, count = 6, type = "SCALAR" },
                new { bufferView = 2, byteOffset = 12, componentType = 5123, count = 6, type = "SCALAR" },
            },
            ["bufferViews"] = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 96 },
                new { buffer = 0, byteOffset = 96, byteLength = 64 },
                new { buffer = 0, byteOffset = 160, byteLength = 24 },
                new { buffer = 0, byteOffset = pngAt, byteLength = png.Length },
            },
            ["buffers"] = new[] { new { byteLength = (int)bin.Length } },
        });
        var text = Encoding.UTF8.GetBytes(json);
        var padded = text.Concat(Enumerable.Repeat((byte)' ', (4 - text.Length % 4) % 4)).ToArray();
        var file = new MemoryStream();
        var g = new BinaryWriter(file);
        g.Write("glTF"u8); g.Write(2u); g.Write((uint)(12 + 8 + padded.Length + 8 + bin.Length));
        g.Write((uint)padded.Length); g.Write(0x4E4F534Au); g.Write(padded);
        g.Write((uint)bin.Length); g.Write(0x004E4942u); g.Write(bin.ToArray());
        var path = Path.Combine(folder, turned ? "turned.glb" : "quad.glb");
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    [Fact]
    public void AGlbIsReadFacingDoomsForwardWithItsPictureAndItsColour()
    {
        using var temp = new TempFolder();
        var mesh = GltfModel.Read(WriteGlb(temp.Path));
        // glTF (x, y, z) → (forward z, left x, up y): the quad stands 0.5 m forward, 2 m wide across, 3 m tall.
        Assert.Equal((-2f, -1f, 0f), mesh.Min);
        Assert.Equal((0.5f, 1f, 3f), mesh.Max);
        Assert.Equal(["Front", "Floor"], mesh.Surfaces.Select(s => s.Name));
        Assert.Equal((255, 0, 0, 255), mesh.Surfaces[0].Image!.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), mesh.Surfaces[1].Image!.Get(0, 0));

        // Seen from the front (rotation 1), the red quad; it faces the viewer.
        var front = MeshBaker.Bake(mesh, 38.4, (0, 0, 0), 1);
        var (r, _, _, a) = front.Get(front.Width / 2, front.Height / 4);
        Assert.True(a == 255 && r > 0, $"rotation 1 shows {r} at alpha {a}");
    }

    [Fact]
    public void ANodesTurnAndPlaceAreApplied()
    {
        using var temp = new TempFolder();
        var mesh = GltfModel.Read(WriteGlb(temp.Path, turned: true));
        // A quarter turn about y takes z (forward) onto x (left), then 10 m along x: the quad now stands 10.5 m to the left.
        Assert.Equal(10.5f, mesh.Max.Y, 3);
        Assert.Equal(1f, mesh.Max.X, 3);
        Assert.Equal(-1f, mesh.Min.X, 3);
    }

    [Fact]
    public void AGlbPropIsWrittenAsAnObjInMetresAndAHugePictureIsShrunk()
    {
        using var temp = new TempFolder();
        var path = WriteGlb(Path.Combine(temp.Path, "source"));
        var project = WadSmith.Core.Projects.Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = WadSmith.Editor.GameResources.Load("freedoom2", project.Folder, [new WadSmith.Core.Games.FoundGame(WadSmith.Core.Games.GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = WadSmith.Editor.Editing.ModelProps.Import(project, game, path);
        Assert.Equal(38.4, made.Scale, 6);
        Assert.StartsWith("in metres: 3 m tall, 96 units", made.SizeNote, StringComparison.Ordinal);
        var folder = Path.Combine(project.Folder, "models", made.Actor.Thing.ClassName);
        Assert.Contains("Model 0 \"model.obj\"", File.ReadAllText(Path.Combine(folder, "modeldef.txt")), StringComparison.Ordinal);
        Assert.Equal(GltfModel.Read(path).TriangleCount, ObjModel.Read(Path.Combine(folder, "model.obj")).TriangleCount);

        // A 3000 × 1500 picture is kept 1024 across, its shape kept.
        var big = new RgbaImage(3000, 1500);
        var small = WadSmith.Editor.Editing.ModelProps.Shrink(big, 1024);
        Assert.Equal((1024, 512), (small.Width, small.Height));
    }

    [Fact]
    public void CompressedOrBrokenGltfSaysWhy()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "draco.gltf");
        File.WriteAllText(path, """{"asset":{"version":"2.0"},"extensionsRequired":["KHR_draco_mesh_compression"]}""");
        Assert.Contains("KHR_draco_mesh_compression", Assert.Throws<CorruptDataException>(() => GltfModel.Read(path)).Message, StringComparison.Ordinal);
        File.WriteAllText(path, "{ not json");
        Assert.Contains("is not a glTF model", Assert.Throws<CorruptDataException>(() => GltfModel.Read(path)).Message, StringComparison.Ordinal);
        var glb = WriteGlb(temp.Path);
        var data = File.ReadAllBytes(glb);
        File.WriteAllBytes(glb, data[..(data.Length - 100)]);
        Assert.Throws<CorruptDataException>(() => GltfModel.Read(glb));
    }
}
