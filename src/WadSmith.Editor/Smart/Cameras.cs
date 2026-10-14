using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor.Editing;

namespace WadSmith.Editor.Smart;

/// <summary>A camera screen: the texture the wall shows, the camera it shows (by its id, in one map) and its field of view.</summary>
public sealed record CameraScreen(string Texture, int Tid, string Slot, int Fov, int Width, int Height);

/// <summary>
/// Security camera screens (§3.5, Phase 7): a wall shows what a camera placed elsewhere in the map sees. In the editor the wall
/// shows a stand-in picture of a screen; in the game ANIMDEFS' <c>cameratexture</c> of the same name replaces it (how map
/// editors show camera textures), and an event handler WadSmith writes points each map's camera at its screen when the
/// map starts. The camera is GZDoom's SecurityCamera, held still.
/// </summary>
public static class Cameras
{
    /// <summary>GZDoom's SecurityCamera (its arguments: pitch, then the sweep's width and time; no sweep holds it still).</summary>
    public const int SecurityCamera = 9025;

    public const string Handler = "WadSmithCameras";

    public const string ScriptFile = "zscript/wadsmith/WadSmithCameras.zs";

    /// <summary>The field of view a new screen shows, in degrees.</summary>
    public const int Fov = 90;

    /// <summary>The camera picture's width in pixels; its height follows the wall's shape.</summary>
    public const int Pixels = 256;

    /// <summary>The project's camera screens.</summary>
    public static IReadOnlyList<CameraScreen> Screens(IReadOnlyList<CopiedAsset> copied)
    {
        var screens = new List<CameraScreen>();
        var c = CultureInfo.InvariantCulture;
        foreach (var r in copied.Where(r => r.Kind == "camera"))
        {
            var p = r.Name.Split('|');
            if (p.Length < 5) continue;
            screens.Add(new CameraScreen(r.Original, int.Parse(p[0], c), p[1], int.Parse(p[2], c), int.Parse(p[3], c), int.Parse(p[4], c)));
        }

        return screens;
    }

    /// <summary>
    /// Makes a screen for a wall <paramref name="width"/> × <paramref name="height"/> map units in map <paramref name="slot"/>,
    /// showing the camera with id <paramref name="tid"/>: its stand-in picture (a texture of the user's own, so it goes on as
    /// a poster), its ANIMDEFS line and the handler. Call it on a worker.
    /// </summary>
    public static CameraScreen Make(Project project, GameResources into, string slot, int tid, double width, double height)
    {
        var tall = Math.Clamp((int)Math.Round(Pixels * height / Math.Max(1, width)), 32, 512);
        var name = FreeName(into);
        var file = $"textures/{name}.png";
        var picture = SignPictures.Render(new SignSettings("CAMERA", SignLettering.Mono, 0x80C8FF, SignBack.Dark), Pixels, tall, null, into);
        if (Math.Max(picture.Width, picture.Height) > Math.Max(Pixels, tall)) picture = ModelProps.Shrink(picture, Math.Max(Pixels, tall));
        var (w, h) = (picture.Width, picture.Height);
        project.WriteFiles([(file, ImageCodec.EncodePng(picture))]);
        into.Textures.Add(ResourceContainer.Open(project.Folder).FindPath(file) ?? throw new CorruptDataException($"{file} was written but could not be read back."));
        var c = CultureInfo.InvariantCulture;
        project.Record(
        [
            new CopiedAsset { Kind = "wall", Game = "picture", Original = "camera screen", Name = name, File = file },
            new CopiedAsset { Kind = "camera", Game = "picture", Original = name, Name = string.Join('|', tid.ToString(c), slot, Fov.ToString(c), w.ToString(c), h.ToString(c)), File = Pictures.AnimationFile },
        ]);
        if (project.FindCopy("picture", Handler, "eventhandler") is null)
        {
            project.Record(
            [
                new CopiedAsset { Kind = "zscript", Game = "picture", Original = Handler, Name = Handler, File = ScriptFile },
                new CopiedAsset { Kind = "eventhandler", Game = "picture", Original = Handler, Name = Handler, File = ProjectMapInfo.FileName },
            ]);
        }

        Write(project);
        return Screens(project.CopiedNow()).Last();
    }

    /// <summary>Writes the handler and the ANIMDEFS lines from the project's screens.</summary>
    public static void Write(Project project)
    {
        project.WriteFile(ScriptFile, Encoding.UTF8.GetBytes(Declare(Screens(project.CopiedNow()))));
        DukeBorrowing.WriteScript(project);
        Pictures.WriteAnimations(project);
    }

    /// <summary>The handler: when a map starts, each of its screens is set to show its camera.</summary>
    public static string Declare(IReadOnlyList<CameraScreen> screens)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder("// Written by WadSmith: the camera screens of the project's maps. Edits here are replaced.\n");
        text.Append("class ").Append(Handler).Append(" : EventHandler\n{\n\toverride void WorldLoaded(WorldEvent e)\n\t{\n");
        foreach (var s in screens) text.Append(c, $"\t\tif (Level.MapName ~== \"{s.Slot}\") Show({s.Tid}, \"{s.Texture}\", {s.Fov});\n");
        text.Append("\t}\n\n\tvoid Show(int tid, string texture, double fov)\n\t{\n\t\tlet cameras = Level.CreateActorIterator(tid);\n");
        text.Append("\t\tActor camera = cameras.Next();\n\t\tif (camera) TexMan.SetCameraToTexture(camera, texture, fov);\n\t}\n}\n");
        return text.ToString();
    }

    private static string FreeName(GameResources into)
    {
        for (var n = 1; n < 10000; n++)
        {
            var name = "CAM" + n.ToString("0000", CultureInfo.InvariantCulture);
            if (!into.Textures.Has(name)) return name;
        }

        throw new CorruptDataException("Every camera screen name is taken in this project.");
    }
}
