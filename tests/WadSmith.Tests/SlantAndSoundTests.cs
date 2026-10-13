using WadSmith.App.Canvas;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Slanting the selection (Eureka's skew) and the sound-zones view (D58).</summary>
public sealed class SlantAndSoundTests
{
    [Fact]
    public void ASlantLeansARoomRoundItsMiddleAndKeepsItsArea()
    {
        var slant = Placement.Slant(45, 0, new MapPoint(64, 64));
        Assert.Equal(new MapPoint(64, 128), slant.Apply(0, 128));
        Assert.Equal(new MapPoint(64, 0), slant.Apply(128, 0));
        Assert.Equal("Slant", slant.Describe());
        Assert.False(slant.Flattens);
        Assert.Equal("Slant", Placement.Slant(0, -30, new MapPoint(0, 0)).Describe());
        Assert.Equal("Turn", Placement.Turn(30, new MapPoint(0, 0)).Describe());
    }

    [Fact]
    public void RoomsJoinedOpenlyShareAZoneAndASoundBlockingWallOrAShutDoorSplitsIt()
    {
        // A | B : C, D shut: A and B open into each other; B and C across a wall that blocks sound; D a closed door off A.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (0, 128, 128, 160));
        var bc = map.Lines.Single(l => l.OnMap && l.Back is not null && Math.Min(l.Start.X, l.End.X) == 256);
        history.Record("Block", e =>
        {
            e.Flag(bc, "blocksound", true);
            e.Set(rooms[3], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, rooms[3].FloorHeight);
        });
        var zones = SoundZones.Of(map.Frame);
        int Zone(Sector s) => zones[s.Slot];
        Assert.Equal(Zone(rooms[0]), Zone(rooms[1]));
        Assert.NotEqual(Zone(rooms[1]), Zone(rooms[2]));
        Assert.NotEqual(Zone(rooms[0]), Zone(rooms[3]));

        // The view paints one colour per zone: three here.
        var picture = CanvasSnapshot.Take(MapGeometry.Build(map.Frame), new MapLook(null), MapViewMode.Sound);
        Assert.Equal(3, picture.AreaStyles.Length);
    }
}
