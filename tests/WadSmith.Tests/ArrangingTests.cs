using WadSmith.Core.Maps;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Lining up, spacing out, rings and grids of objects: one undo step each, whole units.</summary>
public sealed class ArrangingTests
{
    private static (EditorMap Map, History History, List<Thing> Things) With(params (double X, double Y)[] spots)
    {
        var map = EditorMap.FromModel(StarterRoomWithout());
        var history = new History(map);
        var things = new List<Thing>();
        history.Record("Place", e =>
        {
            foreach (var (x, y) in spots) things.Add(e.Add(new Thing { X = x, Y = y, Type = 3001, Angle = 90 }));
        });
        return (map, history, things);

        static MapModel StarterRoomWithout() => Editor.StarterRoom.Create("MAP01", "doom");
    }

    [Fact]
    public void LiningUpMovesEachOnOneLineOnly()
    {
        var (_, history, things) = With((10, 0), (50, 30), (90, -40));
        history.Record("Line", e => Arranging.Line(e, things, LineUp.Left));
        Assert.All(things, t => Assert.Equal(10, t.X));
        Assert.Equal([0.0, 30, -40], things.Select(t => t.Y));
        history.Record("Line", e => Arranging.Line(e, things, LineUp.Middle));
        Assert.All(things, t => Assert.Equal(-5, t.Y));
        history.Undo();
        history.Undo();
        Assert.Equal([10.0, 50, 90], things.Select(t => t.X));
    }

    [Fact]
    public void SpacingEvenlyKeepsTheEndsAndTheOrder()
    {
        var (_, history, things) = With((0, 0), (90, 5), (10, 9), (100, 0));
        history.Record("Spread", e => Arranging.Spread(e, things, across: true));
        Assert.Equal([0.0, 67, 33, 100], things.Select(t => t.X));
        Assert.Equal([0.0, 5, 9, 0], things.Select(t => t.Y));
    }

    [Theory]
    [InlineData(RingFacing.Inward, 180)]
    [InlineData(RingFacing.Outward, 0)]
    public void EightInARingStandEvenlyRoundTheMiddleFacingAsAsked(RingFacing facing, int turn)
    {
        var (_, history, things) = With((0, 0), (10, 40), (-30, 5), (60, 60), (5, -70), (80, 0), (-60, -60), (20, 20));
        var centre = new MapPoint(0, 0);
        history.Record("Ring", e => Arranging.Ring(e, things, centre, 128, facing));
        var bearings = things.Select(t => Math.Round(Math.Atan2(t.Y, t.X) * 180 / Math.PI)).Select(b => (b + 360) % 360).Order().ToList();
        Assert.Equal([0.0, 45, 90, 135, 180, 225, 270, 315], bearings);
        // Whole units: a diagonal spot lands up to 0.71 off the ring.
        Assert.All(things, t => Assert.InRange(Math.Sqrt(t.X * t.X + t.Y * t.Y), 127.25, 128.75));
        Assert.All(things, t => Assert.Equal(t.X, Math.Round(t.X)));
        Assert.All(things, t => Assert.Equal(((int)Math.Round(Math.Atan2(t.Y, t.X) * 180 / Math.PI) + turn + 720) % 360, t.Angle));
        Assert.Equal("Ring", history.UndoLabel);
    }

    [Fact]
    public void ARingKeepsTheOrderTheyStoodIn()
    {
        // North, west, south, east of the middle: east first, then going round counter-clockwise.
        var (_, history, things) = With((0, 100), (-100, 0), (0, -100), (100, 0));
        history.Record("Ring", e => Arranging.Ring(e, things, new MapPoint(0, 0), 50, RingFacing.AsTheyWere));
        Assert.Equal([(0.0, 50.0), (-50, 0), (0, -50), (50, 0)], things.Select(t => (t.X, t.Y)));
        Assert.All(things, t => Assert.Equal(90, t.Angle));
    }

    [Fact]
    public void ANewRingReachesAsFarAsTheyStandOnAverageAndLeavesRoomBetweenThem()
    {
        // Three near the middle and one far out: the ring goes through the average, not the far one.
        var (_, _, things) = With((-10, 0), (10, 0), (0, 10), (0, -300));
        var (_, radius, mean) = Arranging.Spread(things);
        Assert.True(radius > 150 && mean < radius);
        Assert.Equal(Math.Max(64, Math.Ceiling(mean / 8) * 8), Arranging.RingReach(things));

        // Twelve close together still leave 64 between neighbours: 32 × 12 / π ≈ 122, so 128.
        var (_, _, crowd) = With([.. Enumerable.Range(0, 12).Select(i => ((double)i, 0.0))]);
        Assert.Equal(128, Arranging.RingReach(crowd));
    }

    [Fact]
    public void AGridFillsRowsFromTheTopLeftInReadingOrder()
    {
        var (_, history, things) = With((0, 100), (40, 100), (80, 90), (0, 0), (50, 10));
        history.Record("Grid", e => Arranging.Grid(e, things, 3, 64));
        Assert.Equal([(0.0, 100.0), (64, 100), (128, 100), (0, 36), (64, 36)], things.Select(t => (t.X, t.Y)));
    }
}
