using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The elevation profile: the floors and ceilings along a line, and where the player is stopped.</summary>
public sealed class ElevationProfileTests
{
    private static ElevationProfile Profile(EditorMap map, double x0, double y0, double x1, double y1) => ElevationProfile.Build(MapGeometry.Build(map.Frame), new MapPoint(x0, y0), new MapPoint(x1, y1));

    private static void Set(EditorMap map, Sector s, double floor, double ceiling, string? flat = null) => new Editor.Editing.History(map).Record("Heights", e =>
    {
        e.Set(s, x => x.FloorHeight, (x, v) => x.FloorHeight = v, floor);
        e.Set(s, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, ceiling);
        if (flat is not null) e.Set(s, x => x.CeilingTexture, (x, v) => x.CeilingTexture = v, flat);
    });

    [Fact]
    public void AStepTooHighStopsThePlayerAndOneOf16DoesNot()
    {
        var (map, _, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        Set(map, rooms[1], 40, 168);
        var p = Profile(map, 16, 64, 240, 64);
        Assert.Equal(224, p.Length);
        Assert.Equal(2, p.Spans.Count);
        Assert.Equal((0.0, 112.0, 0.0, 128.0), (p.Spans[0].From, p.Spans[0].To, p.Spans[0].Floor, p.Spans[0].Ceiling));
        Assert.Equal((112.0, 224.0, 40.0), (p.Spans[1].From, p.Spans[1].To, p.Spans[1].Floor));
        var step = Assert.Single(p.Steps);
        Assert.Equal((112.0, 40.0, 88.0, "40 up: too high to step"), (step.At, step.Rise, step.Opening, step.Stops));
        Assert.Contains("stopped 112 along: 40 up", p.Describe());

        Set(map, rooms[1], 16, 168);
        Assert.Null(Assert.Single(Profile(map, 16, 64, 240, 64).Steps).Stops);
        Assert.Contains("the player walks all of it", Profile(map, 16, 64, 240, 64).Describe());

        // Stepping down any height is fine; a room too low to stand in is not.
        Set(map, rooms[1], -200, 128);
        Assert.Null(Assert.Single(Profile(map, 16, 64, 240, 64).Steps).Stops);
        Set(map, rooms[1], 0, 40);
        Assert.Equal("40 tall: too low to stand", Assert.Single(Profile(map, 16, 64, 240, 64).Steps).Stops);

        // Both rooms tall enough and a step of 20, but only 50 between the higher floor and the lower ceiling.
        Set(map, rooms[0], 0, 70);
        Set(map, rooms[1], 20, 90);
        Assert.Equal("50 high: too low to pass", Assert.Single(Profile(map, 16, 64, 240, 64).Steps).Stops);
    }

    [Fact]
    public void FromOutsideIntoARoomAndOutAgainIsAWallAtTheFarSide()
    {
        var (map, _, _) = MovingTests.Drawn((0, 0, 128, 128));
        var p = Profile(map, -64, 64, 192, 64);
        Assert.Equal([-1, 0, -1], p.Spans.Select(s => s.Area));
        Assert.Null(p.Steps[0].Stops);
        Assert.Equal("a solid wall", p.Steps[1].Stops);
        Assert.Equal(64, p.Steps[0].At);
        Assert.Contains("1 area", p.Describe());
        Assert.Equal("200 long, outside the map.", Profile(map, 200, 0, 400, 0).Describe());
    }

    [Fact]
    public void AWallWithTheSameAreaOnBothSidesIsNoStep()
    {
        // A room split in two, then both halves given back to the first: the line between them has it on both sides.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 128));
        (IReadOnlyList<Sector> Parts, string?) split = ([], null);
        history.Record("Split", e => split = Editor.Editing.Splitting.Split(e, MapGeometry.Build(map.Frame), [new(128, 0), new(128, 128)]));
        var part = split.Parts[1];
        history.Record("Same area", e =>
        {
            foreach (var side in map.Sides.Where(x => x.OnMap && x.Sector == part)) e.Set(side, x => x.Sector, (x, v) => x.Sector = v, rooms[0]);
        });
        var p = Profile(map, 16, 64, 240, 64);
        Assert.Equal((0.0, 224.0, rooms[0].Slot), (Assert.Single(p.Spans).From, p.Spans[0].To, p.Spans[0].Area));
        Assert.Empty(p.Steps);
    }

    [Fact]
    public void OneAreaAcrossAWallIsOneStretchAndSkyIsKnown()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 128), (0, 0, 128, 128));
        history.Record("Join", e => Editor.Editing.Rooms.Join(e, MapGeometry.Build(map.Frame), [rooms[0], rooms[1]]));
        var room = map.Sectors.Single(s => s.OnMap);
        Set(map, room, 0, 128, "F_SKY1");
        var p = Profile(map, 16, 64, 240, 64);
        var span = Assert.Single(p.Spans);
        Assert.True(span.Sky);
        Assert.Empty(p.Steps);
    }
}
