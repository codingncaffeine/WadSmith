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
