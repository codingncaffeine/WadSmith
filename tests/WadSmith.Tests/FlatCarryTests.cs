using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>A room moved, turned, resized or mirrored whole carries its floor's texture with it.</summary>
public sealed class FlatCarryTests
{
    public static TheoryData<double, double, double, double, double, int> Placements => new()
    {
        // rotation, x/y panning, x/y scale of the floor, and which placement (see Place).
        { 0, 0, 0, 1, 1, 0 }, { 30, 16, 40, 1, 1, 0 }, { 0, 0, 0, 1, 1, 1 }, { -75, 3, -9, 2, 0.5, 1 }, { 30, 16, 40, 1, 1, 2 },
        { 0, 0, 0, 1, 1, 3 }, { 45, 8, 8, 1, 1, 3 }, { 10, 0, 0, 1, 1, 4 }, { 0, 0, 0, 2, 2, 5 },
    };

    private static Placement Place(int which) => which switch
    {
        0 => Placement.Turn(30, new MapPoint(100, -50)),
        1 => Placement.Turn(90, new MapPoint(0, 0)),
        2 => Placement.Size(2, 2, new MapPoint(64, 64)),
        3 => Placement.Size(-1, 1, new MapPoint(128, 0)),
        4 => new Placement(1, 0, 0, 1, 37, -11),
        _ => Placement.Size(1, -1, new MapPoint(0, 300)),
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void EachPointsImageShowsTheTexelThePointShowed(double rotation, double xpan, double ypan, double xscale, double yscale, int which)
    {
        var (t, p) = (new FlatTransform(xpan, ypan, rotation, xscale, yscale), Place(which));
        var after = t.Through(p);
        foreach (var (x, y) in new[] { (0.0, 0.0), (100.0, -37.0), (-250.0, 512.0), (64.0, 64.0) })
        {
            var (u, v) = t.Texel(x, y);
            var to = p.Apply(x, y);
            var (u2, v2) = after.Texel(to.X, to.Y);
            Assert.Equal(u, u2, 2);
            Assert.Equal(v, v2, 2);
        }
    }

    [Fact]
    public void AMoveByWholeRepeatsLeavesNoFieldsAndAHalfRepeatShiftsByHalf()
    {
        Assert.True(FlatTransform.Plain.Through(new Placement(1, 0, 0, 1, 64, -128), (64, 64)).IsPlain);
        Assert.Equal(new FlatTransform(32, 0, 0, 1, 1), FlatTransform.Plain.Through(new Placement(1, 0, 0, 1, 32, 0), (64, 64)));
        Assert.Equal(-90, FlatTransform.Plain.Through(Placement.Turn(90, new MapPoint(0, 0))).Rotation);
    }

    private static MoveSet Whole(EditorMap map, Sector room) => MoveSet.Gather(MapGeometry.Build(map.Frame), MoveSet.Seeds.Of([room]));

    [Fact]
    public void DraggingARoomHalfATileShiftsItsFloorAndUndoTakesTheFieldAway()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256));
        history.Run(new MoveEdit(Whole(map, rooms[0]), 32, 0, _ => (64, 64)));
        Assert.Equal(32, rooms[0].Extra.TryGet("xpanningfloor", out var x) ? x.AsFloat() : 0);
        Assert.Equal(32, rooms[0].Extra.TryGet("xpanningceiling", out var c) ? c.AsFloat() : 0);
        history.Undo();
        Assert.False(rooms[0].Extra.Contains("xpanningfloor"));

        // By a whole tile: nothing to write.
        history.Run(new MoveEdit(Whole(map, rooms[0]), 64, 0, _ => (64, 64)));
        Assert.False(rooms[0].Extra.Contains("xpanningfloor"));
    }

    [Fact]
    public void TurningARoomTurnsItsFloorSoTheSameTexelsLieUnderItsCorners()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 128));
        var before = FlatTransform.Of(rooms[0], false);
        var corner = map.Vertices.Single(v => v.OnMap && v.X == 256 && v.Y == 128);
        var texel = before.Texel(corner.X, corner.Y);
        history.Run(new TransformEdit(Whole(map, rooms[0]), Placement.Turn(90, new MapPoint(128, 64)), _ => (64, 64)));
        var after = FlatTransform.Of(rooms[0], false);
        Assert.Equal(-90, after.Rotation);
        // The same texel, or one a whole tile away (the shift is kept within one 64 repeat): the same picture.
        var (u, v) = after.Texel(corner.X, corner.Y);
        static double Off(double a, double b)
        {
            var r = ((a - b) % 64 + 64) % 64;
            return Math.Min(r, 64 - r);
        }

        Assert.Equal(0, Off(texel.U, u), 3);
        Assert.Equal(0, Off(texel.V, v), 3);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ARoomOnlyPartlyMovedKeepsItsFloorLinedUpWithTheMap()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        var corner = map.Vertices.Single(v => v.OnMap && v.X == 256 && v.Y == 256);
        history.Run(new MoveEdit(MoveSet.Gather(MapGeometry.Build(map.Frame), MoveSet.Seeds.Of([corner])), 32, 32, _ => (64, 64)));
        Assert.DoesNotContain(map.Sectors[0].Extra, kv => kv.Key.Contains("panning", StringComparison.Ordinal));
    }
}
