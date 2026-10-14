using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Cli.Commands;

/// <summary>Brings a 3D model into a project as a prop (Phase 11), as the Objects shelf's "Add a 3D model or picture…" does, and can place it.</summary>
internal sealed class AddModelCommand : ICommand
{
    public string Name => "add-model";

    public string Summary => "Bring a 3D model (OBJ, glTF, MD3, IQM, MD2, MDL) or voxels (VOX, KVX) into a project as a prop, and place it on a map.";

    public string Usage => """
        Usage: wadsmith-cli add-model PROJECT MODEL(.obj|.glb|.gltf|.md3|.iqm|.md2|.mdl|.vox|.kvx) [--at="X Y"] [--map=SLOT] [--games=FOLDER] [--engine-data=gzdoom.pk3]

        Copies the model and its pictures into the project, writes its MODELDEF entry and a decoration class
        with a number of the project's own, and bakes eight sprites from it, as the editor does. With --at it
        is also placed on the map (the project's first map unless --map names one).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var at = args.Option("at");
        var slot = args.Option("map");
        var games = args.Option("games");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 2) throw new UsageException("add-model takes a project folder and a model file.");
        (double X, double Y)? spot = null;
        if (at is not null)
        {
            var parts = at.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) throw new UsageException("--at is two numbers, \"X Y\".");
            spot = (x, y);
        }

        try
        {
            var project = Project.Open(args.Positional[0]);
            var found = GameLocator.Scan([.. GameLocator.StandardFolders(Environment.GetEnvironmentVariable), .. games is null ? Array.Empty<string>() : [games]]);
            var resources = GameResources.Load(project.Info.Game, project.Folder, found, enginePath) ?? throw new CorruptDataException($"The project's game ({project.Info.Game}) was not found on this computer; name its folder with --games.");
            var made = ModelProps.Import(project, resources, args.Positional[1], () => found.FirstOrDefault(g => g.Game.Engine == "quake") is { } quake ? Core.Quake.PakArchive.Open(quake.Path).Read("gfx/palette.lmp") : null);
            ProjectMapInfo.Refresh(project, resources.Game, resources.Files, enginePath);
            foreach (var note in made.Notes) output.WriteLine($"note: {note}");
            var thing = made.Actor.Thing;
            output.WriteLine($"{thing.Name}: class {thing.ClassName}, number {made.Actor.Number}, sprite {thing.Sprite}, {thing.Radius} wide from its middle, {thing.Height} tall; {made.SizeNote}.");
            if (spot is { } p)
            {
                slot ??= project.Info.Maps.FirstOrDefault()?.Slot ?? throw new CorruptDataException("The project has no maps.");
                var map = project.LoadMap(slot);
                map.Things.Add(new MapThing { X = p.X, Y = p.Y, Type = made.Actor.Number });
                project.SaveMap(map);
                output.WriteLine($"placed on {slot} at {p.X.ToString(CultureInfo.InvariantCulture)} {p.Y.ToString(CultureInfo.InvariantCulture)}.");
            }

            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException or KeyNotFoundException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
