using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Turning, resizing and mirroring the selection: shapes stay whole, walls keep facing their rooms, objects keep facing.</summary>
public sealed class TransformTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static MoveSet Gather(EditorMap map, params Element[] selection) => MoveSet.Gather(MapGeometry.Build(map.Frame), MoveSet.Seeds.Of(selection));

    /// <summary>Places the gathered set as the canvas does (a live step, the last step, then the joins when let go), one gesture.</summary>
    private static int Place(History history, MoveSet set, Placement halfway, Placement last)
    {
        var gesture = History.NewGesture();
        var first = new TransformEdit(set, halfway);
        history.Run(first, gesture);
        history.Run(first.To(last), gesture);
        var crossings = 0;
        history.Record("Join", e => crossings = Joining.AfterMove(e, set, Editor.Look.For("doom")), gesture);
        return crossings;
    }

    private static Thing AddThing(EditorMap map, History history, double x, double y, int angle)
    {
        var t = new Thing { X = x, Y = y, Type = 3004, Angle = angle };
        history.Record("Place", e => e.Add(t));
        return t;
    }

    private static (double, double, double, double) Bounds(EditorMap map, Sector room)
    {
        var points = map.Lines.Where(l => l.OnMap && (l.Front.Sector == room || l.Back?.Sector == room)).SelectMany(l => new[] { l.Start, l.End }).ToList();
        return (points.Min(v => v.X), points.Min(v => v.Y), points.Max(v => v.X), points.Max(v => v.Y));
    }

    [Fact]
    public void PlacementsTurnMirrorAndResizeExactly()
    {
        var quarter = Placement.Turn(90, new MapPoint(0, 0));
        Assert.Equal(new MapPoint(0, 64), quarter.Apply(64, 0));
        Assert.Equal(new MapPoint(-64, 0), Placement.Turn(180, new MapPoint(0, 0)).Apply(64, 0));
        Assert.Equal(new MapPoint(32, -64), Placement.Turn(-90, new MapPoint(32, 32)).Apply(128, 32));
        Assert.Equal("Turn", quarter.Describe());
        Assert.Equal(90, quarter.Facing(0));
        Assert.Equal(0, quarter.Facing(270));
        Assert.Equal(45, Placement.Turn(45, new MapPoint(7, 9)).Facing(0));

        // A turn of a third of a circle lands on the game's precision, not a long fraction.
        var p = Placement.Turn(120, new MapPoint(0, 0)).Apply(64, 0);
        Assert.Equal(-32, p.X);
        Assert.Equal(Math.Round(p.Y * 65536) / 65536, p.Y);

        var mirror = Placement.Size(-1, 1, new MapPoint(100, 0));
        Assert.True(mirror.Mirrors);
        Assert.Equal("Mirror", mirror.Describe());
        Assert.Equal(new MapPoint(136, 5), mirror.Apply(64, 5));
        Assert.Equal(180, mirror.Facing(0));
        Assert.Equal(90, mirror.Facing(90));
        Assert.Equal(270, Placement.Size(1, -1, default).Facing(90));
        Assert.False(Placement.Size(-1, -1, default).Mirrors);

        var grow = Placement.Size(2, 1.5, new MapPoint(0, 0));
        Assert.Equal("Resize", grow.Describe());
        Assert.Equal(new MapPoint(128, 96), grow.Apply(64, 64));
        Assert.Equal(0, grow.Facing(0));
        Assert.True(Placement.Size(0, 1, default).Flattens);
    }

    [Fact]
    public void AQuarterTurnKeepsARoomWholeAndItsThingFacingTheSameWall()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 64));
        var imp = AddThing(map, history, 96, 32, 0);
        var before = Udmf(map.ToModel());
        var set = Gather(map, rooms[0]);
        Assert.Equal(new MapPoint(64, 32), set.Middle);
        Assert.Equal(0, Place(history, set, Placement.Turn(45, set.Middle), Placement.Turn(90, set.Middle)));

        DrawingTests.AssertSound(map);
        Assert.Equal((32.0, -32.0, 96.0, 96.0), Bounds(map, rooms[0]));
        Assert.Equal((64.0, 64.0, 90), (imp.X, imp.Y, imp.Angle));
        Assert.Same(rooms[0], MapGeometry.Build(map.Frame).SectorAt(new MapPoint(64, 90)));

        // The turn and its joins are one step, and undo puts back every corner, wall and angle.
        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
        history.Redo();
        Assert.Equal((64.0, 64.0, 90), (imp.X, imp.Y, imp.Angle));
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void MirroringTwoRoomsSwapsThemAndEveryWallStillFacesItsRoom()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var imp = AddThing(map, history, 32, 64, 0);
        var before = Udmf(map.ToModel());
        var set = Gather(map, rooms[0], rooms[1]);
        Assert.Equal(new MapPoint(128, 64), set.Middle);
        Place(history, set, Placement.Size(-0.5, 1, set.Middle), Placement.Size(-1, 1, set.Middle));

        DrawingTests.AssertSound(map);
        var shapes = MapGeometry.Build(map.Frame);
        Assert.Same(rooms[1], shapes.SectorAt(new MapPoint(64, 64)));
        Assert.Same(rooms[0], shapes.SectorAt(new MapPoint(192, 64)));
        Assert.Equal((224.0, 64.0, 180), (imp.X, imp.Y, imp.Angle));
        var shared = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        Assert.Equal(new HashSet<Sector>(rooms), new HashSet<Sector> { shared.Front.Sector, shared.Back!.Sector });

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
        history.Redo();
        DrawingTests.AssertSound(map);
        Assert.Same(rooms[0], MapGeometry.Build(map.Frame).SectorAt(new MapPoint(192, 64)));
    }

    [Fact]
    public void ADragThatMirrorsAndComesBackLeavesTheWallsAsTheyWere()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128));
        var set = Gather(map, rooms[0]);
        var ends = map.Lines.Where(l => l.OnMap).Select(l => (l.Start, l.End)).ToList();
        var gesture = History.NewGesture();
        var first = new TransformEdit(set, Placement.Size(-1, 1, new MapPoint(0, 0)));
        history.Run(first, gesture);
        history.Run(first.To(Placement.Size(-2, 1, new MapPoint(0, 0))), gesture);
        DrawingTests.AssertSound(map);
        history.Run(first.To(Placement.Size(2, 1, new MapPoint(0, 0))), gesture);
        DrawingTests.AssertSound(map);
        Assert.Equal(ends, map.Lines.Where(l => l.OnMap).Select(l => (l.Start, l.End)));
        Assert.Equal((0.0, 0.0, 256.0, 128.0), Bounds(map, rooms[0]));
        Assert.Equal("Resize", history.UndoLabel);
    }

    [Fact]
    public void ARoomStretchedOntoItsNeighbourSharesTheWall()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (256, 0, 384, 128));
        var before = Udmf(map.ToModel());
        var set = Gather(map, rooms[0]);
        Assert.Equal(0, Place(history, set, Placement.Size(1.5, 1, new MapPoint(0, 0)), Placement.Size(2, 1, new MapPoint(0, 0))));

        DrawingTests.AssertSound(map);
        var shared = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        Assert.Equal((256.0, 256.0), (shared.Start.X, shared.End.X));
        Assert.Equal((0.0, 0.0, 256.0, 128.0), Bounds(map, rooms[0]));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }
}
