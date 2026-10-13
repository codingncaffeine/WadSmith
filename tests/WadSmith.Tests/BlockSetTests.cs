using WadSmith.Core.Graphics;
using WadSmith.Core.Models;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Block texture sets as props (Phase 11): any picture of a set makes a metre cube, its faces the right way round.</summary>
public sealed class BlockSetTests
{
    private static void Picture(string path, Func<int, (byte, byte, byte)> colourAt)
    {
        var image = new RgbaImage(8, 8);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var (r, g, b) = colourAt(x);
                image.Set(x, y, r, g, b);
            }
        }

        File.WriteAllBytes(path, ImageCodec.EncodePng(image));
    }

    [Fact]
    public void APictureOfASetIsABlockAndALonePictureIsNot()
    {
        using var temp = new TempFolder();
        Picture(Path.Combine(temp.Path, "crate_side.png"), _ => (1, 1, 1));
        Assert.Null(BlockSet.Of(Path.Combine(temp.Path, "crate_side.png")));
        Picture(Path.Combine(temp.Path, "crate_top.png"), _ => (1, 1, 1));
        Assert.Equal("crate", BlockSet.Of(Path.Combine(temp.Path, "crate_top.png"))?.Name);
        Assert.True(ModelProps.IsModel(Path.Combine(temp.Path, "crate_side.png")));
        Assert.False(ModelProps.IsModel(Path.Combine(temp.Path, "wall.png")));
    }

    [Fact]
    public void EachSideShowsItsPictureTheRightWayRound()
    {
        // The side: red on its left half, blue on its right; the top green; a front of its own, white.
        using var temp = new TempFolder();
        Picture(Path.Combine(temp.Path, "pumpkin_side.png"), x => x < 4 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));
        Picture(Path.Combine(temp.Path, "pumpkin_top.png"), _ => (0, 255, 0));
        Picture(Path.Combine(temp.Path, "pumpkin_front.png"), _ => (255, 255, 255));
        var mesh = BlockSet.Read(Path.Combine(temp.Path, "pumpkin_top.png"));
        Assert.Equal((-0.5f, -0.5f, 0f), mesh.Min);
        Assert.Equal((0.5f, 0.5f, 1f), mesh.Max);

        static (byte R, byte G, byte B) At(RgbaImage i, double across, double down) { var (r, g, b, _) = i.Get((int)(i.Width * across), (int)(i.Height * down)); return (r, g, b); }
        static bool Red((byte R, byte G, byte B) c) => c.R > 0 && c.B == 0 && c.G == 0;
        static bool Blue((byte R, byte G, byte B) c) => c.B > 0 && c.R == 0 && c.G == 0;
        // Rotation 1 sees the front: white. Rotations 3, 5 and 7 see sides: red on the viewer's left, blue on the right.
        var front = MeshBaker.Bake(mesh, 32, (0, 0, 0), 1, 1);
        Assert.True(At(front, 0.5, 0.5) is { R: > 0, G: > 0, B: > 0 });
        foreach (var rotation in new[] { 3, 5, 7 })
        {
            var side = MeshBaker.Bake(mesh, 32, (0, 0, 0), rotation, 1);
            Assert.True(Red(At(side, 0.2, 0.5)) && Blue(At(side, 0.8, 0.5)), $"rotation {rotation}: left {At(side, 0.2, 0.5)}, right {At(side, 0.8, 0.5)}");
        }
    }
}
