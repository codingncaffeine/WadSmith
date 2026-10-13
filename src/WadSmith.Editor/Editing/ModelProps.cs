using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Models;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>A model brought in as a prop: the object it became, how big it was read as, and what was said about it.</summary>
public sealed record ImportedModel(CarriedActor Actor, double Scale, string SizeNote, IReadOnlyList<string> Notes);

/// <summary>
/// 3D models as props in a GZDoom map (D16, D42, Phase 11): the model and its pictures are copied into the project under
/// <c>models/&lt;Class&gt;/</c>, a MODELDEF entry draws it, a ZScript decoration class places it by a number of the project's
/// own, and eight sprites baked from it (<see cref="MeshBaker"/>) show it where models are not drawn — the plan, the shelf,
/// renderers without models and older ports. The model is stood on its base and centred on its middle by MODELDEF's offsets,
/// and its size is read from its height: a model a few units tall was made in metres.
/// </summary>
public static class ModelProps
{
    /// <summary>The project's MODELDEF WadSmith writes: it includes each prop's own entry.</summary>
    public const string DefinitionFile = "modeldef.txt";

    private const string DefinitionMark = "// Written by WadSmith: the 3D models brought into this project. Edits here are replaced.";

    /// <summary>The model files a prop can be made from.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".obj", ".md3", ".iqm"];

    /// <summary>Whether <paramref name="path"/> names a model file WadSmith brings in as a prop.</summary>
    public static bool IsModel(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Map units per metre: a 1.75 m figure stands as tall as Doom's marine (56).</summary>
    public const double UnitsPerMetre = 32;

    /// <summary>GZDoom draws a model's height divided by the game's pixel stretch (FSpriteModelFrame, non-voxels).</summary>
    public const double Stretch = 1.2;

    /// <summary>
    /// How many map units one of the model's units is, from its height: up to 8 units tall it was made in metres; over 1024
    /// in centimetres; otherwise its units are map units. The note says what was read.
    /// </summary>
    public static (double Scale, string Note) SizeOf(Mesh mesh)
    {
        var tall = mesh.Max.Z - mesh.Min.Z;
        if (tall <= 8)
        {
            var scale = UnitsPerMetre * Stretch;
            return (scale, $"read as metres: {tall.ToString("0.##", CultureInfo.InvariantCulture)} m tall, {Math.Max(4, Math.Ceiling(tall * UnitsPerMetre))} units in the map");
        }

        if (tall > 1024)
        {
            var scale = UnitsPerMetre * Stretch / 100;
            return (scale, $"read as centimetres: {Math.Round(tall)} cm tall, {Math.Max(4, Math.Ceiling(tall * UnitsPerMetre / 100))} units in the map");
        }

        return (1, $"read as map units: {Math.Max(4, Math.Ceiling(tall / Stretch))} units tall in the map");
    }

    /// <summary>The model a placed object's number shows in <paramref name="project"/>, by its class; null for any other object.</summary>
    public static string? Of(Project project, int number)
    {
        var actor = project.Info.Copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "model" && c.Name == number.ToString(CultureInfo.InvariantCulture));
        return actor?.Original;
    }

    /// <summary>
    /// Brings the model at <paramref name="path"/> into <paramref name="project"/> as a prop of <paramref name="into"/>'s game.
    /// Reads, bakes and writes files: call it on a worker.
    /// </summary>
    public static ImportedModel Import(Project project, GameResources into, string path)
    {
        if (!IsModel(path)) throw new CorruptDataException($"{Path.GetFileName(path)} is not a model WadSmith brings in yet (it takes {string.Join(", ", Extensions)}).");
        var notes = new List<string>();
        var kind = Path.GetExtension(path).ToLowerInvariant();
        var mesh = kind switch
        {
            ".md3" => Md3Model.Read(path, notes),
            ".iqm" => IqmModel.Read(path, notes),
            _ => ObjModel.Read(path, notes),
        };
        var (scale, sizeNote) = SizeOf(mesh);

        var className = ClassName(project, into, Path.GetFileNameWithoutExtension(path));
        var sprite = SpriteName(project, into);
        var folder = $"models/{className}";
        var files = new List<(string, byte[])>();

        // The pictures beside the model, as PNG: an OBJ written again names them in its usemtl lines (GZDoom reads each as a
        // picture in the model's folder); an MD3 or IQM is copied as it is and MODELDEF skins its surfaces by number.
        var pictures = new string?[mesh.Surfaces.Count];
        for (var s = 0; s < mesh.Surfaces.Count; s++)
        {
            if (mesh.Surfaces[s].Image is not { } image) continue;
            pictures[s] = $"skin{s}.png";
            files.Add(($"{folder}/{pictures[s]}", ImageCodec.EncodePng(image)));
        }

        var modelFile = "model" + (kind is ".md3" or ".iqm" ? kind : ".obj");
        files.Add(($"{folder}/{modelFile}", kind is ".md3" or ".iqm" ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes(ObjModel.Write(mesh, s => pictures[s]))));
        var skins = kind is ".md3" or ".iqm"
            ? string.Concat(pictures.Select((p, s) => p is null ? "" : $"\tSurfaceSkin 0 {s} \"{p}\"\n"))
            : "";

        // Stood on its base, centred on its middle: MODELDEF's offsets are in map units.
        var origin = ((mesh.Min.X + mesh.Max.X) / 2, (mesh.Min.Y + mesh.Max.Y) / 2, mesh.Min.Z);
        var c = CultureInfo.InvariantCulture;
        var s3 = scale.ToString("R", c);
        files.Add(($"{folder}/modeldef.txt", Encoding.UTF8.GetBytes(
            $"Model {className}\n{{\n\tPath \"{folder}\"\n\tModel 0 \"{modelFile}\"\n{skins}\tScale {s3} {s3} {s3}\n" +
            string.Create(c, $"\tOffset {Offset(origin.Item1)} {Offset(origin.Item2)} {Offset(origin.Item3)}\n") +
            $"\tFrameIndex {sprite} A 0 0\n}}\n")));

        string Offset(float at) => (Math.Round(-at * scale, 3) + 0.0).ToString("0.###", c);

        // Eight sprites, one per rotation.
        for (var rotation = 1; rotation <= 8; rotation++)
        {
            files.Add(($"sprites/{sprite}A{rotation}.png", ImageCodec.EncodePng(MeshBaker.Bake(mesh, scale, origin, rotation, Stretch))));
        }

        var radius = Math.Max(4, (int)Math.Ceiling(mesh.Reach * scale));
        var height = Math.Max(4, (int)Math.Ceiling((mesh.Max.Z - mesh.Min.Z) * scale / Stretch));
        var title = Title(Path.GetFileNameWithoutExtension(path));
        var script = $"zscript/wadsmith/{className}.zs";
        files.Add((script, Encoding.UTF8.GetBytes(Declare(className, title, sprite, radius, height))));
        project.WriteFiles(files);

        var number = FreeNumber(project, into);
        project.Record(
        [
            new CopiedAsset { Kind = "zscript", Game = "model", Original = className, Name = className, File = script },
            new CopiedAsset { Kind = "modeldef", Game = "model", Original = className, Name = className, File = $"{folder}/modeldef.txt" },
            new CopiedAsset { Kind = "actor", Game = "model", Original = className, Name = number.ToString(c), File = script },
            new CopiedAsset { Kind = "modelprop", Game = "model", Original = className, Name = string.Join('|', sprite, radius.ToString(c), height.ToString(c), title, Path.GetFileName(path)), File = folder },
        ]);
        DukeBorrowing.WriteScript(project);
        WriteDefinitions(project);

        var thing = ThingOf(number, className, sprite, radius, height, title);
        into.Carry([thing], project.Folder);
        return new ImportedModel(new CarriedActor(number, thing, 8, 0, true), scale, sizeNote, notes);
    }

    /// <summary>The catalogue entry of a prop: a solid decoration showing its baked sprites.</summary>
    public static ThingType ThingOf(int number, string className, string sprite, int radius, int height, string title) =>
        new(number, className, title, sprite, 'A', radius, height, 1, 1, false, ThingCategory.Decoration);

    /// <summary>The props a project's record lists, as catalogue entries (for <see cref="GameResources.Load"/>).</summary>
    public static IEnumerable<ThingType> Recorded(ProjectInfo info)
    {
        foreach (var prop in info.Copied.Where(c => c.Kind == "modelprop"))
        {
            var parts = prop.Name.Split('|');
            var number = info.Copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "model" && c.Original == prop.Original)?.Name;
            if (parts.Length < 4 || number is null) continue;
            yield return ThingOf(int.Parse(number, CultureInfo.InvariantCulture), prop.Original, parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), parts[3]);
        }
    }

    /// <summary>The class: a solid decoration of the model's size that stands still on its first baked frame.</summary>
    public static string Declare(string className, string title, string sprite, int radius, int height) =>
        $"// {title}, a 3D model brought in with WadSmith; its MODELDEF draws the model, the sprites stand in where models are not drawn.\n" +
        $"class {className} : Actor\n{{\n\tDefault\n\t{{\n\t\tTag \"{title.Replace("\"", "'", StringComparison.Ordinal)}\";\n\t\tRadius {radius};\n\t\tHeight {height};\n\t\t+SOLID;\n\t}}\n\n" +
        $"\tStates\n\t{{\n\tSpawn:\n\t\t{sprite} A -1;\n\t\tStop;\n\t}}\n}}\n";

    /// <summary>Writes the project's MODELDEF: an include for every prop's entry; a MODELDEF of the user's own is left alone.</summary>
    public static void WriteDefinitions(Project project)
    {
        var path = Path.Combine(project.Folder, DefinitionFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != DefinitionMark) return;
        }

        var includes = project.Info.Copied.Where(c => c.Kind == "modeldef").Select(c => $"#include \"{c.File}\"").Distinct(StringComparer.Ordinal).ToList();
        if (includes.Count == 0) return;
        project.WriteFile(DefinitionFile, Encoding.UTF8.GetBytes(DefinitionMark + "\n\n" + string.Join("\n", includes) + "\n"));
    }

    /// <summary>"old_barrel-2" → "Old barrel 2".</summary>
    public static string Title(string fileName)
    {
        var words = new string([.. fileName.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Model";
        var text = string.Join(' ', words).ToLowerInvariant();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A class name from the file's name ("Model" + its letters) that neither the game nor the project has.</summary>
    private static string ClassName(Project project, GameResources into, string fileName)
    {
        var letters = string.Concat(Title(fileName).Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        var stem = "Model" + new string([.. letters.Where(char.IsAsciiLetterOrDigit)]);
        if (stem.Length > 40) stem = stem[..40];
        var taken = new HashSet<string>(project.Info.Copied.Select(c => c.Original), StringComparer.OrdinalIgnoreCase);
        var name = stem;
        for (var i = 2; taken.Contains(name) || into.Things.FindClass(name) is not null; i++) name = stem + i.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    /// <summary>A four-letter sprite name, "M" and three more, that neither the game's files nor the project's props use.</summary>
    private static string SpriteName(Project project, GameResources into)
    {
        var used = new HashSet<string>(project.Info.Copied.Where(c => c.Kind == "modelprop").Select(c => c.Name.Split('|')[0]), StringComparer.OrdinalIgnoreCase);
        const string symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (var i = 0; i < symbols.Length * symbols.Length * symbols.Length; i++)
        {
            var name = $"M{symbols[i / (36 * 36)]}{symbols[i / 36 % 36]}{symbols[i % 36]}";
            if (!used.Contains(name) && into.Sprites.Frames(name).Count == 0) return name;
        }

        throw new CorruptDataException("Every sprite name WadSmith gives models is taken in this project.");
    }

    /// <summary>A number this game and the project leave free, from the first carried-object number up.</summary>
    private static int FreeNumber(Project project, GameResources into)
    {
        var taken = new HashSet<int>(into.Things.All.Select(t => t.Number));
        foreach (var c in project.Info.Copied.Where(c => c.Kind == "actor")) taken.Add(int.Parse(c.Name, CultureInfo.InvariantCulture));
        var number = ActorBorrowing.FirstNumber;
        while (taken.Contains(number)) number++;
        return number;
    }
}
