using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Rounded corners: a corner where two walls meet becomes a curve touching both, the map staying whole.</summary>
public sealed class RoundingTests
{
    private static (EditorMap Map, History History, Vertex Corner) Square()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        return (map, history, map.Vertices.Single(v => v.OnMap && v.X == 256 && v.Y == 256));
    }

    private static IReadOnlyList<MapPoint> Outline(EditorMap map) => MapGeometry.Build(map.Frame).Loops(map.Sectors.First(s => s.OnMap))[0];

    [Fact]
    public void ACornerBecomesACurveTouchingBothWalls()
    {
        var (map, history, corner) = Square();
        string? why = "";
        history.Record("Round", e => why = Rounding.Round(e, MapGeometry.Build(map.Frame), corner, 64, 4));
        Assert.Null(why);
        DrawingTests.AssertSound(map);

        // 4 corners, one replaced by the curve's 5 points.
        var outline = Outline(map);
        Assert.Equal(8, outline.Count);
        Assert.Contains(new MapPoint(256, 192), outline);
        Assert.Contains(new MapPoint(192, 256), outline);

        // Every point of the curve is about 64 from the circle's middle at (192, 192), whole units.
        var curve = outline.Where(p => p.X > 192 && p.Y > 192 || p == new MapPoint(256, 192) || p == new MapPoint(192, 256)).ToList();
        Assert.Equal(5, curve.Count);
        Assert.All(curve, p => Assert.InRange(p.DistanceTo(new MapPoint(192, 192)), 63.3, 64.7));
        Assert.DoesNotContain(new MapPoint(256, 256), outline);

        history.Undo();
        Assert.Equal(4, Outline(map).Count);
    }

    [Fact]
    public void ACornerOfTwoRoomsRoundsBothAndTheyStillShareTheCurve()
    {
        // Two rooms side by side: the shared wall's top end has three walls (refused); a lone corner of the right room rounds.
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var shared = map.Vertices.Single(v => v.OnMap && v.X == 256 && v.Y == 256);
        string? why = null;
        history.Record("Round", e => why = Rounding.Round(e, MapGeometry.Build(map.Frame), shared, 32, 4));
        Assert.Contains("two walls", why);

        var outer = map.Vertices.Single(v => v.OnMap && v.X == 512 && v.Y == 0);
        history.Record("Round", e => why = Rounding.Round(e, MapGeometry.Build(map.Frame), outer, 96, 8));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void TooFarAlongTheWallsOrSomethingInTheWayIsRefused()
    {
        var (map, history, corner) = Square();
        string? why = null;
        history.Record("Round", e => why = Rounding.Round(e, MapGeometry.Build(map.Frame), corner, 300, 4));
        Assert.Contains("only 256 long", why);

        // A pillar room drawn in the corner's way.
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Pillar", e => ShapeDrawing.Draw(e, geometry, [new(232, 232), new(232, 248), new(248, 248), new(248, 232)], Editor.Look.For("doom")));
        history.Record("Round", e => why = Rounding.Round(e, MapGeometry.Build(map.Frame), corner, 64, 4));
        Assert.Contains("in the corner", why);
    }

    [Fact]
    public void TheDefaultReachFitsShortWalls()
    {
        var (map, _, _) = MovingTests.Drawn((0, 0, 100, 40));
        var corner = map.Vertices.Single(v => v.OnMap && v.X == 100 && v.Y == 40);
        Assert.Equal(16, Rounding.DefaultReach(MapGeometry.Build(map.Frame), corner));
        var (big, _, bigCorner) = Square();
        Assert.Equal(64, Rounding.DefaultReach(MapGeometry.Build(big.Frame), bigCorner));
    }
}
