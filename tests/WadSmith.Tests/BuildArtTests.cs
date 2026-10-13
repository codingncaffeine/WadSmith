using System.Buffers.Binary;
using System.Text;
using WadSmith.Core.Build;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Duke Nukem 3D's art (D48): the group file, its tiles column by column through its palette, and the tiles as pictures to borrow.</summary>
public sealed class BuildArtTests
{
    /// <summary>A group file holding a palette (index 1 pure red, 2 pure green in 6-bit colour) and one 2 × 3 tile, number 7, offset (1, -2).</summary>
    private static byte[] Grp()
    {
        var palette = new byte[768 + 32];
        palette[3] = 63;
        palette[7] = 63;
        var art = new byte[16 + 8 + 6];
        BinaryPrimitives.WriteInt32LittleEndian(art, 1);
        BinaryPrimitives.WriteInt32LittleEndian(art.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(art.AsSpan(8), 7);
        BinaryPrimitives.WriteInt32LittleEndian(art.AsSpan(12), 7);
        BinaryPrimitives.WriteInt16LittleEndian(art.AsSpan(16), 2);
        BinaryPrimitives.WriteInt16LittleEndian(art.AsSpan(18), 3);
        BinaryPrimitives.WriteInt32LittleEndian(art.AsSpan(20), (1 << 8) | ((-2 & 0xFF) << 16));
        // Column by column: the first column red, red, see-through; the second green three times.
        art[24] = 1; art[25] = 1; art[26] = 255; art[27] = 2; art[28] = 2; art[29] = 2;
        (string Name, byte[] Data)[] files = [("PALETTE.DAT", palette), ("TILES000.ART", art)];
        var grp = new List<byte>(Encoding.ASCII.GetBytes(GrpArchive.Signature));
        grp.AddRange(BitConverter.GetBytes(files.Length));
        foreach (var (name, data) in files)
        {
            grp.AddRange(Encoding.ASCII.GetBytes(name.PadRight(12, '\0')));
            grp.AddRange(BitConverter.GetBytes(data.Length));
        }

        foreach (var (_, data) in files) grp.AddRange(data);
        return [.. grp];
    }

    [Fact]
    public void ATileIsReadColumnByColumnThroughThePalette()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "TEST.GRP");
        File.WriteAllBytes(path, Grp());
        Assert.True(GrpArchive.IsGrp(path));
        var art = BuildArt.Read(GrpArchive.Open(path));
        var tile = Assert.Single(art.Tiles);
        Assert.Equal((7, 2, 3, 1, -2), (tile.Number, tile.Width, tile.Height, tile.OffsetX, tile.OffsetY));

        var picture = art.Picture(tile);
        byte[] Px(int x, int y) => picture.Pixels.AsSpan(((y * 2) + x) * 4, 4).ToArray();
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(0, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(0, 1));
        Assert.Equal(0, Px(0, 2)[3]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Px(1, 2));
        Assert.Equal((2, 1), (picture.OffsetX, picture.OffsetY));

        // As resources: the tile is a texture named DK0007, its picture a PNG; the palette a PLAYPAL.
        var container = BuildArtContainer.Open(path);
        var entry = container.Find("DK0007", ResourceNamespace.Textures)!;
        var decoded = ImageCodec.Decode(entry.Read());
        Assert.Equal((2, 3), (decoded.Width, decoded.Height));
        Assert.Equal(768, container.Find("PLAYPAL")!.Read().Length);
        var library = new TextureLibrary(new ResourceStack([container]));
        Assert.NotNull(library.GetWall("DK0007"));
    }

    [Fact]
    public void DukeNukem3DIsFoundAndItsTilesComeIntoADoomProject()
    {
        var grp = GameFiles.Require("DUKE3D.GRP");
        var report = GameLocator.Report([new ScanTarget(Path.GetDirectoryName(grp)!, 1)], ArchiveCache.Empty(), cancel: TestContext.Current.CancellationToken);
        var duke = Assert.Single(report.Games, g => g.Game.Id == "duke3d");
        Assert.False(duke.Game.Supported);

        var games = new[] { duke, new FoundGame(GameCatalog.Get("doom2")!, GameFiles.Require("DOOM2.WAD"), 1) };
        var resources = GameResources.Load("duke3d", null, games, GameFiles.Require("gzdoom.pk3"))!;
        Assert.Equal(2706, resources.Textures.Walls.Count);  // every tile of the 1.3D/Atomic art with a size, counted independently

        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var doom = GameResources.Load("doom2", project.Folder, games, GameFiles.Require("gzdoom.pk3"))!;
        var name = Borrowing.CopyPicture(project, doom, resources, "DK0450", flat: false);
        Assert.Equal("DK0450", name);
        Assert.True(File.Exists(Path.Combine(project.Folder, "textures", "DK0450.png")));
        Assert.NotNull(doom.Textures.GetWall("DK0450"));
    }
}
