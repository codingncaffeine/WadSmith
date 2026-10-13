using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Models;
using WadSmith.Core.Projects;
using WadSmith.Core.Quake;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Quake's models (Phase 11): MDL read through Quake's palette and written as MD2, MD2 read as GZDoom reads it.</summary>
public sealed class QuakeModelTests
{
    /// <summary>A grey ramp, colour i = (i, i, i), except colour 1 red and colour 2 blue.</summary>
    private static byte[] Palette()
    {
        var p = new byte[768];
        for (var i = 0; i < 256; i++) (p[i * 3], p[i * 3 + 1], p[i * 3 + 2]) = ((byte)i, (byte)i, (byte)i);
        (p[3], p[4], p[5]) = (255, 0, 0);
        (p[6], p[7], p[8]) = (0, 0, 255);
        return p;
    }

    /// <summary>
    /// A Quake MDL: a 4 × 2 skin (left half red, right half blue), three vertices (the third on the seam), one triangle
    /// facing back, a simple frame and a group of two; scale 2, translate (-10, 0, 0).
    /// </summary>
    internal static byte[] Mdl()
    {
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("IDPO"u8); w.Write(6);
        w.Write(2f); w.Write(2f); w.Write(2f); w.Write(-10f); w.Write(0f); w.Write(0f);
        w.Write(30f); w.Write(0f); w.Write(0f); w.Write(20f);
        // Skins, skin size, vertices, triangles, then frame ENTRIES: a group counts as one.
        w.Write(1); w.Write(4); w.Write(2); w.Write(3); w.Write(1); w.Write(2); w.Write(0); w.Write(0); w.Write(1f);
        w.Write(0); w.Write(new byte[] { 1, 1, 2, 2, 1, 1, 2, 2 });
        w.Write(0); w.Write(0); w.Write(0);
        w.Write(0); w.Write(1); w.Write(0);
        w.Write(1); w.Write(1); w.Write(1);
        w.Write(0); w.Write(0); w.Write(1); w.Write(2);
        void Frame(string name, byte lift)
        {
            w.Write(new byte[8]);
            var n = new byte[16];
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(n, 0);
            w.Write(n);
            w.Write(new byte[] { 0, 0, lift, 0, 10, 0, lift, 0, 0, 10, lift, 0 });
        }

        w.Write(0);
        Frame("stand1", 0);
        w.Write(1); w.Write(2); w.Write(new byte[8]); w.Write(0.1f); w.Write(0.2f);
        Frame("walk1", 5);
        Frame("walk2", 10);
        return file.ToArray();
    }

    [Fact]
    public void AnMdlIsReadWithItsFramesItsSkinAndItsSeam()
    {
        var model = Core.Models.Mdl.Read(Mdl(), Palette());
        Assert.Equal(["stand1", "walk1", "walk2"], model.Frames.Select(f => f.Name));
        Assert.Equal((-10f, 0f, 0f), model.Frames[0].Positions[0]);
        Assert.Equal((10f, 0f, 0f), model.Frames[0].Positions[1]);
        Assert.Equal((-10f, 0f, 20f), model.Frames[2].Positions[0]);
        Assert.Equal((255, 0, 0, 255), model.Skin!.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), model.Skin.Get(3, 1));
        // The seam vertex (the third) on a triangle facing back takes the corner half the skin across.
        Assert.Equal((3f, 1f), model.Corners[model.Triangles[0].TC]);
        Assert.Equal((1f, 1f), model.Corners[2]);
    }

    [Fact]
    public void AnMd2WrittenFromAnMdlReadsBackItsFramesAndSkin()
    {
        using var temp = new TempFolder();
        var model = Core.Models.Mdl.Read(Mdl(), Palette());
        File.WriteAllBytes(Path.Combine(temp.Path, "skin0.png"), WadSmith.Core.Graphics.ImageCodec.EncodePng(model.Skin!));
        var path = Path.Combine(temp.Path, "m.md2");
        File.WriteAllBytes(path, Md2.Write(model, "skin0.png"));
        var back = Md2.Read(path);
        Assert.Equal(model.Frames.Select(f => f.Name), back.Frames.Select(f => f.Name));
        Assert.Equal(model.Triangles, back.Triangles);
        for (var f = 0; f < 3; f++)
        {
            for (var v = 0; v < 3; v++)
            {
                var (a, b) = (model.Frames[f].Positions[v], back.Frames[f].Positions[v]);
                Assert.True(MathF.Abs(a.X - b.X) < 0.1f && MathF.Abs(a.Y - b.Y) < 0.1f && MathF.Abs(a.Z - b.Z) < 0.1f, $"frame {f} vertex {v}: {a} → {b}");
            }
        }

        Assert.Equal((255, 0, 0, 255), back.Skin!.Get(0, 0));
    }

    [Fact]
    public void AnMdlPropIsWrittenAsAnMd2InQuakesUnits()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "source", "gfx"));
        File.WriteAllBytes(Path.Combine(temp.Path, "source", "gfx", "palette.lmp"), Palette());
        Directory.CreateDirectory(Path.Combine(temp.Path, "source", "progs"));
        var path = Path.Combine(temp.Path, "source", "progs", "thing.mdl");
        File.WriteAllBytes(path, Mdl());
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        // The palette is found in the gfx folder beside the progs folder, as Quake's files lie when taken out of the PAK.
        var made = ModelProps.Import(project, game, path);
        Assert.Equal(1.2, made.Scale, 6);
        var definition = File.ReadAllText(Path.Combine(project.Folder, "models", made.Actor.Thing.ClassName, "modeldef.txt"));
        Assert.Contains("Model 0 \"model.md2\"", definition, StringComparison.Ordinal);
        Assert.Contains("Skin 0 \"skin0.png\"", definition, StringComparison.Ordinal);
        Assert.Equal(3, Md2.Read(Path.Combine(project.Folder, "models", made.Actor.Thing.ClassName, "model.md2")).Frames.Count);

        // Without a palette anywhere it says what to do.
        File.Delete(Path.Combine(temp.Path, "source", "gfx", "palette.lmp"));
        Assert.Contains("palette", Assert.Throws<CorruptDataException>(() => ModelProps.Import(project, game, path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuakesOwnSoldierIsReadFromTheUsersPak()
    {
        var pak = PakArchive.Open(GameFiles.Require("PAK0.PAK"));
        var model = Core.Models.Mdl.Read(pak.Read("progs/soldier.mdl")!, pak.Read("gfx/palette.lmp"), "soldier.mdl");
        Assert.True(model.Frames.Count > 20, $"{model.Frames.Count} frames");
        Assert.NotNull(model.Skin);
        var mesh = model.ToMesh();
        Assert.InRange(mesh.Max.Z - mesh.Min.Z, 40, 90);
    }
}
