using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>Slopes: the plane fields GZDoom reads, the Ramp tool that writes them, the 3D view that draws them, the walker that climbs them.</summary>
public sealed class SlopeTests
{
    [Fact]
    public void ThePlaneGivesHeightsAndItsFieldsReadBack()
    {
        var floor = SlopePlane.Rising(new MapPoint(0, -256), (0, 1), 0, 0.25, ceiling: false);
        Assert.Equal(1, floor.C);
        Assert.Equal(0, floor.Z(100, -256), 6);
        Assert.Equal(128, floor.Z(-50, 256), 6);
        var ceiling = SlopePlane.Rising(new MapPoint(0, -256), (0, 1), 256, -0.125, ceiling: true);
        Assert.Equal(-1, ceiling.C);
        Assert.Equal(192, ceiling.Z(0, 256), 6);

        var extra = floor.Fields(false).Select(f => new KeyValuePair<string, UdmfValue>(f.Name, f.Value)).ToArray();
        Assert.Equal(floor, SlopePlane.Floor(extra));
        Assert.Null(SlopePlane.Ceiling(extra));
        Assert.Null(SlopePlane.Floor(extra.Take(3).ToArray()));
    }

    private static (EditorMap Map, MapGeometry Geometry, History History, Sector Room, Line South) Room()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var geometry = MapGeometry.Build(map.Frame);
        var room = map.Sectors[0];
        var south = Slopes.Walls(geometry, room).Single(w => w.Words == "the south wall").Wall;
        return (map, geometry, new History(map), room, south);
    }

    [Fact]
    public void TheRampToolSlopesARoomFromAWallAndReadsItBack()
    {
        var (map, geometry, history, room, south) = Room();
        Assert.Equal(["the south wall", "the east wall", "the north wall", "the west wall"], Slopes.Walls(geometry, room).Select(w => w.Words));
        Assert.Equal(1, Slopes.Inward(south, room).Y, 9);
        Assert.Equal(512, Slopes.Reach(geometry, room, south), 6);

        history.Record("Slope the floor", e => Slopes.Set(e, room, Slopes.Ramp(geometry, room, south, 0, 64, false), false));
        var plane = SlopePlane.Floor(room)!.Value;
        Assert.Equal(0, plane.Z(0, -256), 6);
        Assert.Equal(64, plane.Z(0, 256), 6);
        Assert.Equal(32, plane.Z(100, 0), 6);
        var seen = Slopes.Recognise(geometry, room, plane, false)!.Value;
        Assert.Same(south, seen.Wall);
        Assert.Equal(64, seen.Far);

        // The 3D view: the floor's corners follow the plane; the walls' bottoms follow it at each end, their tops stay at the ceiling.
        var scene = MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 128));
        var floor = scene.Batches.Single(b => b.Kind == SurfaceKind.Floor);
        (float Y, float Z)[] corners = [.. Enumerable.Range(0, floor.VertexCount).Select(i => (floor.Vertices[i * SceneBatch.Stride + 1], floor.Vertices[i * SceneBatch.Stride + 2]))];
        Assert.All(corners, c => Assert.Equal((c.Y + 256) / 8, c.Z, 3));
        var walls = scene.Batches.Single(b => b.Kind == SurfaceKind.Wall);
        (float Y, float Z)[] wallCorners = [.. Enumerable.Range(0, walls.VertexCount).Select(i => (walls.Vertices[i * SceneBatch.Stride + 1], walls.Vertices[i * SceneBatch.Stride + 2]))];
        Assert.All(wallCorners.Where(c => c.Z < room.CeilingHeight - 0.01), c => Assert.Equal((c.Y + 256) / 8, c.Z, 3));
        Assert.Contains(wallCorners, c => Math.Abs(c.Z - room.CeilingHeight) < 0.01);

        history.Undo();
        Assert.Null(SlopePlane.Floor(room));
    }

    [Fact]
    public void TheWalkerClimbsARampAStepAtATime()
    {
        var (map, geometry, history, room, south) = Room();
        history.Record("Slope the floor", e => Slopes.Set(e, room, Slopes.Ramp(geometry, room, south, 0, 64, false), false));
        geometry = MapGeometry.Build(map.Frame);
        Assert.Equal((-100 + 256) / 8.0 + Walker.EyeHeight, Walker.Step(geometry, new MapPoint(0, -200), (-200 + 256) / 8.0, new MapPoint(0, -100))!.Value, 6);
        Assert.Null(Walker.Step(geometry, new MapPoint(0, -256), 0, new MapPoint(0, 0)));
    }
}
