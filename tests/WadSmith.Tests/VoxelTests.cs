using System.Buffers.Binary;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Models;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Voxel props (Phase 11): MagicaVoxel files written here to their published layout, turned into KVX that passes GZDoom's own checks, and brought in through VOXELDEF.</summary>
public sealed class VoxelTests
{
    /// <summary>A MagicaVoxel file: SIZE, XYZI and RGBA under MAIN; colour n is RGBA entry n − 1.</summary>
    internal static string WriteVox(string folder, (int X, int Y, int Z) size, IEnumerable<(int X, int Y, int Z, byte Colour)> voxels, string name = "thing")
    {
        Directory.CreateDirectory(folder);
        var list = voxels.ToList();
        var children = new MemoryStream();
        var w = new BinaryWriter(children);
        w.Write("SIZE"u8); w.Write(12); w.Write(0); w.Write(size.X); w.Write(size.Y); w.Write(size.Z);
        w.Write("XYZI"u8); w.Write(4 + list.Count * 4); w.Write(0); w.Write(list.Count);
        foreach (var v in list) { w.Write((byte)v.X); w.Write((byte)v.Y); w.Write((byte)v.Z); w.Write(v.Colour); }
        w.Write("RGBA"u8); w.Write(1024); w.Write(0);
        for (var i = 0; i < 256; i++) { w.Write((byte)(i == 0 ? 255 : 0)); w.Write((byte)0); w.Write((byte)(i == 1 ? 255 : 0)); w.Write((byte)255); }
        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write("VOX "u8); f.Write(150);
        f.Write("MAIN"u8); f.Write(0); f.Write((int)children.Length); f.Write(children.ToArray());
        var path = Path.Combine(folder, name + ".vox");
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    /// <summary>An L: a red column three high at MagicaVoxel (0, 0), a blue voxel at (2, 1, 0).</summary>
    private static string WriteL(string folder) => WriteVox(folder, (3, 2, 3), [(0, 0, 0, 1), (0, 0, 1, 1), (0, 0, 2, 1), (2, 1, 0, 2)]);

    [Fact]
    public void AMagicaVoxelModelIsReadFacingDoomsForward()
    {
        using var temp = new TempFolder();
        var model = MagicaVoxel.Read(WriteL(temp.Path));
        // MagicaVoxel's front is -y: (x, y, z) → (forward 1 - y, left x, up z).
        Assert.Equal((2, 3, 3), (model.Forward, model.Left, model.Up));
        Assert.Equal(4, model.Count);
        Assert.Equal([1, 1, 1], new[] { model[1, 0, 0], model[1, 0, 1], model[1, 0, 2] });
        Assert.Equal(2, model[0, 2, 0]);
        Assert.Equal((255, 0, 0), model.Palette[0]);
        Assert.Equal((0, 0, 255), model.Palette[1]);
    }

    /// <summary>GZDoom's own checks on a KVX (voxels.cpp R_LoadKVX): one mip, x offsets from the offsets' size to the data's end, the palette exactly the last 768 bytes, every column inside the data.</summary>
    private static void PassesGZDoomsChecks(byte[] kvx)
    {
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(kvx.AsSpan(at));
        var numBytes = I(0);
        int xs = I(4), ys = I(8);
        var offsetSize = (xs + 1) * 4 + xs * (ys + 1) * 2;
        var dataSize = numBytes - 24 - offsetSize;
        Assert.True(dataSize >= 0);
        Assert.Equal(offsetSize, I(28));
        Assert.Equal(dataSize, I(28 + xs * 4) - offsetSize);
        Assert.Equal(kvx.Length - 768, 4 + numBytes);
        for (var x = 0; x < xs; x++)
        {
            for (var y = 0; y < ys; y++)
            {
                var yoff = BinaryPrimitives.ReadInt16LittleEndian(kvx.AsSpan(28 + (xs + 1) * 4 + (x * (ys + 1) + y) * 2));
                Assert.InRange(I(28 + x * 4) - offsetSize + yoff, 0, dataSize);
            }
        }

        Assert.All(kvx[^768..], b => Assert.InRange(b, 0, 63));
    }

    [Fact]
    public void TheKvxPassesGZDoomsChecksAndReadsBackTheSameVoxels()
    {
        using var temp = new TempFolder();
        var model = MagicaVoxel.Read(WriteL(temp.Path));
        var kvx = Kvx.Write(model);
        PassesGZDoomsChecks(kvx);
        var back = Kvx.Read(kvx);
        Assert.Equal(model.Cells, back.Cells);
        // VGA levels (0–63) back to full range: 63 is 255.
        Assert.Equal((255, 0, 0), back.Palette[0]);
        // The pivot is the middle of the base: x and y halfway (1/256 units), z at the bottom.
        Assert.Equal((3 * 128, 2 * 128, 3 * 256), (BinaryPrimitives.ReadInt32LittleEndian(kvx.AsSpan(16)), BinaryPrimitives.ReadInt32LittleEndian(kvx.AsSpan(20)), BinaryPrimitives.ReadInt32LittleEndian(kvx.AsSpan(24))));
    }

    [Fact]
    public void ABuriedVoxelIsLeftOutAndAColumnShowsOnlyItsEnds()
    {
        using var temp = new TempFolder();
        var cube = new List<(int, int, int, byte)>();
        for (var x = 0; x < 3; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                for (var z = 0; z < 3; z++) cube.Add((x, y, z, 1));
            }
        }

        var model = MagicaVoxel.Read(WriteVox(temp.Path, (3, 3, 3), cube));
        var kvx = Kvx.Write(model);
        PassesGZDoomsChecks(kvx);
        Assert.Equal(26, Kvx.Read(kvx).Count);

        // The middle column (x 1, y 1): its top voxel and its bottom one, each a slab of its own showing only top or bottom.
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(kvx.AsSpan(at));
        var offsetSize = 4 * 4 + 3 * 4 * 2;
        var start = 28 + offsetSize + I(28 + 4) - offsetSize + BinaryPrimitives.ReadInt16LittleEndian(kvx.AsSpan(28 + 16 + (1 * 4 + 1) * 2));
        Assert.Equal(new byte[] { 0, 1, 16, 0, 2, 1, 32, 0 }, kvx[start..(start + 8)]);
    }

    [Fact]
    public void AVoxelPropIsDrawnByVoxelDefAndTurnsThere()
    {
        using var temp = new TempFolder();
        var path = WriteL(Path.Combine(temp.Path, "source"));
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = ModelProps.Import(project, game, path);
        var sprite = made.Actor.Thing.Sprite!;
        // Three voxels tall; an object is never under 4 units tall, so the player cannot step through it unseen.
        Assert.Equal(4, (int)made.Actor.Thing.Height);
        Assert.StartsWith("4 voxels, one map unit each: 4 units tall", made.SizeNote, StringComparison.Ordinal);
        PassesGZDoomsChecks(File.ReadAllBytes(Path.Combine(project.Folder, "voxels", sprite + ".kvx")));
        var voxeldef = Path.Combine(project.Folder, ModelProps.VoxelFile);
        Assert.Contains($"{sprite}A = \"{sprite}\" {{ Scale = 1.0 }}", File.ReadAllText(voxeldef), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(project.Folder, "models")));
        Assert.True(game.CanShow(made.Actor.Number));
        var shape = ModelProps.Shape(project.Folder, project.CopiedNow(), made.Actor.Number)!;
        Assert.Equal(1, shape.Stretch);
        Assert.Equal(3f, shape.Mesh.Max.Z);

        ModelProps.Turn(project, made.Actor.Thing.ClassName, 90);
        Assert.Contains($"{sprite}A = \"{sprite}\" {{ Scale = 1.0 AngleOffset = 90 }}", File.ReadAllText(voxeldef), StringComparison.Ordinal);
        Assert.Equal(90, ModelProps.TurnOf(project.CopiedNow(), made.Actor.Thing.ClassName));
        Assert.EndsWith("@90", ModelProps.Shape(project.Folder, project.CopiedNow(), made.Actor.Number)!.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenVoxFileSaysWhy()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "bad.vox");
        // A file with its header and an empty MAIN: version 150, then MAIN with no content and no children.
        File.WriteAllBytes(path, [.. "VOX "u8, 150, 0, 0, 0, .. "MAIN"u8, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Contains("no model", Assert.Throws<CorruptDataException>(() => MagicaVoxel.Read(path)).Message, StringComparison.Ordinal);
        File.WriteAllText(path, "not a voxel");
        Assert.Contains("not a MagicaVoxel model", Assert.Throws<CorruptDataException>(() => MagicaVoxel.Read(path)).Message, StringComparison.Ordinal);
    }
}
