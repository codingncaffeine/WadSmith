using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A shape's corner that lands a fraction of a unit off an existing wall or corner (a click snapped to a slanted wall,
/// then rounded) is on it: the wall is split there and the stretch the two rooms share becomes one wall, not two lying
/// along each other. Found on a first map (2026-10-12): a triangle drawn against a slanted wall shared nothing, so no door
/// could go between the rooms.
/// </summary>
public sealed class SnappedDrawingTests
{
    private static (EditorMap Map, History History) Fresh()
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        return (map, new History(map));
    }

    private static IReadOnlyList<Sector> Draw(EditorMap map, History history, params MapPoint[] shape)
    {
        IReadOnlyList<Sector> made = [];
        history.Record("Draw a room", e => made = ShapeDrawing.Draw(e, MapGeometry.Build(map.Frame), shape, Look.For("doom")));
        return made;
    }

    [Fact]
    public void ATriangleDrawnAlongPartOfASlantedWallSharesThatStretchAndTakesADoor()
    {
        var (map, history) = Fresh();
        // The first map's rooms, as drawn: the second room's corner at (305, 24) lies 0.16 off the first room's slanted wall.
        Draw(map, history, new(-512, -352), new(-256, 256), new(256, 256), new(384, -352));
        Draw(map, history, new(672, -32), new(384, -352), new(305, 24));
        DrawingTests.AssertSound(map);

        Assert.Equal(2, map.Sectors.Count(s => s.OnMap));
        Assert.Equal(7, map.Lines.Count(l => l.OnMap));
        var shared = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        Assert.NotSame(shared.Front.Sector, shared.Back!.Sector);

        // The corner went onto the wall, where the point projects, so the shared stretch is exactly on the old wall's line.
        var corner = shared.Start.Y > -300 ? shared.Start : shared.End;
        Assert.Equal((304.85, 23.97), (Math.Round(corner.X, 2), Math.Round(corner.Y, 2)));
        var off = Math.Abs((corner.X - 256) * (-352 - 256) - (corner.Y - 256) * (384 - 256)) / Math.Sqrt(128 * 128 + 608 * 608);
        Assert.True(off < 0.001, $"the corner is {off:0.###} off the wall");

        // And a door goes between the rooms there.
        var geometry = MapGeometry.Build(map.Frame);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, geometry, shared, Look.For("doom"), shared.Length > Doors.MaxWidth ? Doors.Centred(shared) : null));
        Assert.Null(made.Why);
        Assert.NotNull(made.Door);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ACornerWithinAUnitOfAnExistingCornerIsThatCorner()
    {
        var (map, history) = Fresh();
        Draw(map, history, new(0, 0), new(0, 256), new(256, 256), new(256, 0));
        Draw(map, history, new(256.4, 0.3), new(256, 256.6), new(512, 256), new(512, 0));
        DrawingTests.AssertSound(map);
        Assert.Equal(6, map.Vertices.Count(v => v.OnMap));
        Assert.Equal(7, map.Lines.Count(l => l.OnMap));
        Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
    }
}
