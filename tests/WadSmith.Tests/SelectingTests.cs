using System.Collections.Concurrent;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// Selections from the whole map (Eureka's select all, invert, select-alike, select path, last selection) and copy and
/// paste properties, the tools a veteran reaches for that WadSmith lacked (D58).
/// </summary>
public sealed class SelectingTests
{
    private sealed class Queue
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action a) => _queue.Enqueue(a);

        public async Task PumpUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!done())
            {
                while (_queue.TryDequeue(out var a)) a();
                if (DateTime.UtcNow > until) throw new TimeoutException();
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }
    }

    private static MapGeometry Shapes(EditorMap map) => MapGeometry.Build(map.Frame);

    [Fact]
    public void AllPicksEveryRoomAndObjectAndInvertPicksTheOthersOfTheSameKind()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (512, 0, 640, 128));
        var thing = new Thing { X = 64, Y = 64, Type = 3001 };
        history.Record("Place", e => e.Add(thing));
        var g = Shapes(map);

        var all = Selecting.All(g, PickOnly.Anything);
        Assert.Equal(rooms.Count + 1, all.Count);
        Assert.Superset(rooms.Cast<Element>().ToHashSet(), all.ToHashSet());
        Assert.Contains(thing, all);
        Assert.Equal(map.Lines.Count(l => l.OnMap), Selecting.All(g, PickOnly.Walls).Count);

        // One room selected: the other rooms, not walls or the object; one wall's face: the other walls.
        Assert.Equal(rooms.Skip(1).Cast<Element>(), Selecting.Invert(g, [rooms[0]], PickOnly.Anything));
        var face = map.Lines.First(l => l.OnMap).Front;
        var others = Selecting.Invert(g, [face], PickOnly.Anything);
        Assert.Equal(map.Lines.Count(l => l.OnMap) - 1, others.Count);
        Assert.DoesNotContain(face.Line!, others);
        Assert.All(others, e => Assert.IsType<Line>(e));

        // Nothing selected: everything; with objects picked only, the objects.
        Assert.Equal(all.Count, Selecting.Invert(g, [], PickOnly.Anything).Count);
        Assert.Equal([thing], Selecting.Invert(g, [], PickOnly.Objects));
    }

    [Fact]
    public void AlikeMatchesByEachMeasureWithTheSeedFirst()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (1024, 0, 1152, 128));
        history.Record("Dress", e =>
        {
            e.Set(rooms[1], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NUKAGE1");
            e.Set(rooms[3], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NUKAGE1");
            e.Set(rooms[2], s => s.Light, (s, v) => s.Light = v, 96);
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 16);
        });
        var g = Shapes(map);

        Assert.Equal([rooms[3], rooms[1]], Selecting.Like(g, rooms[3], Alike.FloorPicture));
        Assert.Equal(3, Selecting.Like(g, rooms[0], Alike.Brightness).Count);
        Assert.Equal([rooms[2]], Selecting.Like(g, rooms[2], Alike.Brightness));
        Assert.Equal(3, Selecting.Like(g, rooms[0], Alike.FloorHeight).Count);

        // Joined at the same floor height: room 1 is raised, so room 0 reaches no further; room 2 reaches nothing either.
        Assert.Equal([rooms[0]], Selecting.Like(g, rooms[0], Alike.JoinedFloorHeight));
        history.Record("Level", e => e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 0));
        Assert.Equal([rooms[0], rooms[1], rooms[2]], Selecting.Like(Shapes(map), rooms[0], Alike.JoinedFloorHeight));

        // A measure that is not the element's: nothing.
        Assert.Empty(Selecting.Like(g, rooms[0], Alike.ObjectKind));
        Assert.False(Selecting.Fits(rooms[0], Alike.WallPicture));
    }

    [Fact]
    public void WallsAlongFollowTheOutlineUntilTheWayForks()
    {
        // Two rooms side by side: solid walls follow solid walls, so from the far west wall the path runs past the ends of
        // the open wall between them, round the outline of both (6 walls); a third solid wall at a corner forks it.
        var (map, _, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var g = Shapes(map);
        var west = map.Lines.Single(l => l.OnMap && l.Start.X == 0 && l.End.X == 0);
        var along = Selecting.Like(g, west, Alike.WallsAlong);
        Assert.Same(west, along[0]);
        Assert.Equal(6, along.Count);
        Assert.All(along.Cast<Line>(), l => Assert.Null(l.Back));

        // A second room touching the first at its corner (128, 128): four solid walls meet there, so the way forks and stops
        // from both ends: the second room's four walls and none of the first's (whose walls come first at that corner).
        var (forked, _, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 128, 192, 192));
        var start = forked.Lines.Single(l => l.OnMap && l.Start.X == 192 && l.End.X == 192);
        var path = Selecting.Like(Shapes(forked), start, Alike.WallsAlong);
        Assert.Equal(4, path.Count);
        Assert.All(path.Cast<Line>(), l => Assert.True(l.Start.X >= 128 && l.End.X >= 128 && l.Start.Y >= 128 && l.End.Y >= 128));
    }

    [Fact]
    public void WallPictureMatchesAnyPartOfEitherFace()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        var solid = map.Lines.First(l => l.OnMap && l.Back is null);
        history.Record("Dress", e =>
        {
            e.Set(solid.Front, s => s.Middle, (s, v) => s.Middle = v, "STARTAN2");
            e.Set(shared.Back!, s => s.Bottom, (s, v) => s.Bottom = v, "STARTAN2");
        });
        var found = Selecting.Like(Shapes(map), solid.Front, Alike.WallPicture);
        Assert.Equal([solid, shared], found);
    }

    [Fact]
    public void TheLastSelectionComesBackAndASecondTimeSwapsBack()
    {
        var (map, _, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var selection = new Selection();
        Assert.False(selection.BringBack());
        selection.Set(rooms[0], rooms[1]);
        selection.Clear();
        Assert.True(selection.BringBack());
        Assert.Equal([rooms[0], rooms[1]], selection.Items);

        selection.Pick(rooms[0], new MapPoint(1, 1));
        Assert.True(selection.BringBack());
        Assert.Equal([rooms[0], rooms[1]], selection.Items);
        Assert.True(selection.BringBack());
        Assert.Equal([rooms[0]], selection.Items);
        _ = map;
    }

    [Fact]
    public void ARoomsPropertiesGoOntoOthersButTheirTagsStay()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (512, 0, 640, 128), (1024, 0, 1152, 128));
        history.Record("Dress", e =>
        {
            e.Set(rooms[0], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NUKAGE1");
            e.Set(rooms[0], s => s.FloorHeight, (s, v) => s.FloorHeight = v, -16);
            e.Set(rooms[0], s => s.Light, (s, v) => s.Light = v, 208);
            e.Set(rooms[0], s => s.Special, (s, v) => s.Special = v, 5);
            e.Set(rooms[0], s => s.Tag, (s, v) => s.Tag = v, 7);
            e.Field(rooms[0], "lightcolor", Core.Maps.Udmf.UdmfValue.Int(0x40FF40));
            e.Set(rooms[1], s => s.Tag, (s, v) => s.Tag = v, 9);
            e.Field(rooms[1], "fadecolor", Core.Maps.Udmf.UdmfValue.Int(0x101010));
        });
        Assert.True(Properties.Copy(rooms[0]));
        var count = 0;
        history.Record("Paste", e => count = Properties.Paste(e, Properties.Held!, [rooms[1], rooms[2], map.Vertices.First(v => v.OnMap)]));
        Assert.Equal(2, count);
        foreach (var s in rooms.Skip(1))
        {
            Assert.Equal(("NUKAGE1", -16.0, 208, 5), (s.FloorTexture, s.FloorHeight, s.Light, s.Special));
            Assert.True(s.Extra.TryGet("lightcolor", out _));
        }

        // The tag stays the room's own; a field the copied room lacks goes.
        Assert.Equal(9, rooms[1].Tag);
        Assert.False(rooms[1].Extra.TryGet("fadecolor", out _));
        history.Undo();
        Assert.NotEqual("NUKAGE1", rooms[1].FloorTexture);
        Assert.Equal((0.0, 9), (rooms[1].FloorHeight, rooms[1].Tag));
        Assert.True(rooms[1].Extra.TryGet("fadecolor", out _));
    }

    [Fact]
    public async Task ASolidWallsPictureGoesOnAnOpeningsUpperAndLowerNotAcrossIt()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var solid = map.Lines.First(l => l.OnMap && l.Back is null);
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        history.Record("Dress", e => e.Set(solid.Front, s => s.Middle, (s, v) => s.Middle = v, "BROWN1"));
        var middle = shared.Front.Middle;
        Assert.True(Properties.Copy(solid));
        history.Record("Paste", e => Properties.Paste(e, Properties.Held!, [shared]));
        Assert.Equal(("BROWN1", middle, "BROWN1"), (shared.Front.Top, shared.Front.Middle, shared.Front.Bottom));
        Assert.Equal(("BROWN1", "BROWN1"), (shared.Back!.Top, shared.Back.Bottom));
        DrawingTests.AssertSound(map);
    }

    private static async Task<(TempFolder Temp, EditorSession Session, Queue Ui, Sector Room)> Opened(params MapPoint[] room)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new Queue();
        var session = EditorSession.Open(project, "MAP01", ui.Post);
        session.DrawRoom(room);
        var drawn = Assert.Single(session.Selection.Items.OfType<Sector>());
        await ui.PumpUntil(() => session.Geometry.Version == session.Map.Version);
        return (temp, session, ui, drawn);
    }

    private static List<(double X, double Y)> Corners(EditorSession session, Sector room) =>
        [.. session.Map.Lines.Where(l => l.OnMap && (l.Front.Sector == room || l.Back?.Sector == room)).SelectMany(l => new[] { l.Start, l.End }).Distinct().Select(v => (v.X, v.Y)).Order()];

    [Fact]
    public async Task SnapMovesCornersOntoTheGridAndLeavesOneWhosePointIsTaken()
    {
        var (temp, session, ui, room) = await Opened(new(515, 3), new(515, 131), new(645, 131), new(645, 3));
        using var _ = temp;
        using var __ = session;
        session.SnapSelection(16);
        await ui.PumpUntil(() => session.History.UndoLabel is "Snap to the grid" or "Join");
        Assert.Equal([(512.0, 0.0), (512.0, 128.0), (640.0, 0.0), (640.0, 128.0)], Corners(session, room));
        DrawingTests.AssertSound(session.Map);

        // A room 3 wide whose west and east corners share their nearest grid point: one side moves onto it, the other
        // stays, so the room keeps its width and faces (never turned inside out), and it says so.
        var (t2, thin, ui2, sliver) = await Opened(new(1010, 0), new(1010, 128), new(1013, 128), new(1013, 0));
        using var ___ = t2;
        using var ____ = thin;
        var told = new List<string>();
        thin.Notice += told.Add;
        thin.SnapSelection(16);
        await ui2.PumpUntil(() => told.Count > 0);
        Assert.Contains("stayed", told[^1]);
        Assert.Equal(4, Corners(thin, sliver).Distinct().Count());
        DrawingTests.AssertSound(thin.Map);
    }

    [Fact]
    public void SnappingNeverTurnsAWallRound()
    {
        // The east corners first: they take the grid point (1008) the west ones want, which would put the east side west
        // of the west side and turn the room inside out. The walls that would turn keep an end where it was.
        var (map, _, _) = MovingTests.Drawn((1010, 0, 1013, 128));
        var g = Shapes(map);
        var corners = map.Vertices.Where(v => v.OnMap).OrderByDescending(v => v.X).ToList();
        var (moves, _, refused) = Shaping.Snap(g, corners, [], 16);
        MapPoint At(Vertex v) => moves.TryGetValue(v, out var p) ? p : new MapPoint(v.X, v.Y);
        foreach (var l in map.Lines.Where(l => l.OnMap))
        {
            var (a, b) = (At(l.Start), At(l.End));
            Assert.True((b.X - a.X) * (l.End.X - l.Start.X) + (b.Y - a.Y) * (l.End.Y - l.Start.Y) > 0, $"{l.Start.X},{l.Start.Y} → {l.End.X},{l.End.Y} turned round");
        }

        Assert.True(refused >= 2);
    }

    [Fact]
    public async Task ARoomsCornersGoRoundACircleOrIntoALineAndTheMapStaysWhole()
    {
        var (temp, session, ui, room) = await Opened(new(512, 0), new(512, 128), new(576, 160), new(640, 128), new(640, 0));
        using var _ = temp;
        using var __ = session;
        session.ShapeCorners(circle: true);
        await ui.PumpUntil(() => session.History.UndoLabel is "Space the corners round a circle" or "Join");
        var corners = Corners(session, room);
        var (cx, cy) = (corners.Average(c => c.X), corners.Average(c => c.Y));
        var radii = corners.Select(c => Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy))).ToList();
        Assert.True(radii.Max() - radii.Min() < 0.1, string.Join(", ", radii));
        DrawingTests.AssertSound(session.Map);

        // Three corners of a wall run: the middle one goes halfway between the ends.
        session.History.Undo();
        await ui.PumpUntil(() => session.Geometry.Version == session.Map.Version);
        var a = session.Map.Vertices.Single(v => v.OnMap && v.X == 512 && v.Y == 128);
        var top = session.Map.Vertices.Single(v => v.OnMap && v.X == 576 && v.Y == 160);
        var b = session.Map.Vertices.Single(v => v.OnMap && v.X == 640 && v.Y == 128);
        session.Selection.Set(a, top, b);
        session.ShapeCorners(circle: false);
        await ui.PumpUntil(() => top.Y == 128);
        Assert.Equal((576.0, 128.0), (top.X, top.Y));

        // Two corners alone: nothing to shape, and it says so.
        session.Selection.Set(a, b);
        var told = new List<string>();
        session.Notice += told.Add;
        session.ShapeCorners(circle: true);
        await ui.PumpUntil(() => told.Count > 0);
        Assert.Contains("three corners", Assert.Single(told));
    }

    [Fact]
    public async Task TheSelectionMovesByAnExactDistance()
    {
        var (temp, session, ui, room) = await Opened(new(512, 0), new(512, 128), new(640, 128), new(640, 0));
        using var _ = temp;
        using var __ = session;
        session.MoveSelection(32, -16);
        await ui.PumpUntil(() => session.History.UndoLabel == "Move");
        Assert.Equal([(544.0, -16.0), (544.0, 112.0), (672.0, -16.0), (672.0, 112.0)], Corners(session, room));
    }

    [Fact]
    public async Task TheSessionSelectsFromTheWholeMapSwapsFlatsAndSplitsWalls()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new Queue();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        session.DrawRoom([new(512, 0), new(512, 128), new(768, 128), new(768, 0)]);
        var drawn = Assert.Single(session.Selection.Items.OfType<Sector>());

        // Select all waits for the shapes to catch up with the room just drawn, then picks both rooms and the start.
        session.SelectAll(PickOnly.Anything);
        await ui.PumpUntil(() => session.Selection.Count > 1);
        Assert.Equal(3, session.Selection.Count);
        Assert.Contains(drawn, session.Selection.Items);

        // Floor and ceiling pictures trade places, with the fields that go with each.
        session.Selection.Set(drawn);
        var (floor, ceiling) = (drawn.FloorTexture, drawn.CeilingTexture);
        session.History.Record("Turn the floor", e => e.Field(drawn, "rotationfloor", Core.Maps.Udmf.UdmfValue.Float(45.0)));
        session.SwapFlats();
        Assert.Equal((ceiling, floor), (drawn.FloorTexture, drawn.CeilingTexture));
        Assert.False(drawn.Extra.TryGet("rotationfloor", out _));
        Assert.True(drawn.Extra.TryGet("rotationceiling", out _));

        // A wall split in half: two walls, the new corner in the middle and selected.
        var wall = session.Map.Lines.Single(l => l.OnMap && l.Start.X == 512 && l.End.X == 512);
        session.Selection.Set(wall);
        session.SplitWalls();
        var corner = Assert.IsType<Vertex>(Assert.Single(session.Selection.Items));
        Assert.Equal((512.0, 64.0), (corner.X, corner.Y));
        Assert.Equal(5, session.Map.Lines.Count(l => l.OnMap && (l.Front.Sector == drawn || l.Back?.Sector == drawn)));
        DrawingTests.AssertSound(session.Map);
    }
}
