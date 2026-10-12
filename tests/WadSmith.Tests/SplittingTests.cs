using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Splitting a room along a line drawn across it, from one of its walls to another.</summary>
public sealed class SplittingTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static MapGeometry Shapes(EditorMap map) => MapGeometry.Build(map.Frame);

    private static (IReadOnlyList<Sector> Parts, string? Why) Split(EditorMap map, History history, params (double X, double Y)[] path)
    {
        (IReadOnlyList<Sector>, string?) result = ([], null);
        history.Record("Split a room", e => result = Splitting.Split(e, Shapes(map), [.. path.Select(p => new MapPoint(p.X, p.Y))]));
        return result;
    }

    [Fact]
    public void AStraightLineAcrossARoomMakesTwoAndTheSmallerIsTheNewOne()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256));
        history.Record("Settings", e =>
        {
            e.Set(rooms[0], s => s.Tag, (s, v) => s.Tag = v, 5);
            e.Set(rooms[0], s => s.Light, (s, v) => s.Light = v, 144);
        });
        var before = Udmf(map.ToModel());
        var (parts, why) = Split(map, history, (0, 64), (256, 64));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
        Assert.Same(rooms[0], parts[0]);
        var part = parts[1];
        Assert.Same(part, Shapes(map).SectorAt(new MapPoint(128, 32)));
        Assert.Same(rooms[0], Shapes(map).SectorAt(new MapPoint(128, 200)));
        Assert.Equal((5, 144), (part.Tag, part.Light));
        Assert.Equal(7, map.Lines.Count(l => l.OnMap));
        var cut = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        Assert.Equal(new HashSet<Sector> { rooms[0], part }, new HashSet<Sector> { cut.Front.Sector, cut.Back!.Sector });

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void ABentLineACornerToCornerLineAndALineBackToTheSameWallSplitToo()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        var (parts, _) = Split(map, history, (0, 128), (128, 128), (128, 0));
        DrawingTests.AssertSound(map);
        Assert.Same(parts[1], Shapes(map).SectorAt(new MapPoint(64, 64)));
        Assert.Same(parts[0], Shapes(map).SectorAt(new MapPoint(200, 200)));

        (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        (parts, _) = Split(map, history, (0, 0), (256, 256));
        Assert.Equal(2, parts.Count);
        DrawingTests.AssertSound(map);
        Assert.Equal(5, map.Lines.Count(l => l.OnMap));

        // Out from the west wall and back to it: a piece 64 deep is cut off.
        (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        (parts, _) = Split(map, history, (0, 64), (64, 64), (64, 192), (0, 192));
        DrawingTests.AssertSound(map);
        Assert.Same(parts[1], Shapes(map).SectorAt(new MapPoint(32, 128)));
        Assert.Same(parts[0], Shapes(map).SectorAt(new MapPoint(32, 16)));
        Assert.Same(parts[0], Shapes(map).SectorAt(new MapPoint(160, 128)));
    }

    [Fact]
    public void ThePartRoundAPillarTakesThePillarsWallsWithIt()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 512, 256), (64, 96, 128, 160));
        history.Record("Pillar", e => Deleting.Delete(e, Shapes(map), [rooms[1]], Editor.Look.For("doom")));
        var (parts, _) = Split(map, history, (256, 0), (256, 256));
        DrawingTests.AssertSound(map);
        var west = Shapes(map).SectorAt(new MapPoint(32, 32))!;
        Assert.Contains(west, parts);
        Assert.All(map.Lines.Where(l => l.OnMap && l.Start.X is 64 or 128 && l.End.X is 64 or 128), l => Assert.Same(west, l.Front.Sector));
    }

    [Fact]
    public void NoSplitForALineThatStopsInsideLeavesTheRoomOrRunsToAPillar()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (512, 0, 768, 256), (96, 96, 160, 160));
        var before = Udmf(map.ToModel());
        Assert.Null(Splitting.Across(Shapes(map), [new(0, 64), new(128, 64)]));
        Assert.Null(Splitting.Across(Shapes(map), [new(0, 32), new(384, 32), new(512, 32)]));
        Assert.Null(Splitting.Across(Shapes(map), [new(0, 32), new(256, 32), new(256, 64), new(0, 64)]));
        history.Record("Pillar", e => Deleting.Delete(e, Shapes(map), [rooms[2]], Editor.Look.For("doom")));
        var (parts, why) = Split(map, history, (0, 128), (96, 128));
        Assert.Empty(parts);
        Assert.Contains("pillar", why);
        Assert.NotEqual(before, Udmf(map.ToModel()));

        // Straight through a pillar near one end: the line's middle is in the room, its way is not.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (16, 96, 48, 160));
        history.Record("Pillar", e => Deleting.Delete(e, Shapes(map), [rooms[1]], Editor.Look.For("doom")));
        Assert.Null(Splitting.Across(Shapes(map), [new(0, 128), new(256, 128)]));
        Assert.NotNull(Splitting.Across(Shapes(map), [new(0, 200), new(256, 200)]));
    }
}
