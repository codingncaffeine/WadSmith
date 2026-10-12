using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Round rooms: corners on the ellipse in the dragged box, drawn as rooms like any shape.</summary>
public sealed class ShapesTests
{
    private static double Area(IReadOnlyList<MapPoint> p)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Count; i++) sum += p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y;
        return Math.Abs(sum) / 2;
    }

    [Fact]
    public void ARoundRoomFillsItsBoxWithFlatTopAndBottom()
    {
        var circle = Shapes.Ellipse(new MapPoint(0, 0), new MapPoint(256, 256), 16);
        Assert.Equal(16, circle.Count);
        Assert.All(circle, p => Assert.InRange(p.X, 0, 256));
        Assert.All(circle, p => Assert.InRange(p.Y, 0, 256));
        Assert.All(circle, p => Assert.Equal(Math.Round(p.X), p.X));

        // Close to a circle's area, and a flat side along the bottom and the top.
        Assert.InRange(Area(circle), Math.PI * 128 * 128 * 0.95, Math.PI * 128 * 128);
        Assert.Equal(2, circle.Count(p => p.Y == circle.Min(q => q.Y)));
        Assert.Equal(2, circle.Count(p => p.Y == circle.Max(q => q.Y)));

        // An oval in a wide box, and as few as three sides; a tiny one keeps no corner twice.
        var oval = Shapes.Ellipse(new MapPoint(-256, 0), new MapPoint(256, 128), 24);
        Assert.InRange(oval.Max(p => p.X) - oval.Min(p => p.X), 500, 512);
        Assert.InRange(oval.Max(p => p.Y) - oval.Min(p => p.Y), 126, 128);
        Assert.Equal(3, Shapes.Ellipse(new MapPoint(0, 0), new MapPoint(64, 64), 1).Count);
        var tiny = Shapes.Ellipse(new MapPoint(0, 0), new MapPoint(2, 2), 64);
        Assert.True(tiny.Count >= 3 && tiny.Zip(tiny.Skip(1)).All(z => z.First.DistanceTo(z.Second) > 0.5));
    }

    [Fact]
    public void ARoundRoomIsDrawnAndJoinsTheRoomItOverlaps()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        IReadOnlyList<Sector> made = [];
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Draw a room", e => made = ShapeDrawing.Draw(e, geometry, Shapes.Ellipse(new MapPoint(128, 64), new MapPoint(384, 192), 16), Editor.Look.For("doom")));
        DrawingTests.AssertSound(map);
        // Two new areas: the part over the room and the part beyond it, sharing the room's wall.
        Assert.Equal(2, made.Count);
        var shapes = MapGeometry.Build(map.Frame);
        Assert.Contains(shapes.SectorAt(new MapPoint(330, 128)), made);
        Assert.Contains(shapes.SectorAt(new MapPoint(200, 128)), made);
        Assert.NotNull(shapes.SectorAt(new MapPoint(64, 32)));
        Assert.DoesNotContain(shapes.SectorAt(new MapPoint(64, 32)), made);
    }
}
