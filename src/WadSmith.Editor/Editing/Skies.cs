using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>A sky made from a 360° picture: the name the map's sky setting uses, how big its faces are, and what was said.</summary>
public sealed record MadeSky(string Name, int Size, IReadOnlyList<string> Notes);

/// <summary>
/// Skies from 360° pictures (D16, D42, Phase 11): a panorama or an HDRI photograph becomes a GZDoom skybox — six faces as
/// textures of the project (<c>textures/SKBnnF.png</c>) and a <c>skybox SKYBnn</c> in the project's GLDEFS, which the map's
/// sky setting names like any of the game's skies.
/// </summary>
public static class Skies
{
    /// <summary>The project's GLDEFS WadSmith writes, marked so a GLDEFS of the user's own is left alone.</summary>
    public const string DefinitionFile = "gldefs.txt";

    private const string Mark = "// Written by WadSmith: skies made from 360° pictures. Edits here are replaced.";

    /// <summary>The largest face made: a picture 4096 wide gives 1024-pixel faces, as sharp as it can.</summary>
    public const int MaxFace = 1024;

    private static readonly string[] Letters = ["N", "E", "S", "W", "T", "B"];

    /// <summary>Whether <paramref name="path"/> is a picture a sky can be made from (one Skia reads, or Radiance HDR).</summary>
    public static bool IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".hdr";

    /// <summary>Makes a skybox of <paramref name="path"/> in <paramref name="project"/>, its faces added to <paramref name="into"/>'s textures. Reads, resamples and writes: call it on a worker.</summary>
    public static MadeSky Make(Project project, GameResources into, string path)
    {
        var name = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);
        var picture = RadianceHdr.Looks(bytes) ? RadianceHdr.Decode(bytes, name) : Tga.DecodeAny(bytes, name);
        var notes = new List<string>();
        var ratio = (double)picture.Width / picture.Height;
        if (Math.Abs(ratio - 2) > 0.1) notes.Add($"{name} is {picture.Width}×{picture.Height}; a 360° picture is twice as wide as tall, so the sky may look stretched.");
        var size = Math.Clamp(1 << (int)Math.Floor(Math.Log2(Math.Max(1, picture.Width / 4))), 64, MaxFace);

        var used = project.CopiedNow().Where(c => c.Kind == "skybox").Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = Enumerable.Range(1, 99).First(i => !used.Contains(Number(i)) && !into.Textures.Has(Number(i)));
        var sky = Number(n);
        var faces = Letters.Select(l => string.Create(CultureInfo.InvariantCulture, $"SKB{n:00}{l}")).ToList();
        var cube = Panorama.ToCube(picture, size);
        project.WriteFiles([.. faces.Select((f, i) => ($"textures/{f}.png", ImageCodec.EncodePng(cube[i])))]);
        var container = ResourceContainer.Open(project.Folder);
        foreach (var face in faces) into.Textures.Add(container.FindPath($"textures/{face}.png") ?? throw new CorruptDataException($"textures/{face}.png was written but could not be read back."));
        project.Record(new CopiedAsset { Kind = "skybox", Game = "picture", Original = name, Name = sky, File = DefinitionFile });
        WriteDefinitions(project);
        into.AddSky(sky);
        return new MadeSky(sky, size, notes);

        static string Number(int i) => string.Create(CultureInfo.InvariantCulture, $"SKYB{i:00}");
    }

    /// <summary>The project's skyboxes, by name, from its record.</summary>
    public static IEnumerable<string> Recorded(ProjectInfo info) => info.Copied.Where(c => c.Kind == "skybox").Select(c => c.Name);

    /// <summary>Writes the project's GLDEFS from its skies; a GLDEFS of the user's own is left alone.</summary>
    public static void WriteDefinitions(Project project)
    {
        var path = Path.Combine(project.Folder, DefinitionFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != Mark) return;
        }

        var text = new StringBuilder(Mark).Append("\n\n");
        foreach (var sky in project.CopiedNow().Where(c => c.Kind == "skybox"))
        {
            var number = sky.Name[4..];
            text.Append("skybox ").Append(sky.Name).Append("\n{\n");
            foreach (var letter in Letters) text.Append("\tSKB").Append(number).Append(letter).Append('\n');
            text.Append("}\n\n");
        }

        project.WriteFile(DefinitionFile, Encoding.UTF8.GetBytes(text.ToString()));
    }
}
