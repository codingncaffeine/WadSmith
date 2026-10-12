using WadSmith.Editor.App;

namespace WadSmith.Cli.Commands;

/// <summary>Writes WadSmith's feature tour (D32) as a project, for playing it in the engine.</summary>
internal sealed class TourCommand : ICommand
{
    public string Name => "tour";

    public string Summary => "Write the feature tour as a project folder for a game.";

    public string Usage => """
        Usage: wadsmith-cli tour FOLDER --game=ID [--engine=doom] [--map=MAP01]

        Builds the feature tour (a room for each feature, each with a sign the game shows) for the
        game ID (an id from data/games/games.json) in a new project at FOLDER, with the sign handler
        (zscript.txt) and the MAPINFO that registers it. Play it with -file FOLDER.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var game = args.Option("game") ?? throw new UsageException("tour needs --game=ID.");
        var engine = args.Option("engine") ?? "doom";
        var slot = args.Option("map") ?? "MAP01";
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("tour takes one folder.");
        var project = FeatureTour.Create(args.Positional[0], game, engine, slot);
        output.WriteLine($"tour: {project.Folder} ({slot})");
        return ExitCode.Ok;
    }
}
