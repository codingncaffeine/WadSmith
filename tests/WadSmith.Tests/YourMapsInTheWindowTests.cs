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
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var said = new List<string>();
                var editor = Ui.NewMap(vm);
                var game = editor.Session.Project.Info.Game;
                Ui.Wait(editor.CloseCommand.ExecuteAsync(null));
                Ui.Wait(vm.Welcome.RefreshProjectsAsync());
                var tile = vm.Welcome.Games.Single(t => t.Found.Game.Id == game);
                string Listed() => $"{tile.ProjectsLabel}: {string.Join(", ", tile.Projects.Select(p => p.Name))}; others {vm.Welcome.OtherProjects.Count}";
                said.Add(Listed());

                var mine = tile.Projects.Single();
                mine.RenameCommand.Execute(null);
                mine.NewName = "Cave";
                Ui.Wait(mine.CommitRenameCommand.ExecuteAsync(null));
                said.Add($"{Listed()}; sidecar {Project.Open(mine.Folder).Info.Name}");

                Ui.Wait(tile.Projects.Single().CopyCommand.ExecuteAsync(null));
                said.Add(Listed());

                var copy = tile.Projects.Single(p => p.Name == "Cave copy");
                Ui.Wait(copy.DeleteCommand.ExecuteAsync(null));
                var deleted = Directory.Exists(Path.Combine(App.App.Services!.Paths.Projects, Project.DeletedFolderName)) ? Directory.GetDirectories(Path.Combine(App.App.Services.Paths.Projects, Project.DeletedFolderName)).Length : 0;
                said.Add($"{Listed()}; in Deleted {deleted}; note {vm.Welcome.ProjectsNote.Split('.')[0]}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "Your maps · 1: My map; others 0",
            "Your maps · 1: Cave; others 0; sidecar Cave",
            "Your maps · 2: Cave copy, Cave; others 0",
            "Your maps · 1: Cave; others 0; in Deleted 1; note Cave copy moved to " + Path.Combine(App.App.Services!.Paths.Projects, Project.DeletedFolderName) + "/Cave copy",
        ], seen.Select((s, i) => i == 3 ? s[..s.IndexOf("/Cave copy", StringComparison.Ordinal)] + "/Cave copy" : s));
    }
}
