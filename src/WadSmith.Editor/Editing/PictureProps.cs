using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>A decoration made from a picture: its object, how many frames it moves through, and what was said.</summary>
public sealed record ImportedDecoration(CarriedActor Actor, int Frames, IReadOnlyList<string> Notes);

/// <summary>
/// What a picture decoration's record holds: its sprite, size in the map, name, the file it came from, the picture's
/// scale, whether it blocks, each frame's tics, and the picture's own size in pixels (for a new height).
/// </summary>
public sealed record DecorationShape(string Sprite, int Radius, int Height, string Title, string File, double Scale, bool Solid, IReadOnlyList<int> Tics, int PixelWidth, int PixelHeight)
{
    public string Record() => string.Join('|', Sprite, I(Radius), I(Height), Title.Replace('|', ' '), File.Replace('|', ' '), Scale.ToString("R", CultureInfo.InvariantCulture), Solid ? "1" : "0", string.Join(',', Tics.Select(I)), I(PixelWidth), I(PixelHeight));

    public static DecorationShape? Parse(string text)
    {
        var p = text.Split('|');
        if (p.Length < 10) return null;
        var c = CultureInfo.InvariantCulture;
        return new DecorationShape(p[0], int.Parse(p[1], c), int.Parse(p[2], c), p[3], p[4], double.Parse(p[5], c), p[6] == "1", [.. p[7].Split(',').Select(t => int.Parse(t, c))], int.Parse(p[8], c), int.Parse(p[9], c));
    }

    /// <summary>The shape standing <paramref name="height"/> map units tall (the picture scaled to it, its width with it).</summary>
    public DecorationShape Standing(int height)
    {
        var scale = Math.Round(Math.Max(1, height) / (double)PixelHeight, 4);
        return this with { Scale = scale, Height = Math.Max(1, height), Radius = Math.Max(4, (int)Math.Ceiling(PixelWidth * scale / 2)) };
    }

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Decorations made from pictures of the user's own (Phase 7, §3.6): a PNG, JPEG or other still picture, or an animated
/// GIF, WebP or APNG, becomes an object that shows it from every side, standing on the floor by its bottom middle. A moving
/// picture plays at its own pace (each frame a sprite frame, A to Z, with its own tics). A picture taller than
/// <see cref="StandsAtMost"/> pixels is scaled to stand that tall; the Inspector sets another height and whether it blocks.
/// </summary>
public static class PictureProps
{
    /// <summary>Sprite frames run A to Z: a longer animation keeps every n-th frame, running as long.</summary>
    public const int MostFrames = 26;

    /// <summary>How tall a new decoration stands at most, in map units (the player is 56).</summary>
    public const int StandsAtMost = 64;

    /// <summary>
    /// Brings the picture at <paramref name="path"/> into <paramref name="project"/> as a decoration: its sprites, its class
    /// and its number. Reads and writes files: call it on a worker.
    /// </summary>
    public static ImportedDecoration Import(Project project, GameResources into, string path)
    {
        var file = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);
        var notes = new List<string>();
        IReadOnlyList<AnimationFrame> frames = ImageCodec.IsKnownImage(bytes) ? AnimatedPicture.Read(bytes, file) : [new AnimationFrame(Tga.DecodeAny(bytes, file), 0)];
        if (frames.Count > MostFrames)
        {
            notes.Add($"It has {frames.Count} frames; every {(int)Math.Ceiling(frames.Count / (double)MostFrames)}th is kept ({MostFrames} at most for an object), running as long.");
            frames = AnimatedPicture.Thin(frames, MostFrames);
        }

        if (Math.Max(frames[0].Image.Width, frames[0].Image.Height) > ModelProps.MaxSkin)
        {
            notes.Add($"Its {frames[0].Image.Width}×{frames[0].Image.Height} pictures were made {ModelProps.MaxSkin} pixels across at most, to keep the project small.");
            frames = [.. frames.Select(f => f with { Image = ModelProps.Shrink(f.Image, ModelProps.MaxSkin) })];
        }

        var (width, height) = (frames[0].Image.Width, frames[0].Image.Height);
        var title = ModelProps.Title(Path.GetFileNameWithoutExtension(path));
        var className = ClassName(project, into, title);
        var sprite = SpriteName(project, into);
        var tics = frames.Count == 1 ? [-1] : frames.Select(f => AnimatedPicture.Tics(f.Milliseconds)).ToList();
        var shape = new DecorationShape(sprite, 0, 0, title, file, 1, true, tics, width, height).Standing(Math.Min(height, StandsAtMost));

        // Each frame a sprite seen from every side (rotation 0), standing on its bottom middle.
        var files = new List<(string, byte[])>();
        for (var i = 0; i < frames.Count; i++)
        {
            var image = frames[i].Image;
            image.OffsetX = image.Width / 2;
            image.OffsetY = image.Height;
            files.Add(($"sprites/{sprite}{(char)('A' + i)}0.png", ImageCodec.EncodePng(image)));
        }

        var script = $"zscript/wadsmith/{className}.zs";
        files.Add((script, Encoding.UTF8.GetBytes(Declare(className, shape))));
        project.WriteFiles(files);

        var number = FreeNumber(project, into);
        var c = CultureInfo.InvariantCulture;
        project.Record(
        [
            new CopiedAsset { Kind = "zscript", Game = "picture", Original = className, Name = className, File = script },
            new CopiedAsset { Kind = "actor", Game = "picture", Original = className, Name = number.ToString(c), File = script },
            new CopiedAsset { Kind = "pictureprop", Game = "picture", Original = className, Name = shape.Record(), File = script },
        ]);
        DukeBorrowing.WriteScript(project);

        var thing = ThingOf(number, className, shape);
        into.Carry([thing], project.Folder);
        if (shape.Height < height) notes.Add($"It stands {shape.Height} units tall in the map (the player is 56); its Inspector sets another height.");
        return new ImportedDecoration(new CarriedActor(number, thing, frames.Count, 0, true), frames.Count, notes);
    }

    /// <summary>Gives the decoration <paramref name="className"/> a new height and blocking, for every one placed: its class written again. Call it on a worker.</summary>
    public static ThingType Change(Project project, GameResources into, string className, int height, bool solid)
    {
        var record = project.CopiedNow().FirstOrDefault(c => c.Kind == "pictureprop" && c.Original == className) ?? throw new CorruptDataException($"{className} is not a picture decoration of this project.");
        var shape = (DecorationShape.Parse(record.Name) ?? throw new CorruptDataException($"The record of {className} cannot be read.")).Standing(height) with { Solid = solid };
        project.WriteFile(record.File, Encoding.UTF8.GetBytes(Declare(className, shape)));
        project.Replace(record, record with { Name = shape.Record() });
        var number = int.Parse(project.CopiedNow().First(c => c.Kind == "actor" && c.Original == className).Name, CultureInfo.InvariantCulture);
        var thing = ThingOf(number, className, shape);
        into.Carry([thing], project.Folder);
        return thing;
    }

    /// <summary>The decoration an object of <paramref name="type"/> is in this project: its class and shape; null for any other object.</summary>
    public static (string ClassName, DecorationShape Shape)? Of(Project project, int type)
    {
        var copied = project.CopiedNow();
        var number = type.ToString(CultureInfo.InvariantCulture);
        if (copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "picture" && c.Name == number) is not { } actor) return null;
        return copied.FirstOrDefault(c => c.Kind == "pictureprop" && c.Original == actor.Original) is { } record && DecorationShape.Parse(record.Name) is { } shape ? (actor.Original, shape) : null;
    }

    /// <summary>The decorations a project's record lists, as catalogue entries (for <see cref="GameResources.Load"/>).</summary>
    public static IEnumerable<ThingType> Recorded(ProjectInfo info)
    {
        foreach (var prop in info.Copied.Where(c => c.Kind == "pictureprop"))
        {
            var number = info.Copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "picture" && c.Original == prop.Original)?.Name;
            if (number is null || DecorationShape.Parse(prop.Name) is not { } shape) continue;
            yield return ThingOf(int.Parse(number, CultureInfo.InvariantCulture), prop.Original, shape);
        }
    }

    private static ThingType ThingOf(int number, string className, DecorationShape s) =>
        new(number, className, s.Title, s.Sprite, 'A', s.Radius, s.Height, s.Scale, s.Scale, false, ThingCategory.Decoration);

    /// <summary>The class: the picture's frames in turn (one frame stands still), at its scale and size, blocking or not.</summary>
    public static string Declare(string className, DecorationShape s)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"// {s.Title}, a picture brought in with WadSmith as a decoration. Edits here are replaced.\n");
        text.Append(c, $"class {className} : Actor\n{{\n\tDefault\n\t{{\n\t\tTag \"{s.Title.Replace("\"", "'", StringComparison.Ordinal)}\";\n");
        text.Append(c, $"\t\tRadius {s.Radius};\n\t\tHeight {s.Height};\n\t\tScale {s.Scale.ToString("R", c)};\n");
        if (s.Solid) text.Append("\t\t+SOLID;\n");
        text.Append("\t}\n\n\tStates\n\t{\n\tSpawn:\n");
        if (s.Tics is [-1]) text.Append(c, $"\t\t{s.Sprite} A -1;\n\t\tStop;\n");
        else
        {
            for (var i = 0; i < s.Tics.Count; i++) text.Append(c, $"\t\t{s.Sprite} {(char)('A' + i)} {s.Tics[i]};\n");
            text.Append("\t\tLoop;\n");
        }

        text.Append("\t}\n}\n");
        return text.ToString();
    }

    private static string ClassName(Project project, GameResources into, string title)
    {
        var letters = string.Concat(title.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        var stem = "Picture" + new string([.. letters.Where(char.IsAsciiLetterOrDigit)]);
        if (stem.Length > 40) stem = stem[..40];
        var taken = new HashSet<string>(project.CopiedNow().Select(c => c.Original), StringComparer.OrdinalIgnoreCase);
        var name = stem;
        for (var i = 2; taken.Contains(name) || into.Things.FindClass(name) is not null; i++) name = stem + i.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    /// <summary>A four-letter sprite name, "P" and three more, that neither the game's files nor the project's decorations use.</summary>
    private static string SpriteName(Project project, GameResources into)
    {
        var used = new HashSet<string>(project.CopiedNow().Where(c => c.Kind == "pictureprop").Select(c => c.Name.Split('|')[0]), StringComparer.OrdinalIgnoreCase);
        const string symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (var i = 0; i < symbols.Length * symbols.Length * symbols.Length; i++)
        {
            var name = $"P{symbols[i / (36 * 36)]}{symbols[i / 36 % 36]}{symbols[i % 36]}";
            if (!used.Contains(name) && into.Sprites.Frames(name).Count == 0) return name;
        }

        throw new CorruptDataException("Every sprite name WadSmith gives picture decorations is taken in this project.");
    }

    private static int FreeNumber(Project project, GameResources into)
    {
        var taken = new HashSet<int>(into.Things.All.Select(t => t.Number));
        foreach (var c in project.CopiedNow().Where(c => c.Kind == "actor")) taken.Add(int.Parse(c.Name, CultureInfo.InvariantCulture));
        var number = ActorBorrowing.FirstNumber;
        while (taken.Contains(number)) number++;
        return number;
    }
}
