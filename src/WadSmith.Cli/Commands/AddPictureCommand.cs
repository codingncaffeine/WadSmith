using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Cli.Commands;

/// <summary>Brings a picture into a project as a texture, as the Textures shelf's "Add a picture or material…" does: still, animated, or a PBR material set.</summary>
internal sealed class AddPictureCommand : ICommand
{
    public string Name => "add-picture";

    public string Summary => "Bring a picture, an animated GIF/WebP, or a PBR material set into a project as a texture.";

    public string Usage => """
        Usage: wadsmith-cli add-picture PROJECT PICTURE [--games=FOLDER] [--engine-data=gzdoom.pk3]

        A still picture becomes textures/NAME.png. An animated GIF, WebP or APNG becomes a texture per frame and an
        ANIMDEFS entry that plays them at the picture's own pace. Any picture of a PBR set (Bricks076_1K-PNG_Color.png,
        brick_wall_001_nor_gl_1k.png...) brings the set: its colour as the texture, its maps where GZDoom finds them.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var games = args.Option("games");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 2) throw new UsageException("add-picture takes a project folder and a picture.");
        try
        {
            var project = Project.Open(args.Positional[0]);
            var found = GameLocator.Scan([.. GameLocator.StandardFolders(Environment.GetEnvironmentVariable), .. games is null ? Array.Empty<string>() : [games]]);
            var resources = GameResources.Load(project.Info.Game, project.Folder, found, enginePath) ?? throw new CorruptDataException($"The project's game ({project.Info.Game}) was not found on this computer; name its folder with --games.");
            if (PbrSet.Of(args.Positional[1]) is not null)
            {
                var material = Materials.Import(project, resources, args.Positional[1]);
                foreach (var note in material.Notes) output.WriteLine($"note: {note}");
                output.WriteLine($"material: texture {material.Texture}, with {Materials.Words(material.Layers)}.");
            }
            else
            {
                var picture = Pictures.Import(project, resources, args.Positional[1]);
                foreach (var note in picture.Notes) output.WriteLine($"note: {note}");
                output.WriteLine($"texture {picture.Texture}, {picture.Width}×{picture.Height}, {picture.Frames} frame(s).");
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
