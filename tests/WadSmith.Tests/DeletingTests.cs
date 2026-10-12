using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Deleting so the map stays whole: rooms, walls, corners and objects.</summary>
public sealed class DeletingTests
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static List<Line> WallList(EditorMap map) => [.. map.Lines.Where(l => l.OnMap)];

    /// <summary>Deletes, checks the map is sound, and that undo gives it back exactly; returns what stayed and why.</summary>
    private static IReadOnlyList<string> Delete(EditorMap map, History history, params Element[] selection)
    {
        var before = Udmf(map.ToModel());
        IReadOnlyList<string> said = [];
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Delete", e => said = Deleting.Delete(e, geometry, selection, Look));
        DrawingTests.AssertSound(map);
        var after = Udmf(map.ToModel());
        if (history.UndoLabel == "Delete" && before != after)
        {
            history.Undo();
            Assert.Equal(before, Udmf(map.ToModel()));
            history.Redo();
            Assert.Equal(after, Udmf(map.ToModel()));
        }

        return said;
    }

    [Fact]
    public void ARoomOnItsOwnGoesWithItsWallsCornersAndThings()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (512, 0, 640, 128));
        var t = new Thing { X = 64, Y = 64, Type = 3004 };
        history.Record("Add", e => e.Add(t));
        Assert.Empty(Delete(map, history, rooms[0]));
        Assert.False(t.OnMap);
        Assert.Equal(4, WallList(map).Count);
        Assert.Equal(4, map.Vertices.Count(v => v.OnMap));
        Assert.Single(map.Sectors, s => s.OnMap);
    }

    [Fact]
    public void ARoomBesideAnotherLeavesTheSharedWallAsTheNeighboursSolidWall()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        foreach (var gone in rooms.ToList())
        {
            var (m, h, r) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
            var deleted = r[rooms.IndexOf(gone)];
            var stays = r.Single(s => s != deleted);
            Assert.Empty(Delete(m, h, deleted));
            Assert.Equal(4, WallList(m).Count);
            Assert.All(WallList(m), l => Assert.True(l.Back is null && l.Front.Sector == stays && l.Flag("blocking") && l.Front.Middle == "STARTAN3"));
        }
    }

    [Fact]
    public void ARoomInsideAnotherLeavesASolidPillar()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 512, 512), (192, 192, 320, 320));
        Assert.Empty(Delete(map, history, rooms[1]));
        Assert.Equal(8, WallList(map).Count);
        Assert.Null(MapGeometry.Build(map.Frame).SectorAt(new MapPoint(256, 256)));
        Assert.Same(rooms[0], MapGeometry.Build(map.Frame).SectorAt(new MapPoint(100, 100)));
    }

    [Fact]
    public void AWallBetweenTwoRoomsGoesAndTheyBecomeOne()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var shared = WallList(map).Single(l => l.Back is not null);
        var keep = shared.Front.Sector;
        Assert.Empty(Delete(map, history, shared));
        Assert.Same(keep, Assert.Single(map.Sectors, s => s.OnMap));
        Assert.Equal(6, WallList(map).Count);
        Assert.Same(keep, MapGeometry.Build(map.Frame).SectorAt(new MapPoint(64, 64)));
        Assert.Same(keep, MapGeometry.Build(map.Frame).SectorAt(new MapPoint(192, 64)));
    }

    [Fact]
    public void OnePieceOfAWallBetweenTwoRoomsTakesTheRestOfItWhenTheyBecomeOne()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var shared = WallList(map).Single(l => l.Back is not null);
        history.Record("Add a corner", e => Walls.Split(e, shared, e.Add(new Vertex { X = 128, Y = 64 })));
        Assert.Equal(2, WallList(map).Count(l => l.Back is not null));
        Assert.Empty(Delete(map, history, shared));
        Assert.Single(map.Sectors, s => s.OnMap);
        Assert.DoesNotContain(WallList(map), l => l.Back is not null);
        Assert.Equal(6, WallList(map).Count);
    }

    [Fact]
    public void ADeletedFaceDeletesItsWall()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        Assert.Empty(Delete(map, history, WallList(map).Single(l => l.Back is not null).Back!));
        Assert.Single(map.Sectors, s => s.OnMap);
    }

    [Fact]
    public void AWallWithARoomOnOneSideStays()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128));
        var before = Udmf(map.ToModel());
        var said = Delete(map, history, WallList(map)[0]);
        Assert.Contains("would leave the room open", Assert.Single(said));
        Assert.Equal(before, Udmf(map.ToModel()));
        Assert.Equal("Draw a room", history.UndoLabel);
    }

    [Fact]
    public void ACornerInTheMiddleOfAWallGoesAndTheWallIsOneAgain()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128));
        var west = WallList(map).Single(l => l.Start.X == 0 && l.End.X == 0);
        var mid = new Vertex { X = 0, Y = 64 };
        history.Record("Add a corner", e => Walls.Split(e, west, e.Add(mid)));
        Assert.Equal(5, WallList(map).Count);
        Assert.Empty(Delete(map, history, mid));
        Assert.Equal(4, WallList(map).Count);
        Assert.Equal(4, map.Vertices.Count(v => v.OnMap));
    }

    [Fact]
    public void TwoCornersSideBySideGoTogether()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 192));
        var west = WallList(map).Single(l => l.Start.X == 0 && l.End.X == 0);
        var (a, b) = (new Vertex { X = 0, Y = 64 }, new Vertex { X = 0, Y = 128 });
        history.Record("Add corners", e =>
        {
            var piece = Walls.Split(e, west, e.Add(west.Start.Y < west.End.Y ? a : b));
            var rest = west.Start.Y < west.End.Y ? piece : west;
            Walls.Split(e, rest, e.Add(west.Start.Y < west.End.Y ? b : a));
        });
        Assert.Equal(6, WallList(map).Count);
        Assert.Empty(Delete(map, history, a, b));
        Assert.Equal(4, WallList(map).Count);
    }

    [Fact]
    public void ARoomsCornerGoesAndTheRoomBecomesATriangle()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128));
        var corner = map.Vertices.Single(v => v.OnMap && v.X == 128 && v.Y == 128);
        Assert.Empty(Delete(map, history, corner));
        Assert.Equal(3, WallList(map).Count);
        Assert.Same(rooms[0], MapGeometry.Build(map.Frame).SectorAt(new MapPoint(32, 32)));
    }

    [Fact]
    public void ACornerWhereThreeWallsMeetStays()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var corner = map.Vertices.Single(v => v.OnMap && v.X == 128 && v.Y == 128);
        Assert.Contains("three or more walls", Assert.Single(Delete(map, history, corner)));
        Assert.True(corner.OnMap);
    }

    [Fact]
    public void AThingGoesAndComesBackWithUndo()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128));
        var t = new Thing { X = 64, Y = 64, Type = 1 };
        history.Record("Add", e => e.Add(t));
        Assert.Empty(Delete(map, history, t));
        Assert.False(t.OnMap);
        history.Undo();
        Assert.True(t.OnMap);
    }
}
