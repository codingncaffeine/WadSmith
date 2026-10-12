using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>A box over the plan picks the biggest whole things inside it.</summary>
public sealed class BoxSelectionTests
{
    private static IReadOnlyList<Element> Pick(EditorMap map, double x0, double y0, double x1, double y1) => BoxSelection.Pick(MapGeometry.Build(map.Frame), x0, y0, x1, y1);

    private static (EditorMap Map, List<Sector> Rooms, Thing Thing) TwoRooms()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var thing = new Thing { X = 64, Y = 64, Type = 3004 };
        history.Record("Add", e => e.Add(thing));
        return (map, rooms, thing);
    }

    [Fact]
    public void ABoxRoundARoomPicksTheRoomAndWhatStandsInIt()
    {
        var (map, rooms, thing) = TwoRooms();
        Assert.Equal([rooms[0], thing], Pick(map, -8, -8, 136, 136));
        Assert.Equal([rooms[0], rooms[1], thing], Pick(map, -8, -8, 264, 136));
    }

    [Fact]
    public void ABoxRoundPartOfARoomPicksItsWallsAndThenItsCorners()
    {
        var (map, _, _) = TwoRooms();

        // Round the west wall: the wall, not its two corners again.
        var west = Assert.IsType<Line>(Assert.Single(Pick(map, -8, -8, 8, 136)));
        Assert.Equal((0.0, 0.0), (west.Start.X, west.End.X));

        // Round one corner only.
        var corner = Assert.IsType<Vertex>(Assert.Single(Pick(map, 120, 120, 136, 136)));
        Assert.Equal((128.0, 128.0), (corner.X, corner.Y));
    }

    [Fact]
    public void AnEmptyBoxPicksNothing()
    {
        var (map, _, _) = TwoRooms();
        Assert.Empty(Pick(map, 1000, 1000, 1100, 1100));
    }
}
