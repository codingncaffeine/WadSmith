using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// The landmark index (D28): every entry opens a spot in the game's own map that shows the feature it names, checked
/// with the editor's own recognisers on the copy of each game found here (Freedoom on CI, the user's games locally).
/// </summary>
public sealed class LandmarkTests
{
    private static readonly Lazy<IReadOnlyList<FoundGame>> Games = new(() => GameLocator.Scan(GameFiles.AllWads.Select(Path.GetDirectoryName).Distinct()!));

    public static TheoryData<string> IndexedGames() => [.. Landmarks.All.Select(l => l.Game).Distinct()];

    [Theory]
    [MemberData(nameof(IndexedGames))]
    public void EveryEntryShowsItsFeatureInTheGamesOwnMap(string game)
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        var found = Games.Value.FirstOrDefault(g => Landmarks.IndexId(g.Game.Id) == game && !g.Game.Id.EndsWith("-shareware", StringComparison.Ordinal));
        if (found is null) Assert.Skip($"{game} is not among the test games.");
        var things = ThingCatalog.Load(ResourceContainer.Open(engine), found.Game.Engine);
        bool IsKey(int type) => things.Find(type)?.Category == ThingCategory.Key;
        var wrong = new List<string>();
        foreach (var map in Landmarks.All.Where(l => l.Game == game).GroupBy(l => l.Map))
        {
            var editorMap = Landmarks.Read(found.Path, map.Key, engine, found.Game.MapNamespace) ?? throw new InvalidOperationException($"{found.Path} has no {map.Key}");
            var geometry = MapGeometry.Build(editorMap.Frame);
            wrong.AddRange(map.Where(l => !Landmarks.Shows(l.Kind, l.At, editorMap, geometry, IsKey)).Select(l => $"{l.Map} {l.Kind} at {l.X}, {l.Y}"));
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void ASpotBesideTheFeatureDoesNotCount()
    {
        // The control: each Freedoom entry moved 256 units away no longer shows its feature (for most kinds), so the check can fail.
        var engine = GameFiles.Require("gzdoom.pk3");
        var wad = GameFiles.Require("freedoom2.wad");
        var things = ThingCatalog.Load(ResourceContainer.Open(engine), "doom");
        bool IsKey(int type) => things.Find(type)?.Category == ThingCategory.Key;
        var map = Landmarks.Read(wad, "MAP01", engine, "doom")!;
        var geometry = MapGeometry.Build(map.Frame);
        var entries = Landmarks.All.Where(l => l.Game == "freedoom2" && l.Map == "MAP01").ToList();
        Assert.NotEmpty(entries);
        var moved = entries.Count(l => Landmarks.Shows(l.Kind, new MapPoint(l.X + 256, l.Y + 256), map, geometry, IsKey));
        Assert.True(moved <= entries.Count / 4, $"{moved} of {entries.Count} still show their feature 256 units away");
        Assert.Equal("doom", Landmarks.IndexId("doom-ultimate"));
        Assert.Equal("doom", Landmarks.For("door", ["doom-ultimate"])!.Game);
        Assert.Equal("freedoom2", Landmarks.For("exit", ["hexen", "freedoom2"])!.Game);
        Assert.Null(Landmarks.For("exit", ["hexen"]));
    }
}

/// <summary>
/// See it in a real map in the window: the Door tool's button opens a game's own map at a door, selected, where nothing
/// changes; Back returns to the same map as it was left, and Remix makes a project of the map that can change.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class LandmarkInTheWindowTests
{
    [Fact]
    public async Task TheDoorToolOpensARealDoorThatNothingChangesAndBackAndRemixWork()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var editor = Ui.NewMap(vm);
                said.Add($"select tool: {editor.SeeOne?.Title ?? "no button"}");
                editor.Tool = App.Canvas.EditorTool.Door;
                said.Add($"door tool: {(editor.SeeOne is null ? "no button" : "button")}");
                editor.SeeOne!.Open.Execute(null);
                for (var i = 0; i < 400 && ReferenceEquals(vm.Page, editor); i++) Ui.Pump(0.05);
                var peek = (App.ViewModels.EditorViewModel)vm.Page!;
                var session = peek.Session;
                var at = peek.Peek!.Landmark.At;
                var shows = Landmarks.Shows("door", at, session.Map, session.Geometry, _ => false);
                said.Add($"peek: {peek.Peek.Landmark.Kind}, shows {shows}, selected {session.Selection.Items.SingleOrDefault() is Editor.Model.Sector s && Doors.Recognise(session.Geometry, s) is not null}, in the cache {session.Project.Folder.StartsWith(temp.Path, StringComparison.Ordinal) && session.Project.Folder.Contains("peek", StringComparison.Ordinal)}");
                var version = session.Map.Version;
                session.Paint("BROWN1", at, 8, ceiling: false);
                session.ChangeMapSettings(m => m with { Title = "Changed" });
                Ui.Pump(0.2);
                said.Add($"after edits: map changed {session.Map.Version != version}, undo {session.History.CanUndo}, title '{session.MapEntry.Title}'");
                peek.BackFromPeekCommand.Execute(null);
                Ui.Pump(0.3);
                said.Add($"back: same editor {ReferenceEquals(vm.Page, editor)}");

                editor.SeeOne!.Open.Execute(null);
                for (var i = 0; i < 400 && ReferenceEquals(vm.Page, editor); i++) Ui.Pump(0.05);
                Ui.Wait(((App.ViewModels.EditorViewModel)vm.Page!).RemixPeekCommand.ExecuteAsync(null));
                for (var i = 0; i < 400 && vm.Page is not App.ViewModels.EditorViewModel { Peek: null }; i++) Ui.Pump(0.05);
                var remix = (App.ViewModels.EditorViewModel)vm.Page!;
                said.Add($"remix: read-only {remix.Session.History.ReadOnly}, a project of its own {!ReferenceEquals(remix, editor) && remix.Session.Project.Folder.StartsWith(App.App.Services!.Paths.Projects, StringComparison.Ordinal)}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            "select tool: no button",
            "door tool: button",
            "peek: door, shows True, selected True, in the cache True",
            "after edits: map changed False, undo False, title ''",
            "back: same editor True",
            "remix: read-only False, a project of its own True",
        ], log);
    }
}
