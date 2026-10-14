using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// Disconnecting at a corner (6c): rooms that touch only at a corner each get one of their own, pulled into them, both rooms
/// staying closed; a corner that closes one area round it is refused in words, the map unchanged.
/// </summary>
public sealed class DisconnectingTests
{
    private static Vertex At(EditorMap map, double x, double y) => map.Vertices.Single(v => v.OnMap && v.X == x && v.Y == y);

    [Fact]
    public void TwoRoomsTouchingAtACornerEachGetOneOfTheirOwn()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 64, 64), (64, 64, 128, 128));
        var shared = At(map, 64, 64);
        Assert.Equal(4, map.Lines.Count(l => l.OnMap && (l.Start == shared || l.End == shared)));

        (int Made, string? WhyNot) result = default;
        history.Record("Disconnect", e => result = Disconnecting.Corner(e, shared));
        Assert.Equal((1, (string?)null), result);
        DrawingTests.AssertSound(map);

        // Each room is closed on its own corner now, the two a few units apart, and neither room's walls end at the other's corner.
        var geometry = MapGeometry.Build(map.Frame);
        var corners = rooms.Select(r => geometry.Loops(r).Single().Single(p => Math.Abs(p.X - 64) <= 4 && Math.Abs(p.Y - 64) <= 4)).ToList();
        Assert.Equal(new MapPoint(64, 64), corners[0]);
        Assert.Equal(new MapPoint(67, 67), corners[1]);
        Assert.Equal(2, map.Lines.Count(l => l.OnMap && (l.Start == shared || l.End == shared)));

        history.Undo();
        Assert.Equal(4, map.Lines.Count(l => l.OnMap && (l.Start == shared || l.End == shared)));
        Assert.Single(map.Vertices, v => v.OnMap && Math.Abs(v.X - 64) <= 4 && Math.Abs(v.Y - 64) <= 4);
    }

    [Fact]
    public void ACornerThatClosesOneAreaIsLeftWhole()
    {
        // A room's own corner (open space outside it only), and a corner where two joined rooms meet a third (no open space).
        var (map, history, _) = MovingTests.Drawn((0, 0, 64, 64), (64, 0, 128, 64), (0, 64, 128, 128));
        foreach (var (x, y) in new[] { (0.0, 0.0), (64.0, 64.0) })
        {
            (int Made, string? WhyNot) result = default;
            var before = map.Version;
            history.Record("Disconnect", e => result = Disconnecting.Corner(e, At(map, x, y)));
            Assert.Equal(0, result.Made);
            Assert.StartsWith("The walls at this corner close one area round it", result.WhyNot, StringComparison.Ordinal);
            Assert.Equal(before, map.Version);
        }

        DrawingTests.AssertSound(map);
    }
}
