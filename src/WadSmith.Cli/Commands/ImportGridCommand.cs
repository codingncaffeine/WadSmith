using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Grids;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Cli.Commands;

/// <summary>Turns a Wolfenstein 3D level into rooms, as a map of a project (Phase 11's grid maps).</summary>
internal sealed class ImportGridCommand : ICommand
{
    public string Name => "import-grid";

    public string Summary => "Turn a Wolfenstein 3D level into rooms, doors and walls: a new map of a project.";

    public string Usage => """
        Usage: wadsmith-cli import-grid PROJECT MAPHEAD.WL6 [--level=1] [--map=SLOT] [--games=FOLDER]

        Reads the level (MAPHEAD and GAMEMAPS of Wolfenstein 3D, its shareware or Spear of Destiny) from your own copy and
        writes it as a map of the project: each run of floor tiles a room, each door a door the player opens, the walls in
        the game's pictures, tiles 128 units square. --level counts from 1; --map names the slot (the next free one when
        left out).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var levelText = args.Option("level") ?? "1";
        var slot = args.Option("map");
        var games = args.Option("games");
        args.RejectUnread();
        if (args.Positional.Count != 2) throw new UsageException("import-grid takes a project folder and a MAPHEAD file.");
        if (!int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 1) throw new UsageException("--level is a number from 1.");
        try
        {
            var project = Project.Open(args.Positional[0]);
            var levels = WolfMaps.Read(args.Positional[1]);
            if (number > levels.Count) throw new UsageException($"The file holds {levels.Count} levels.");
            var game = GameCatalog.Get(project.Info.Game) ?? throw new CorruptDataException($"The project's game ({project.Info.Game}) is not one WadSmith knows.");
            var found = GameLocator.Scan([.. GameLocator.StandardFolders(Environment.GetEnvironmentVariable), .. games is null ? Array.Empty<string>() : [games]]);
            var resources = GameResources.Load(project.Info.Game, project.Folder, found, EngineData.Find());
            var look = Look.For(game.Engine);
            var walls = Look.Available(game.Engine, resources).Select(l => l.Look.Wall).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            slot ??= GridRooms.NextSlot(project);
            var level = levels[number - 1];
            var map = GridRooms.Build(GridRooms.FromWolf(level), slot, look, walls);
            project.SaveMap(map, level.Name);
            output.WriteLine($"{slot}: {level.Name}, {map.Sectors.Count} rooms and doors, {map.Lines.Count} walls.");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
