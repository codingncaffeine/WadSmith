using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Cli.Commands;

/// <summary>Brings a PBR material set into a project as a texture (Phase 11), as the Textures shelf's "Add a material…" does.</summary>
internal sealed class AddMaterialCommand : ICommand
{
    public string Name => "add-material";

    public string Summary => "Bring a PBR material set (colour, normal, roughness, metal, occlusion) into a project as a texture.";

    public string Usage => """
        Usage: wadsmith-cli add-material PROJECT PICTURE [--games=FOLDER] [--engine-data=gzdoom.pk3]

        PICTURE is any picture of the set (Bricks076_1K-PNG_Color.png, brick_wall_001_nor_gl_1k.png...). The colour becomes
        textures/NAME.png and the maps go where GZDoom finds them by name (materials/normalmaps/NAME.png and so on).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var games = args.Option("games");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 2) throw new UsageException("add-material takes a project folder and a picture of the set.");
        try
        {
            var project = Project.Open(args.Positional[0]);
            var found = GameLocator.Scan([.. GameLocator.StandardFolders(Environment.GetEnvironmentVariable), .. games is null ? Array.Empty<string>() : [games]]);
            var resources = GameResources.Load(project.Info.Game, project.Folder, found, enginePath) ?? throw new CorruptDataException($"The project's game ({project.Info.Game}) was not found on this computer; name its folder with --games.");
            var made = Materials.Import(project, resources, args.Positional[1]);
            foreach (var note in made.Notes) output.WriteLine($"note: {note}");
            output.WriteLine($"texture {made.Texture}, with {Materials.Words(made.Layers)}.");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException or KeyNotFoundException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
