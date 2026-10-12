using WadSmith.Core.Engines;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Cli.Commands;

/// <summary>Prints the engine command the editor's Play runs for a project's map (for checks that run it in a nested session).</summary>
internal sealed class PlayCliCommand : ICommand
{
    public string Name => "play";

    public string Summary => "Print the engine command Play runs for a project's map.";

    public string Usage => """
        Usage: wadsmith-cli play PROJECT_FOLDER --print --data=FOLDER [--map=SLOT] [--skill=1-5] [--engine=PATH]
                                 [--games=FOLDER] [--home=FOLDER]

        Prints the command the editor's Play starts, one argument a line, the program first: the base game,
        the project folder (and an add-on the game needs) on top, the map, the difficulty, and the engine
        settings and log WadSmith keeps in --data (the settings copied once from the GZDoom settings in the
        home folder, which is only read). --home stands in for the home folder. The command is only printed:
        the editor plays it.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var print = args.Flag("print");
        var data = args.Option("data");
        var map = args.Option("map");
        var skill = int.TryParse(args.Option("skill"), out var s) ? Math.Clamp(s, 1, 5) : 3;
        var enginePath = args.Option("engine");
        var games = args.Option("games");
        var home = args.Option("home");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("play takes one project folder.");
        if (!print || data is null) throw new UsageException("play prints the command only: pass --print and --data=FOLDER.");

        string? Env(string name) => name == "HOME" && home is not null ? home : Environment.GetEnvironmentVariable(name);
        var project = Project.Open(args.Positional[0]);
        var found = GameLocator.Scan([.. GameLocator.StandardFolders(Env), .. games is null ? Array.Empty<string>() : [games]]);
        var engine = enginePath is not null ? new EngineInstall("Your engine", enginePath, "custom") : EngineLocator.Find(null, Env).FirstOrDefault();
        if (engine is null)
        {
            error.WriteLine("No engine was found: install GZDoom, or pass --engine=PATH.");
            return ExitCode.Failed;
        }

        var slot = map ?? project.Info.Maps.FirstOrDefault()?.Slot ?? "MAP01";
        var config = EngineConfig.Prepare(Path.GetFullPath(data), Env);
        var log = Path.Combine(Path.GetFullPath(data), "engine", "last-play.log");
        var (request, why) = PlayRequests.For(project, slot, found, config, log, skill);
        if (request is null)
        {
            error.WriteLine(why);
            return ExitCode.Failed;
        }

        var (program, arguments) = PlayCommand.For(engine, request);
        output.WriteLine(program);
        foreach (var a in arguments) output.WriteLine(a);
        return ExitCode.Ok;
    }
}
