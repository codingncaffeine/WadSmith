using WadSmith.Core.Containers;
using WadSmith.Core.Exporting;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Cli.Commands;

/// <summary>Exports a project as one file for GZDoom or a classic port, and prints what the export lost (Phase 12).</summary>
internal sealed class ExportCommand : ICommand
{
    public string Name => "export";

    public string Summary => "Export a project as a .pk3 for GZDoom or a .wad for a classic port, with what it loses.";

    public string Usage => """
        Usage: wadsmith-cli export PROJECT_FOLDER --for=gzdoom|vanilla|limit-removing|boom|mbf21 --out=FILE
                                   [--engine-data=gzdoom.pk3] [--games=FOLDER] [--home=FOLDER] [--verbose]

        Writes the project as one file: for GZDoom the project folder as a .pk3, for a classic port every map
        in the classic format with WadSmith's nodes in a .wad (Boom and MBF21 also get a UMAPINFO with the maps'
        names, music, skies and next maps). Prints what the export lost, map by map: what the port has no type,
        flag or field for, what it does not know (objects, the game's pictures, found in --games or the usual
        folders), and the format's limits. Exit code 0 when nothing in the export stops the port with an error,
        1 when something does (the file is written either way).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var exportFor = args.Option("for") ?? throw new UsageException("export needs --for=gzdoom|vanilla|limit-removing|boom|mbf21.");
        var outFile = args.Option("out") ?? throw new UsageException("export needs --out=FILE.");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        var games = args.Option("games");
        var home = args.Option("home");
        var verbose = args.Flag("verbose");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("export takes one project folder.");
        if (exportFor != ProjectExport.GZDoom && ClassicPorts.Get(exportFor) is null) throw new UsageException($"--for takes {ProjectExport.GZDoom}, {string.Join(", ", ClassicPorts.All.Select(p => p.Id))}.");

        string? Env(string name) => name == "HOME" && home is not null ? home : Environment.GetEnvironmentVariable(name);
        var project = Project.Open(args.Positional[0]);
        PictureNames? pictures = null;
        if (exportFor != ProjectExport.GZDoom)
        {
            var found = GameLocator.Scan([.. GameLocator.StandardFolders(Env), .. games is null ? Array.Empty<string>() : [games]]);
            if (found.FirstOrDefault(g => g.Game.Id == project.Info.Game) is { } game)
            {
                var stack = new ResourceStack();
                if (game.Game.AddOnFor is { } baseId && found.FirstOrDefault(g => g.Game.Id == baseId) is { } baseGame) stack.Add(ResourceContainer.Open(baseGame.Path));
                stack.Add(ResourceContainer.Open(game.Path));
                pictures = ProjectExport.PicturesOf(stack);
            }
            else
            {
                error.WriteLine($"The project's game ({project.Info.Game}) was not found, so its pictures are not checked.");
            }
        }

        var report = ProjectExport.Write(project, exportFor, outFile, enginePath, pictures);
        output.WriteLine($"Wrote {Path.GetFullPath(outFile)}.");
        output.Write(report.ToText(verbose));
        return report.Breaks ? ExitCode.Failed : ExitCode.Ok;
    }
}
