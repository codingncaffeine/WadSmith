using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Cli.Commands;

internal sealed class RemixCommand : ICommand
{
    public string Name => "remix";

    public string Summary => "Start a project from one of a base game's own maps.";

    public string Usage => """
        Usage: wadsmith-cli remix BASEGAME --map=NAME --out=PROJECT_FOLDER [--engine-data=gzdoom.pk3] [--target=gzdoom|vanilla|limit-removing]

        Copies the map into a new project in GZDoom's own map format, ready to edit and play.
        The base game file is only read.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var map = args.Option("map") ?? throw new UsageException("remix needs --map=NAME.");
        var outFolder = args.Option("out") ?? throw new UsageException("remix needs --out=PROJECT_FOLDER.");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        var target = args.Option("target") ?? Targets.GZDoom;
        if (!Targets.IsKnown(target)) throw new UsageException($"--target takes {string.Join(", ", Targets.All.Select(t => t.Id))}.");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("remix takes one base game file.");
        if (enginePath is null)
        {
            error.WriteLine("No engine data file (uzdoom.pk3 or gzdoom.pk3) was found; install UZDoom or GZDoom, or pass --engine-data=PATH.");
            return ExitCode.Failed;
        }

        try
        {
            var notes = new List<string>();
            var project = Remix.Create(outFolder, args.Positional[0], map, enginePath, notes, target: target);
            output.WriteLine($"Project \"{project.Info.Name}\" created in {project.Folder}.");
            foreach (var n in notes) output.WriteLine($"  Note: {n}");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
