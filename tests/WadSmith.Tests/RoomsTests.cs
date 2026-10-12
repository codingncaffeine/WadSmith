using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Rooms as wholes: joined into one, and an island made a pillar, a pool or a platform.</summary>
public sealed class RoomsTests
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static MapGeometry Shapes(EditorMap map) => MapGeometry.Build(map.Frame);

    private static IReadOnlyList<string> Join(EditorMap map, History history, params Sector[] rooms)
    {
        IReadOnlyList<string> said = [];
        history.Record("Join rooms", e => said = Rooms.Join(e, Shapes(map), rooms));
        return said;
    }

    private static int Count<T>(IEnumerable<T> elements)
        where T : Element => elements.Count(e => e.OnMap);

    [Fact]
    public void TwoRoomsSideBySideBecomeOneWithStraightWalls()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        history.Record("Raise", e => e.Set(rooms[0], r => r.FloorHeight, (r, v) => r.FloorHeight = v, 8.0));
        var before = Udmf(map.ToModel());
        Assert.Empty(Join(map, history, rooms[0], rooms[1]));
        DrawingTests.AssertSound(map);
        Assert.Same(rooms[0], Assert.Single(map.Sectors, s => s.OnMap));
        Assert.Equal(8, rooms[0].FloorHeight);

        // The wall between them went, and the corners it left in the middle of the long walls with it.
        Assert.Equal(4, Count(map.Lines));
        Assert.Equal(4, Count(map.Vertices));
        Assert.Same(rooms[0], Shapes(map).SectorAt(new MapPoint(200, 64)));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void ThreeRoomsInARowAndRoomsOfDifferentSizesJoin()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128));
        Join(map, history, rooms[1], rooms[0], rooms[2]);
        DrawingTests.AssertSound(map);
        Assert.Same(rooms[1], Assert.Single(map.Sectors, s => s.OnMap));
        Assert.Equal(4, Count(map.Lines));

        // A narrow room against a wide one: the corners where its walls meet the wide one's stay.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 32, 256, 96));
        Join(map, history, rooms[0], rooms[1]);
        DrawingTests.AssertSound(map);
        Assert.Single(map.Sectors, s => s.OnMap);
        Assert.Equal(8, Count(map.Lines));
        Assert.Same(rooms[0], Shapes(map).SectorAt(new MapPoint(200, 64)));
    }

    [Fact]
    public void RoomsApartBecomeOneAreaInTwoPlacesAndSaySo()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (256, 0, 384, 128));
        var said = Assert.Single(Join(map, history, rooms[0], rooms[1]));
        Assert.Contains("does not touch", said);
        DrawingTests.AssertSound(map);
        Assert.Equal(8, Count(map.Lines));
        Assert.Same(rooms[0], Shapes(map).SectorAt(new MapPoint(320, 64)));
    }

    [Fact]
    public void AnIslandBecomesAPillarAPoolOrAPlatform()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (64, 64, 128, 128));
        Assert.Same(rooms[0], Rooms.Island(Shapes(map), rooms[1]));
        Assert.Null(Rooms.Island(Shapes(map), rooms[0]));

        history.Record("Make a pool", e => Rooms.Pool(e, Shapes(map), rooms[1], rooms[0], Look));
        Assert.Equal((-16.0, Look.Water), (rooms[1].FloorHeight, rooms[1].FloorTexture));
        var edges = map.Lines.Where(l => l.OnMap && l.Back is not null).ToList();
        Assert.Equal(4, edges.Count);
        Assert.All(edges, l => Assert.Equal(Look.Wall, (l.Front.Sector == rooms[1] ? l.Front : l.Back!).Bottom));
        history.Undo();

        history.Record("Make a platform", e => Rooms.Platform(e, Shapes(map), rooms[1], rooms[0], Look));
        Assert.Equal(24, rooms[1].FloorHeight);
        Assert.All(edges, l => Assert.Equal(Look.Step, (l.Front.Sector == rooms[0] ? l.Front : l.Back!).Bottom));
        history.Undo();

        // A pillar: the island goes, its walls solid toward the room round it.
        history.Record("Make a pillar", e => Deleting.Delete(e, Shapes(map), [rooms[1]], Look));
        DrawingTests.AssertSound(map);
        Assert.False(rooms[1].OnMap);
        Assert.Null(Shapes(map).SectorAt(new MapPoint(96, 96)));
        Assert.Equal(8, map.Lines.Count(l => l.OnMap && l.Back is null && l.Front.Sector == rooms[0]));

        // A room against the outer wall is not an island.
        (map, _, rooms) = MovingTests.Drawn((0, 0, 256, 256), (0, 0, 64, 64));
        Assert.Null(Rooms.Island(Shapes(map), rooms[1]));
    }
}
