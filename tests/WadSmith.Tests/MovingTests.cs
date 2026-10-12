using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Dragging the selection: what moves, one undo step per drag, the neighbour stretching.</summary>
public sealed class MovingTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    /// <summary>Two rooms side by side (0–128 and 128–256), an object in each.</summary>
    private static EditorMap Rooms()
    {
        var m = SceneTests.TwoRooms(0, 128);
        m.Things.Add(new MapThing { X = 64, Y = 64, Type = 3004 });
        m.Things.Add(new MapThing { X = 192, Y = 64, Type = 3004 });
        return EditorMap.FromModel(m);
    }

    private static MoveSet Gather(EditorMap map, params Element[] selection) => MoveSet.Gather(MapGeometry.Build(map.Frame), MoveSet.Seeds.Of(selection));

    [Fact]
    public void ARoomMovesWithItsCornersAndTheThingsInIt()
    {
        var map = Rooms();
        var set = Gather(map, map.Sectors[0]);
        Assert.Equal([(0.0, 0.0), (0, 128), (128, 0), (128, 128)], set.Vertices.Select(v => (v.X, v.Y)).Order());
        Assert.Same(map.Things[0], Assert.Single(set.Things));

        // Its four walls go whole (the shared one too); the neighbour's two walls at the shared corners stretch.
        Assert.Equal(4, set.Carried.Count);
        Assert.Equal(2, set.Stretched.Count);
        Assert.All(set.Stretched, s => Assert.Equal(128, s.MX));
    }

    [Fact]
    public void AWallOrAFaceMovesItsTwoCornersAndACornerItself()
    {
        var map = Rooms();
        var shared = map.Lines[^1];
        Assert.Equal(2, Gather(map, shared).Vertices.Count);
        Assert.Equal(Gather(map, shared).Vertices, Gather(map, shared.Back!).Vertices);
        Assert.Same(shared.Start, Assert.Single(Gather(map, shared.Start).Vertices));
        Assert.Empty(Gather(map, map.Things[1]).Vertices);
    }

    [Fact]
    public void ADragIsOneStepAndUndoPutsEverythingBack()
    {
        var map = Rooms();
        var history = new History(map);
        var before = Udmf(map.ToModel());
        var set = Gather(map, map.Sectors[0]);
        var gesture = History.NewGesture();
        var first = new MoveEdit(set, -16, 0);
        history.Run(first, gesture);
        history.Run(first.To(-40, 8), gesture);
        history.Run(first.To(-64, 0), gesture);

        Assert.Equal(-64, map.Things[0].X - 64);
        Assert.Equal(192, map.Things[1].X);
        Assert.Contains(map.Vertices, v => v.X == -64 && v.Y == 0);

        // The neighbour's walls stretch: its north wall now runs from the moved corner to its own.
        var north = map.Lines.Single(l => l.Front.Sector == map.Sectors[1] && l.Start.Y == 128 && l.End.Y == 128);
        Assert.Equal((64.0, 256.0), (north.Start.X, north.End.X));
        DrawingTests.AssertSound(map);

        Assert.True(history.Undo());
        Assert.False(history.CanUndo);
        Assert.Equal(before, Udmf(map.ToModel()));
        Assert.True(history.Redo());
        Assert.Equal(-64, map.Things[0].X - 64);
    }

    [Fact]
    public void ADragLetGoOfIsNotKeptForRedo()
    {
        var map = Rooms();
        var history = new History(map);
        var before = Udmf(map.ToModel());
        var gesture = History.NewGesture();
        history.Run(new MoveEdit(Gather(map, map.Things[0]), 32, 32), gesture);
        Assert.False(history.Discard(History.NewGesture()));
        Assert.True(history.Discard(gesture));
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    /// <summary>An empty map with rooms drawn by the Room tool's own drawing.</summary>
    private static (EditorMap Map, History History, List<Sector> Rooms) Drawn(params (double X0, double Y0, double X1, double Y1)[] boxes)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var history = new History(map);
        var rooms = new List<Sector>();
        foreach (var (x0, y0, x1, y1) in boxes)
        {
            var geometry = MapGeometry.Build(map.Frame);
            history.Record("Draw a room", e => rooms.AddRange(ShapeDrawing.Draw(e, geometry, [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)], Editor.Look.For("doom"))));
        }

        return (map, history, rooms);
    }

    /// <summary>Drags a room as the canvas does (live steps, then the joins when let go), as one gesture.</summary>
    private static int Drag(EditorMap map, History history, Sector room, double dx, double dy)
    {
        var set = Gather(map, room);
        var gesture = History.NewGesture();
        var first = new MoveEdit(set, dx / 2, dy / 2);
        history.Run(first, gesture);
        history.Run(first.To(dx, dy), gesture);
        var crossings = 0;
        history.Record("Join", e => crossings = Joining.AfterMove(e, set, Editor.Look.For("doom")), gesture);
        return crossings;
    }

    private static List<Line> Walls(EditorMap map) => [.. map.Lines.Where(l => l.OnMap)];

    [Fact]
    public void ARoomDraggedFlushAgainstAnotherSharesTheWall()
    {
        var (map, history, rooms) = Drawn((0, 0, 128, 128), (256, 0, 384, 128));
        var before = Udmf(map.ToModel());
        Assert.Equal(0, Drag(map, history, rooms[1], -128, 0));
        DrawingTests.AssertSound(map);
        Assert.Equal(7, Walls(map).Count);
        Assert.Equal(6, map.Vertices.Count(v => v.OnMap));
        var shared = Assert.Single(Walls(map), l => l.Back is not null);
        Assert.Equal(new HashSet<Sector>(rooms), new HashSet<Sector> { shared.Front.Sector, shared.Back!.Sector });
        Assert.False(shared.Flag("blocking"));

        // The drag and its joins are one step.
        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
        history.Redo();
        DrawingTests.AssertSound(map);
        Assert.Equal(7, Walls(map).Count);
    }

    [Fact]
    public void AShortRoomDraggedAgainstALongWallSplitsItAndSharesThePiece()
    {
        var (map, history, rooms) = Drawn((0, 0, 128, 256), (256, 64, 384, 192));
        Assert.Equal(0, Drag(map, history, rooms[1], -128, 0));
        DrawingTests.AssertSound(map);
        Assert.Equal(6 + 3, Walls(map).Count);
        var shared = Assert.Single(Walls(map), l => l.Back is not null);
        Assert.Equal((64.0, 192.0), (Math.Min(shared.Start.Y, shared.End.Y), Math.Max(shared.Start.Y, shared.End.Y)));
    }

    [Fact]
    public void ALongRoomDraggedAgainstAShortWallIsSplitByItsCorners()
    {
        var (map, history, rooms) = Drawn((0, 64, 128, 192), (256, 0, 384, 256));
        Assert.Equal(0, Drag(map, history, rooms[1], -128, 0));
        DrawingTests.AssertSound(map);
        Assert.Equal(3 + 6, Walls(map).Count);
        Assert.Single(Walls(map), l => l.Back is not null);
    }

    [Fact]
    public void ACornerDraggedOntoACornerBecomesIt()
    {
        var (map, history, rooms) = Drawn((0, 0, 128, 128), (256, 256, 384, 384));
        Assert.Equal(0, Drag(map, history, rooms[1], -128, -128));
        DrawingTests.AssertSound(map);
        Assert.Equal(7, map.Vertices.Count(v => v.OnMap));
        Assert.Equal(8, Walls(map).Count);
    }

    [Fact]
    public void WallsLeftCrossingAreCountedNotChanged()
    {
        var (map, history, rooms) = Drawn((0, 0, 128, 128), (256, 0, 384, 128));
        Assert.Equal(2, Drag(map, history, rooms[1], -192, 64));
        Assert.Equal(8, Walls(map).Count);
    }

    [Fact]
    public void ThingsInTheNeighbourStayWhenARoomMoves()
    {
        var map = Rooms();
        var history = new History(map);
        history.Run(new MoveEdit(Gather(map, map.Sectors[1]), 0, 64), History.NewGesture());
        Assert.Equal((64.0, 64.0), (map.Things[0].X, map.Things[0].Y));
        Assert.Equal((192.0, 128.0), (map.Things[1].X, map.Things[1].Y));
        DrawingTests.AssertSound(map);
        Assert.Equal(Udmf(map.FrameFromScratch().ToModel()), Udmf(map.ToModel()));
    }
}
