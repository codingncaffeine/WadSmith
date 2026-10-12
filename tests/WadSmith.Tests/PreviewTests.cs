using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Preview;

namespace WadSmith.Tests;

public sealed class PreviewTests
{
    private static string Golden(string name)
    {
        // The goldens sit beside the test sources; walk up from the test binary to find them.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "goldens"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "goldens", name);
    }

    [Fact]
    public void TheFirstFreedoomMapDrawsExactlyAsTheGolden()
    {
        var wad = WadContainer.Open(GameFiles.Require("freedoom1.wad"));
        var map = MapConversion.Read(MapLocator.Find(wad).First(m => m.Name == "E1M1"));
        var drawn = MapPreview.Render(map, new MapPreview.Options(512));
        var golden = ImageCodec.Decode(File.ReadAllBytes(Golden("freedoom1-E1M1-512.png")));
        Assert.Equal((golden.Width, golden.Height), (drawn.Width, drawn.Height));
        var differing = 0;
        for (var i = 0; i < drawn.Pixels.Length; i += 4)
        {
            if (drawn.Pixels[i] != golden.Pixels[i] || drawn.Pixels[i + 1] != golden.Pixels[i + 1] || drawn.Pixels[i + 2] != golden.Pixels[i + 2]) differing++;
        }

        Assert.True(differing == 0, $"{differing} pixels differ from the golden image");
    }

    [Fact]
    public void AnEmptyMapIsABlankPicture()
    {
        var img = MapPreview.Render(new MapModel { Name = "MAP01", Namespace = "zdoom" }, new MapPreview.Options(64));
        Assert.All(Enumerable.Range(0, 64 * 64), i => Assert.Equal(0, img.Pixels[i * 4]));
    }

    [Fact]
    public void ASolidWallIsRedAndAFloorStepIsBrown()
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        m.Vertices.AddRange([new MapVertex { X = 0, Y = 0 }, new MapVertex { X = 100, Y = 0 }, new MapVertex { X = 0, Y = 100 }, new MapVertex { X = 100, Y = 100 }]);
        m.Sectors.AddRange([new MapSector { FloorHeight = 0, CeilingHeight = 128 }, new MapSector { FloorHeight = 24, CeilingHeight = 128 }]);
        m.Sides.AddRange([new MapSide { Sector = 0 }, new MapSide { Sector = 0 }, new MapSide { Sector = 1 }]);
        m.Lines.Add(new MapLine { V1 = 0, V2 = 1, Front = 0 });            // bottom: one-sided
        m.Lines.Add(new MapLine { V1 = 2, V2 = 3, Front = 1, Back = 2 });  // top: floor step
        var img = MapPreview.Render(m, new MapPreview.Options(128, Margin: 8));
        // Down column 64: the step (top of the map) comes first, the solid wall (bottom) last.
        int Row(SkiaSharp.SKColor c) => Enumerable.Range(0, 128).FirstOrDefault(y => img.Get(64, y) is var p && p.R == c.Red && p.G == c.Green && p.B == c.Blue, -1);
        var brown = Row(MapPreview.FloorChange);
        var red = Row(MapPreview.Wall);
        Assert.InRange(brown, 6, 10);
        Assert.InRange(red, 118, 122);
    }
}
