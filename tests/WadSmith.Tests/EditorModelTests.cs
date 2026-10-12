using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

public sealed class EditorModelTests
{
    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void EveryMapPassesThroughTheEditorUnchanged(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        foreach (var lumps in MapLocator.Find(wad))
        {
            var model = MapConversion.Read(lumps, game);
            var before = UdmfText.WriteString(MapConversion.ToUdmf(model));
            var after = UdmfText.WriteString(MapConversion.ToUdmf(EditorMap.FromModel(model).ToModel()));
            Assert.True(before == after, $"{fileName} {lumps.Name} changes on its way through the editor");
        }
    }

    private static EditorMap Square()
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        m.Vertices.AddRange([new MapVertex { X = 0, Y = 0 }, new MapVertex { X = 128, Y = 0 }, new MapVertex { X = 128, Y = 128 }]);
        m.Sectors.Add(new MapSector { CeilingHeight = 128, FloorTexture = "F", CeilingTexture = "C" });
        m.Sides.Add(new MapSide { Sector = 0 });
        m.Lines.Add(new MapLine { V1 = 0, V2 = 1, Front = 0 });
        return EditorMap.FromModel(m);
    }

    private static SetProperty<Sector, double> FloorTo(Sector s, double h) => new(s, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, h);

    [Fact]
    public void AnEditUndoesAndRedoes()
    {
        var map = Square();
        var history = new History(map);
        var sector = map.Sectors[0];
        var v0 = map.Version;
        history.Run(FloorTo(sector, 32));
        Assert.Equal(32, sector.FloorHeight);
        Assert.Equal("Change floor height", history.UndoLabel);
        Assert.True(history.Undo());
        Assert.Equal(0, sector.FloorHeight);
        Assert.True(history.Redo());
        Assert.Equal(32, sector.FloorHeight);
        Assert.Equal(v0 + 3, map.Version);
    }

    [Fact]
    public void ADragIsOneStep()
    {
        var map = Square();
        var history = new History(map);
        var sector = map.Sectors[0];
        var drag = History.NewGesture();
        foreach (var h in new[] { 8.0, 16, 24, 40 }) history.Run(FloorTo(sector, h), drag);
        Assert.Single(history.Steps);
        history.Undo();
        Assert.Equal(0, sector.FloorHeight);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ANewEditClearsTheRedoSteps()
    {
        var map = Square();
        var history = new History(map);
        history.Run(FloorTo(map.Sectors[0], 8));
        history.Undo();
        Assert.True(history.CanRedo);
        history.Run(FloorTo(map.Sectors[0], 16));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void ACheckpointStopsADragFromMergingAcrossIt()
    {
        var map = Square();
        var history = new History(map);
        var drag = History.NewGesture();
        history.Run(FloorTo(map.Sectors[0], 8), drag);
        history.Checkpoint("Played");
        history.Run(FloorTo(map.Sectors[0], 16), drag);
        Assert.Equal(2, history.Steps.Count);
        Assert.Equal("Played", history.Steps[0].Checkpoint);
    }

    [Fact]
    public void ABatchUndoesInReverseOrder()
    {
        var map = Square();
        var history = new History(map);
        var s = map.Sectors[0];
        history.Run(new Batch("Raise and light", [FloorTo(s, 16), new SetProperty<Sector, int>(s, "Change brightness", x => x.Light, (x, v) => x.Light = v, 255)]));
        Assert.Equal((16.0, 255), (s.FloorHeight, s.Light));
        history.Undo();
        Assert.Equal((0.0, 160), (s.FloorHeight, s.Light));
    }

    [Fact]
    public void ElementsKeepTheirIdentityAcrossEdits()
    {
        var map = Square();
        var line = map.Lines[0];
        var id = line.Id;
        var history = new History(map);
        history.Run(new SetProperty<Vertex, double>(line.End, "Move corner", v => v.X, (v, x) => v.X = x, 256));
        Assert.Same(line, map.Lines[0]);
        Assert.Equal(id, line.Id);
        Assert.Equal(256, line.Length);
    }

    [Fact]
    public void SelectionPrunesWhatIsGone()
    {
        var map = Square();
        var sel = new Selection();
        var ghost = new Thing();
        sel.Set(map.Lines[0], ghost);
        sel.Prune(map);
        Assert.Equal([map.Lines[0]], sel.Items);
    }
}
