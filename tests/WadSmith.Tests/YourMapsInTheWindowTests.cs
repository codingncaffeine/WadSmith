using WadSmith.App.ViewModels;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>Each game's tile lists the maps made for it, with rename, copy and delete; the bottom of the start screen no longer hides them.</summary>
[Collection(TimingCollection.Name)]
public sealed class YourMapsInTheWindowTests
{
    [Fact]
    public async Task AMapMadeForAGameIsOnItsTileAndCanBeRenamedCopiedAndDeleted()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();

        // A map made on an earlier day, before the app starts: it must be on its game's tile after the scan, with nothing done by hand.
        var earlier = Project.Create(Path.Combine(temp.Path, "projects", "Old one"), "Old one", "freedoom2");
        earlier.SaveMap(new Core.Maps.MapModel { Name = "MAP01", Namespace = "zdoom" }, origin: new MapOrigin { Kind = "new" });
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var said = new List<string>();
                var tile = vm.Welcome.Games.Single(t => t.Found.Game.Id == "freedoom2");
                for (var i = 0; i < 100 && tile.Projects.Count == 0; i++) Ui.Pump(0.05);
                string Listed(GameTileViewModel t) => $"{t.ProjectsLabel}: {string.Join(", ", t.Projects.Select(p => p.Name))}; others {vm.Welcome.OtherProjects.Count}";
                said.Add("at start — " + Listed(tile));
                Ui.Wait(tile.Projects.Single().DeleteCommand.ExecuteAsync(null));

                // A new map goes on the tile of whichever game it was made for, as soon as the editor closes.
                var editor = Ui.NewMap(vm);
                var game = editor.Session.Project.Info.Game;
                Ui.Wait(editor.CloseCommand.ExecuteAsync(null));
                tile = vm.Welcome.Games.Single(t => t.Found.Game.Id == game);
                for (var i = 0; i < 100 && tile.Projects.Count == 0; i++) Ui.Pump(0.05);
                said.Add(Listed(tile));

                var mine = tile.Projects.Single();
                mine.RenameCommand.Execute(null);
                mine.NewName = "Cave";
                Ui.Wait(mine.CommitRenameCommand.ExecuteAsync(null));
                said.Add($"{Listed(tile)}; sidecar {Project.Open(mine.Folder).Info.Name}");

                Ui.Wait(tile.Projects.Single().CopyCommand.ExecuteAsync(null));
                said.Add(Listed(tile));

                var copy = tile.Projects.Single(p => p.Name == "Cave copy");
                Ui.Wait(copy.DeleteCommand.ExecuteAsync(null));
                var deleted = Directory.Exists(Path.Combine(App.App.Services!.Paths.Projects, Project.DeletedFolderName)) ? Directory.GetDirectories(Path.Combine(App.App.Services.Paths.Projects, Project.DeletedFolderName)).Length : 0;
                said.Add($"{Listed(tile)}; in Deleted {deleted}; note {vm.Welcome.ProjectsNote.Split('.')[0]}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "at start — Your maps · 1: Old one; others 0",
            "Your maps · 1: My map; others 0",
            "Your maps · 1: Cave; others 0; sidecar Cave",
            "Your maps · 2: Cave copy, Cave; others 0",
            "Your maps · 1: Cave; others 0; in Deleted 2; note Cave copy moved to " + Path.Combine(App.App.Services!.Paths.Projects, Project.DeletedFolderName) + "/Cave copy",
        ], seen.Select((s, i) => i == 4 ? s[..s.IndexOf("/Cave copy", StringComparison.Ordinal)] + "/Cave copy" : s));
    }
}
