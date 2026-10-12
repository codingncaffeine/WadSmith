using WadSmith.Editor.App;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// The feature tour (D32): its script runs line for line in every game family, and each room holds the feature its sign
/// names, as the editor's own recognisers find it.
/// </summary>
public sealed class FeatureTourTests
{
    [Theory]
    [InlineData("doom")]
    [InlineData("heretic")]
    [InlineData("hexen")]
    [InlineData("strife")]
    [InlineData("chex")]
    public void TheTourIsBuiltForEveryFamilyWithEachFeatureWhereItsSignSays(string engine)
    {
        var map = FeatureTour.Build("MAP01", engine);
        var g = MapGeometry.Build(map.Frame);
        bool IsKey(int type) => Keys.ColourOf(type) is not null;
        // Each room by its rectangle (west, south, east, north), with what the finder must see in it.
        (string Kind, double X0, double Y0, double X1, double Y1)[] rooms =
        [
            ("door", 0, 256, 512, 768), ("lift", 512, 256, 1024, 768), ("stairs", 1024, 256, 1536, 768), ("window", 1536, 256, 2048, 768),
            ("secret", 0, -640, 512, 0), ("switch", 1024, -768, 1536, 0), ("teleporter", 1536, -512, 2048, 0), ("exit", 2048, -512, 2560, 0),
        ];
        var found = Landmarks.Find(map, g, IsKey);
        bool In(string kind, double x0, double y0, double x1, double y1) => found.Any(f => f.Kind == kind && f.At.X >= x0 && f.At.X <= x1 && f.At.Y >= y0 && f.At.Y <= y1);
        Assert.Empty(rooms.Where(r => !In(r.Kind, r.X0, r.Y0, r.X1, r.Y1)).Select(r => r.Kind));
        Assert.Equal(engine is "doom" or "chex", In("locked door", 512, -768, 1024, 0) && In("key", 512, -512, 1024, 0));
        var signs = map.Sectors.Count(s => s.OnMap && s.Extra.TryGet(Signs.Field, out var v) && v.AsString().Length > 0);
        Assert.Equal(engine is "doom" or "chex" ? 11 : 10, signs);
        Assert.Contains(map.Things, t => t.OnMap && Lights.Kind(t.Type) is not null);
    }
}

/// <summary>The tour in the window: the Guide menu opens it read-only with its signs; Remix makes the user's own copy, signs and all, that changes.</summary>
[Collection(TimingCollection.Name)]
public sealed class FeatureTourInTheWindowTests
{
    [Fact]
    public async Task TheGuideMenuOpensTheTourAndRemixCopiesIt()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var editor = Ui.NewMap(vm);
                editor.GuideMenu.Single(l => l.Title.StartsWith("Feature tour", StringComparison.Ordinal)).Open.Execute(null);
                for (var i = 0; i < 400 && ReferenceEquals(vm.Page, editor); i++) Ui.Pump(0.05);
                var tour = (App.ViewModels.EditorViewModel)vm.Page!;
                var signs = tour.Session.Map.Sectors.Count(s => s.OnMap && s.Extra.TryGet(Signs.Field, out _));
                said.Add($"tour: read-only {tour.Session.History.ReadOnly}, landmark {tour.Peek?.Landmark is null}, signs {signs}, handler {File.Exists(Path.Combine(tour.Session.Project.Folder, "zscript.txt"))}");
                Ui.Wait(tour.RemixPeekCommand.ExecuteAsync(null));
                for (var i = 0; i < 400 && vm.Page is not App.ViewModels.EditorViewModel { Peek: null }; i++) Ui.Pump(0.05);
                var mine = (App.ViewModels.EditorViewModel)vm.Page!;
                var folder = mine.Session.Project.Folder;
                said.Add($"mine: read-only {mine.Session.History.ReadOnly}, in projects {folder.StartsWith(App.App.Services!.Paths.Projects, StringComparison.Ordinal)}, handler {File.Exists(Path.Combine(folder, "zscript.txt"))}, mapinfo kept {File.ReadAllText(Path.Combine(folder, "zmapinfo.txt")).Contains("WadSmithSigns", StringComparison.Ordinal)}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(["tour: read-only True, landmark True, signs 11, handler True", "mine: read-only False, in projects True, handler True, mapinfo kept True"], log);
    }
}
