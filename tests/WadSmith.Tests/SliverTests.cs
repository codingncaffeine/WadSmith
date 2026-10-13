using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>A sliver of room left where walls nearly met is called out and joined to the room beside it (a user's map, 2026-10-12).</summary>
public sealed class SliverTests
{
    [Fact]
    public void ASliverBetweenTwoRoomsIsCalledOutAndJoinedToTheRoomBesideIt()
    {
        // A room 0–256, a sliver 3 units deep above it, then a room above that: the door between the first room and the sliver
        // opens into 3 units of room, not into the room the user meant.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (0, 256, 256, 259), (0, 259, 256, 512));
        var sliver = rooms[1];
        var problem = Assert.Single(Problems.Check(MapGeometry.Build(map.Frame), null, Look.For("doom"), "doom"), p => p.Rule == "sliver");
        Assert.Same(sliver, Assert.Single(problem.Show));
        Assert.Contains("3 units across", problem.Sentence);

        history.Record(problem.FixLabel!, problem.Fix!);
        DrawingTests.AssertSound(map);
        Assert.False(sliver.OnMap && map.Lines.Any(l => l.OnMap && (ReferenceEquals(l.Front.Sector, sliver) || ReferenceEquals(l.Back?.Sector, sliver))));
        Assert.DoesNotContain(Problems.Check(MapGeometry.Build(map.Frame), null, Look.For("doom"), "doom"), p => p.Rule == "sliver");

        // A door's frames, 8 deep, are not slivers.
        var (doorMap, doorHistory, _) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160));
        var opening = Assert.Single(doorMap.Lines, l => l.OnMap && l.Back is not null);
        doorHistory.Record("Door", e => Doors.Make(e, MapGeometry.Build(doorMap.Frame), opening, Look.For("doom"), null, fill: true, heightOf: _ => 72));
        Assert.DoesNotContain(Problems.Check(MapGeometry.Build(doorMap.Frame), null, Look.For("doom"), "doom"), p => p.Rule == "sliver");
    }
}
