using System.Text;
using WadSmith.Core.Graphics;
using WadSmith.Core.Models;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>MD3 and IQM models (Phase 11): files written here to the formats' published layouts are read in GZDoom's axes, skinned, baked and written as props.</summary>
public sealed class ModelFormatTests
{
    /// <summary>The box of <see cref="ModelPropTests"/> in model axes (X forward, Y left, Z up): its front (+X) as one surface, the other five as another.</summary>
    private static (List<(float X, float Y, float Z, float U, float V)> Front, List<(float X, float Y, float Z, float U, float V)> Others) Box()
    {
        (float, float, float)[] c = [(-1, -2, 0), (1, -2, 0), (1, 2, 0), (-1, 2, 0), (-1, -2, 3), (1, -2, 3), (1, 2, 3), (-1, 2, 3)];
        List<(float, float, float, float, float)> Quad(int a, int b, int d, int e) =>
        [
            (c[a].Item1, c[a].Item2, c[a].Item3, 0, 1), (c[b].Item1, c[b].Item2, c[b].Item3, 1, 1), (c[d].Item1, c[d].Item2, c[d].Item3, 1, 0),
            (c[a].Item1, c[a].Item2, c[a].Item3, 0, 1), (c[d].Item1, c[d].Item2, c[d].Item3, 1, 0), (c[e].Item1, c[e].Item2, c[e].Item3, 0, 0),
        ];
        return (Quad(1, 2, 6, 5), [.. Quad(0, 3, 7, 4), .. Quad(0, 1, 5, 4), .. Quad(3, 2, 6, 7), .. Quad(0, 1, 2, 3), .. Quad(4, 5, 6, 7)]);
    }

    private static void Pictures(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, r, b) in new[] { ("red.png", (byte)255, (byte)0), ("blue.png", (byte)0, (byte)255) })
        {
            var image = new RgbaImage(4, 4);
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++) image.Set(x, y, r, 0, b);
            }

            File.WriteAllBytes(Path.Combine(folder, name), ImageCodec.EncodePng(image));
        }
    }

    private static byte[] Name(string text, int length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>An MD3 of the box: one frame, one tag (so the tags' and surfaces' offsets differ), a surface per side group with a shader path from another game's folders.</summary>
    internal static string WriteMd3(string folder)
    {
        Pictures(folder);
        var (front, rest) = Box();
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        foreach (var (surface, shader, corners) in new[] { ("front", "models/props/crate/red.tga", front), ("sides", "models/props/crate/BLUE.jpg", rest) })
        {
            const int header = 108, shaders = 68;
            var n = corners.Count;
            int ofsTriangles = header + shaders, ofsSt = ofsTriangles + n / 3 * 12, ofsXyz = ofsSt + n * 8, end = ofsXyz + n * 8;
            w.Write("IDP3"u8); w.Write(Name(surface, 64)); w.Write(0); w.Write(1); w.Write(1); w.Write(n); w.Write(n / 3);
            w.Write(ofsTriangles); w.Write(header); w.Write(ofsSt); w.Write(ofsXyz); w.Write(end);
            w.Write(Name(shader, 64)); w.Write(0);
            for (var i = 0; i < n; i++) w.Write(i);
            foreach (var v in corners) { w.Write(v.U); w.Write(v.V); }
            foreach (var v in corners) { w.Write((short)(v.X * 64)); w.Write((short)(v.Y * 64)); w.Write((short)(v.Z * 64)); w.Write((short)0); }
        }

        var file = new MemoryStream();
        var h = new BinaryWriter(file);
        const int frames = 108, tags = frames + 56, surfaces = tags + 112;
        h.Write("IDP3"u8); h.Write(15); h.Write(Name("crate", 64)); h.Write(0); h.Write(1); h.Write(1); h.Write(2); h.Write(0);
        h.Write(frames); h.Write(tags); h.Write(surfaces); h.Write(surfaces + (int)body.Length);
        h.Write(new byte[56]);
        h.Write(new byte[112]);
        h.Write(body.ToArray());
        var path = Path.Combine(folder, "crate.md3");
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    /// <summary>An IQM (version 2) of the box: positions and texture corners as float arrays, two meshes whose materials name pictures beside it.</summary>
    internal static string WriteIqm(string folder)
    {
        Pictures(folder);
        var (front, rest) = Box();
        var all = front.Concat(rest).ToList();
        var text = Encoding.ASCII.GetBytes("\0front\0red.png\0sides\0blue.png\0");
        const int header = 124;
        int ofsText = header, ofsMeshes = ofsText + text.Length, ofsArrays = ofsMeshes + 2 * 24, ofsPositions = ofsArrays + 2 * 20, ofsCorners = ofsPositions + all.Count * 12, ofsTriangles = ofsCorners + all.Count * 8, end = ofsTriangles + all.Count / 3 * 12;
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("INTERQUAKEMODEL\0"u8);
        uint[] fields = [2, (uint)end, 0, (uint)text.Length, (uint)ofsText, 2, (uint)ofsMeshes, 2, (uint)all.Count, (uint)ofsArrays, (uint)(all.Count / 3), (uint)ofsTriangles, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        foreach (var f in fields) w.Write(f);
        w.Write(text);
        // Meshes: name, material, first vertex, vertices, first triangle, triangles.
        w.Write(1u); w.Write(7u); w.Write(0u); w.Write((uint)front.Count); w.Write(0u); w.Write((uint)(front.Count / 3));
        w.Write(15u); w.Write(21u); w.Write((uint)front.Count); w.Write((uint)rest.Count); w.Write((uint)(front.Count / 3)); w.Write((uint)(rest.Count / 3));
        // Vertex arrays: type, flags, format (7 = float), size, offset.
        w.Write(0u); w.Write(0u); w.Write(7u); w.Write(3u); w.Write((uint)ofsPositions);
        w.Write(1u); w.Write(0u); w.Write(7u); w.Write(2u); w.Write((uint)ofsCorners);
        foreach (var v in all) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        foreach (var v in all) { w.Write(v.U); w.Write(v.V); }
        for (var i = 0u; i < all.Count; i++) w.Write(i);
        var path = Path.Combine(folder, "crate.iqm");
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    public static TheoryData<string> Formats => ["md3", "iqm"];

    [Theory]
    [MemberData(nameof(Formats))]
    public void TheModelIsReadInGZDoomsAxesWithEachSurfacesPicture(string format)
    {
        using var temp = new TempFolder();
        var path = format == "md3" ? WriteMd3(temp.Path) : WriteIqm(temp.Path);
        var mesh = format == "md3" ? Md3Model.Read(path) : IqmModel.Read(path);
        Assert.Equal((-1f, -2f, 0f), mesh.Min);
        Assert.Equal((1f, 2f, 3f), mesh.Max);
        Assert.Equal(["front", "sides"], mesh.Surfaces.Select(s => s.Name));
        Assert.Equal((255, 0, 0, 255), mesh.Surfaces[0].Image!.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), mesh.Surfaces[1].Image!.Get(0, 0));

        // Seen from the front, red; from its left, blue — the same as the OBJ box.
        var (scale, _) = ModelProps.SizeOf(mesh);
        var front = MeshBaker.Bake(mesh, scale, (0, 0, 0), 1);
        Assert.InRange(front.Width, 153, 156);
        Assert.Equal((byte)0, front.Get(front.Width / 2, front.Height / 2).B);
        var left = MeshBaker.Bake(mesh, scale, (0, 0, 0), 3);
        Assert.Equal((byte)0, left.Get(left.Width / 2, left.Height / 2).R);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void AnMd3OrIqmPropIsCopiedAsItIsAndSkinnedSurfaceBySurface(string format)
    {
        using var temp = new TempFolder();
        var path = format == "md3" ? WriteMd3(Path.Combine(temp.Path, "source")) : WriteIqm(Path.Combine(temp.Path, "source"));
        var project = WadSmith.Core.Projects.Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = WadSmith.Editor.GameResources.Load("freedoom2", project.Folder, [new WadSmith.Core.Games.FoundGame(WadSmith.Core.Games.GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = ModelProps.Import(project, game, path);
        Assert.Equal("Crate", made.Actor.Thing.Name);
        var folder = Path.Combine(project.Folder, "models", made.Actor.Thing.ClassName);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(folder, "model." + format)));
        var definition = File.ReadAllText(Path.Combine(folder, "modeldef.txt"));
        Assert.Contains($"Model 0 \"model.{format}\"", definition, StringComparison.Ordinal);
        Assert.Contains("SurfaceSkin 0 0 \"skin0.png\"", definition, StringComparison.Ordinal);
        Assert.Contains("SurfaceSkin 0 1 \"skin1.png\"", definition, StringComparison.Ordinal);
        Assert.True(game.CanShow(made.Actor.Number));
    }

    [Fact]
    public void ACutShortMd3OrIqmSaysSo()
    {
        using var temp = new TempFolder();
        foreach (var path in new[] { WriteMd3(Path.Combine(temp.Path, "a")), WriteIqm(Path.Combine(temp.Path, "b")) })
        {
            var data = File.ReadAllBytes(path);
            File.WriteAllBytes(path, data[..(data.Length - 40)]);
            var error = Assert.Throws<WadSmith.Core.CorruptDataException>(() => path.EndsWith(".md3", StringComparison.Ordinal) ? Md3Model.Read(path) : IqmModel.Read(path));
            Assert.Contains("ends early", error.Message, StringComparison.Ordinal);
        }
    }
}
