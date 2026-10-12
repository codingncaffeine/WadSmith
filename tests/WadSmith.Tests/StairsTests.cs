using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Stairs: a rectangle dragged from the bottom to the top becomes steps that join the floors at its two ends.</summary>
public sealed class StairsTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static (IReadOnlyList<Sector> Steps, string? Why) Make(EditorMap map, History history, MapPoint from, MapPoint to)
    {
        (IReadOnlyList<Sector>, string?) result = ([], null);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make stairs", e => result = Stairs.Make(e, geometry, from, to, Look.For("doom")));
        return result;
    }

    /// <summary>A low room (floor 0) and a high room (floor 64) with a gap of 256 between them.</summary>
    private static (EditorMap Map, History History, Sector Low, Sector High) Rooms()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (512, 0, 768, 256));
        history.Record("Raise", e =>
        {
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64);
            e.Set(rooms[1], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 192);
        });
        return (map, history, rooms[0], rooms[1]);
    }

    [Fact]
    public void StairsBetweenTwoRoomsClimbFromOneFloorToTheOther()
    {
        var (map, history, low, high) = Rooms();
        var before = Udmf(map.ToModel());
        var (steps, why) = Make(map, history, new MapPoint(256, 64), new MapPoint(512, 192));
        Assert.Null(why);
        DrawingTests.AssertSound(map);

        // 64 up in risers of 16: three steps between the rooms.
        Assert.Equal([16.0, 32.0, 48.0], steps.Select(s => s.FloorHeight).Order());
        Assert.All(steps, s => Assert.True(s.CeilingHeight - s.FloorHeight >= Stairs.Headroom));
        var g = MapGeometry.Build(map.Frame);
        Assert.Equal(16, g.SectorAt(new MapPoint(290, 128))!.FloorHeight);
        Assert.Equal(48, g.SectorAt(new MapPoint(480, 128))!.FloorHeight);

        // The first step shares a wall with the low room, the last with the high one; the risers show the step texture.
        Assert.Contains(map.Lines, l => l.OnMap && l.Back is not null && new[] { l.Front.Sector, l.Back.Sector }.Contains(low) && steps.Contains(l.Front.Sector == low ? l.Back.Sector : l.Front.Sector));
        Assert.Contains(map.Lines, l => l.OnMap && l.Back is not null && new[] { l.Front.Sector, l.Back.Sector }.Contains(high));
        Assert.Contains(map.Lines, l => l.OnMap && l.Back is not null && steps.Contains(l.Front.Sector) && steps.Contains(l.Back.Sector) && l.Front.Bottom == "STEP6");

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void StairsInTheOpenClimbSixteenAStep()
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var history = new History(map);
        var (steps, why) = Make(map, history, new MapPoint(0, 0), new MapPoint(64, 256));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => 16.0 * i), steps.Select(s => s.FloorHeight).Order());
    }

    [Fact]
    public void StairsTooSteepForThePlayerAreRefused()
    {
        var (map, history, _, high) = Rooms();
        history.Record("Raise", e => e.Set(high, s => s.FloorHeight, (s, v) => s.FloorHeight = v, 400));
        var (steps, why) = Make(map, history, new MapPoint(448, 96), new MapPoint(512, 144));
        Assert.Empty(steps);
        Assert.Contains("more than the player can climb", why);
    }

    [Fact]
    public void StairsAlongARoomsWallSplitItAtEveryStep()
    {
        // Steps running north beside the low room: each step's edge starts on the room's east wall.
        var (map, history, low, _) = Rooms();
        var (steps, why) = Make(map, history, new MapPoint(256, 64), new MapPoint(320, 192));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
        Assert.Equal(4, steps.Count);
        Assert.Equal(4, map.Lines.Count(l => l.OnMap && l.Back is not null && (l.Front.Sector == low || l.Back.Sector == low)));
    }

    [Fact]
    public void StairsDraggedFromTheHighRoomGoDown()
    {
        var (map, history, _, _) = Rooms();
        var (steps, _) = Make(map, history, new MapPoint(512, 64), new MapPoint(256, 192));
        Assert.Equal([16.0, 32.0, 48.0], steps.Select(s => s.FloorHeight).Order());
        var g = MapGeometry.Build(map.Frame);
        Assert.Equal(48, g.SectorAt(new MapPoint(480, 128))!.FloorHeight);
    }
}
