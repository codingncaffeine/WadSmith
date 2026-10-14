using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>
/// Hubs (Phase 9): a map's settings put it in one of the game's hubs, in none, or in a hub of the project's own, which the
/// project's zmapinfo.txt defines; the command line takes the same and refuses a hub the game does not have.
/// </summary>
public sealed class HubTests
{
    private static async Task<(TempFolder Temp, EditorSession Session, InspectorViewModel Inspector, Func<Task> Settle)> Open(string gameId, string wadName, string slot)
    {
        var temp = new TempFolder();
        var game = GameCatalog.Get(gameId)!;
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", gameId);
        project.SaveMap(StarterRoom.Create(slot, gameId == "hexen" ? "hexen" : "doom"));
        var wad = GameFiles.Require(wadName);
        var engine = GameFiles.Require("gzdoom.pk3");
        var resources = GameResources.Load(gameId, project.Folder, [new FoundGame(game, wad, new FileInfo(wad).Length)], engine)!;
        var ui = new ConcurrentQueue<Action>();
        var session = EditorSession.Open(project, slot, new SessionHooks(ui.Enqueue, resources, EngineData: engine));
        async Task Settle()
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            do
            {
                while (ui.TryDequeue(out var a)) a();
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            while ((session.Defaults is null || session.Geometry.Version != session.Map.Version || !ui.IsEmpty) && DateTime.UtcNow < until);
        }

        await Settle();
        var inspector = new InspectorViewModel(session);
        await Settle();
        return (temp, session, inspector, Settle);
    }

    [Fact]
    public async Task AHexenMapGoesIntoAnotherHubNoneOrOneOfTheProjectsOwn()
    {
        var (temp, session, inspector, settle) = await Open("hexen", "HEXEN.WAD", "MAP02");
        using var _ = temp;
        using var __ = session;
        ChoiceRow Hub() => inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Hub");
        Assert.Equal(
        [
            "The game's hub 1 (MAP01–MAP06)", "No hub", "The game's hub 2 (MAP08–MAP13)", "The game's hub 3 (MAP27, MAP28, MAP30 and 4 more)",
            "The game's hub 4 (MAP21–MAP26)", "The game's hub 5 (MAP35–MAP40)", "A new hub of this project's",
        ], Hub().Options);
        Assert.Equal(0, Hub().Value);

        // A new hub of the project's own: the map's block names it, and the file defines it a hub.
        Hub().Value = Hub().Options.Count - 1;
        await settle();
        Assert.Equal(ProjectMapInfo.FirstOwnHub, session.MapEntry.Hub);
        Assert.Equal("This project's hub 100 (MAP02)", Hub().Options[Hub().Value]);
        Assert.Equal("A new hub of this project's", Hub().Options[^1]);
        var flush = session.FlushAsync();
        while (!flush.IsCompleted) await settle();
        var text = File.ReadAllText(Path.Combine(session.Project.Folder, ProjectMapInfo.FileName));
        Assert.Contains("\tcluster = 100\n", text, StringComparison.Ordinal);
        Assert.Contains("cluster 100\n{\n\thub\n}\n", text, StringComparison.Ordinal);
        Assert.Contains("levelnum = 2", text, StringComparison.Ordinal);

        // Out of every hub, then into the second of the game's.
        Hub().Value = Hub().Options.ToList().IndexOf("No hub");
        await settle();
        Assert.Equal(0, session.MapEntry.Hub);
        Hub().Value = Hub().Options.ToList().IndexOf("The game's hub 2 (MAP08–MAP13)");
        await settle();
        Assert.Equal(2, session.MapEntry.Hub);

        // Back to the game's own: nothing of the map's own is left to write.
        Hub().Value = 0;
        await settle();
        Assert.Null(session.MapEntry.Hub);
    }

    [Fact]
    public async Task ADoomIIMapHasNoGameHubToJoin()
    {
        var (temp, session, inspector, _) = await Open("doom2", "DOOM2.WAD", "MAP01");
        using var _1 = temp;
        using var _2 = session;
        Assert.Equal(["No hub (the game's)", "A new hub of this project's"], inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Hub").Options);
    }

    [Fact]
    public void TheCommandLineTakesTheGamesHubsAndTheProjectsOwn()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "hexen");
        project.SaveMap(StarterRoom.Create("MAP02", "hexen"));
        var games = Path.GetDirectoryName(GameFiles.Require("HEXEN.WAD"))!;
        var engine = GameFiles.Require("gzdoom.pk3");
        (int Code, string Out, string Error) Run(string hub)
        {
            var (output, error) = (new StringWriter(), new StringWriter());
            try
            {
                var code = new Cli.Commands.MapSettingsCommand().Run(Cli.CommandArgs.Parse([project.Folder, "--map=MAP02", $"--hub={hub}", $"--games={games}", $"--engine-data={engine}"]), output, error);
                return (code, output.ToString(), error.ToString());
            }
            catch (Cli.UsageException e)
            {
                return (2, output.ToString(), e.Message);
            }
        }

        var refused = Run("7");
        Assert.Equal(2, refused.Code);
        Assert.Contains("Hexen has no hub 7 (its hubs are 1, 2, 3, 4, 5); the project's own hubs are numbered from 100.", refused.Error, StringComparison.Ordinal);
        Assert.Null(Project.Open(project.Folder).Map("MAP02")!.Hub);

        var own = Run("100");
        Assert.Equal(0, own.Code);
        Assert.Contains("hub 100", own.Out, StringComparison.Ordinal);
        Assert.Contains("cluster 100\n{\n\thub\n}\n", own.Out, StringComparison.Ordinal);
        Assert.Equal(3, Run("3").Code is 0 ? Project.Open(project.Folder).Map("MAP02")!.Hub : -1);
        Assert.Equal(0, Run("none").Code is 0 ? Project.Open(project.Folder).Map("MAP02")!.Hub : -1);
        Assert.Null(Run("game").Code is 0 ? Project.Open(project.Folder).Map("MAP02")!.Hub : -1);
    }
}
