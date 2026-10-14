using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>A picture brought in: the texture name walls, doors and floors use, its size, how many frames it moves through, and what was said.</summary>
public sealed record ImportedPicture(string Texture, int Width, int Height, int Frames, IReadOnlyList<string> Notes);

/// <summary>
/// Pictures and animations of the user's own as textures (D15, Phase 7, at the user's request in Phase 11): a still picture
/// becomes <c>textures/NAME.png</c>; an animated GIF, WebP or APNG becomes a texture per frame (<c>NAME000</c>, <c>NAME001</c>…)
/// and an ANIMDEFS entry that steps through them at the picture's own pace (its delays in game tics, 35 a second). The
/// first frame is the texture to put on a wall, a door or a floor; GZDoom plays the rest. At most 300 frames (every n-th
/// kept, running as long) and 1024 pixels across.
/// </summary>
public static class Pictures
{
    /// <summary>The project's ANIMDEFS WadSmith writes, marked so an ANIMDEFS of the user's own is left alone.</summary>
    public const string AnimationFile = "animdefs.txt";

    private const string Mark = "// Written by WadSmith: pictures brought in that move. Edits here are replaced.";

    public const int MaxFrames = 300;

    /// <summary>Whether <paramref name="path"/> is a picture WadSmith brings in as a texture.</summary>
    public static bool IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tga" or ".apng";

    /// <summary>Brings the picture at <paramref name="path"/> into <paramref name="project"/> and <paramref name="into"/>'s textures. Reads and writes files: call it on a worker.</summary>
    public static ImportedPicture Import(Project project, GameResources into, string path) => Import(project, into, Path.GetFileName(path), File.ReadAllBytes(path));

    /// <summary>Brings a picture held in memory (pasted, named <paramref name="file"/>) in, as <see cref="Import(Project, GameResources, string)"/> does a file.</summary>
    public static ImportedPicture Import(Project project, GameResources into, string file, byte[] bytes) =>
        ImportFrames(project, into, file, ImageCodec.IsKnownImage(bytes) ? AnimatedPicture.Read(bytes, file) : [new AnimationFrame(Tga.DecodeAny(bytes, file), 0)]);

    /// <summary>Brings frames in (a video's, read by <see cref="VideoFrames"/>; a picture's, decoded), named after <paramref name="file"/>, as one texture that moves through them.</summary>
    public static ImportedPicture ImportFrames(Project project, GameResources into, string file, IReadOnlyList<AnimationFrame> frames)
    {
        var path = file;
        var notes = new List<string>();
        if (frames.Count > MaxFrames)
        {
            notes.Add($"It has {frames.Count} frames; every {(int)Math.Ceiling(frames.Count / (double)MaxFrames)}th is kept ({MaxFrames} at most), running as long.");
            frames = AnimatedPicture.Thin(frames, MaxFrames);
        }

        var (width, height) = (frames[0].Image.Width, frames[0].Image.Height);
        if (Math.Max(width, height) > ModelProps.MaxSkin)
        {
            notes.Add($"Its {width}×{height} pictures were made {ModelProps.MaxSkin} pixels across at most, to keep the project small.");
            frames = [.. frames.Select(f => f with { Image = ModelProps.Shrink(f.Image, ModelProps.MaxSkin) })];
            (width, height) = (frames[0].Image.Width, frames[0].Image.Height);
        }

        var letters = new string([.. Path.GetFileNameWithoutExtension(path).ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);
        if (letters.Length == 0) letters = "PICTURE";
        string name;
        List<string> names;
        if (frames.Count == 1)
        {
            name = Borrowing.FreeName(into.Textures, letters[..Math.Min(8, letters.Length)], into.Game);
            names = [name];
        }
        else
        {
            // Frame names of eight letters: five of the file's and three digits, a stem no texture of the game or project uses.
            var stem = letters[..Math.Min(5, letters.Length)].PadRight(5, 'X');
            for (var n = 1; Enumerable.Range(0, frames.Count).Any(i => into.Textures.Has(Frame(stem, i))); n++) stem = stem[..3] + n.ToString("00", CultureInfo.InvariantCulture);
            names = [.. Enumerable.Range(0, frames.Count).Select(i => Frame(stem, i))];
            name = names[0];
        }

        project.WriteFiles([.. frames.Select((f, i) => ($"textures/{names[i]}.png", ImageCodec.EncodePng(f.Image)))]);
        var container = ResourceContainer.Open(project.Folder);
        foreach (var n in names) into.Textures.Add(container.FindPath($"textures/{n}.png") ?? throw new CorruptDataException($"textures/{n}.png was written but could not be read back."));
        var records = names.Select(n => new CopiedAsset { Kind = "wall", Game = "picture", Original = file, Name = n, File = $"textures/{n}.png" }).ToList();
        if (frames.Count > 1)
        {
            var tics = string.Join(',', frames.Select(f => AnimatedPicture.Tics(f.Milliseconds).ToString(CultureInfo.InvariantCulture)));
            records.Add(new CopiedAsset { Kind = "animation", Game = "picture", Original = file, Name = $"{names[0][..5]}|{frames.Count}|{tics}", File = AnimationFile });
        }

        project.Record(records);
        if (frames.Count > 1) WriteAnimations(project);
        return new ImportedPicture(name, width, height, frames.Count, notes);

        static string Frame(string stem, int i) => stem + i.ToString("000", CultureInfo.InvariantCulture);
    }

    /// <summary>The frame an animation shows <paramref name="seconds"/> after it began: GZDoom counts 35 tics a second and loops.</summary>
    public static string FrameAt((string[] Frames, int[] Tics) animation, double seconds) =>
        animation.Frames.Length == 0 ? "" : animation.Frames[Math.Min(FrameIndexAt(animation.Tics, seconds), animation.Frames.Length - 1)];

    /// <summary>Which frame (by its place) an animation of these tics shows <paramref name="seconds"/> after it began.</summary>
    public static int FrameIndexAt(IReadOnlyList<int> tics, double seconds)
    {
        var total = tics.Sum();
        if (tics.Count == 0 || total <= 0) return 0;
        var tic = (long)Math.Floor(seconds * 35) % total;
        for (var i = 0; i < tics.Count; i++)
        {
            tic -= tics[i];
            if (tic < 0) return i;
        }

        return tics.Count - 1;
    }

    /// <summary>The project's animations by their first frame's name: every frame and its tics (what its ANIMDEFS says), for the 3D view to play.</summary>
    public static Dictionary<string, (string[] Frames, int[] Tics)> Animations(IReadOnlyList<CopiedAsset> copied)
    {
        var animations = new Dictionary<string, (string[] Frames, int[] Tics)>(StringComparer.OrdinalIgnoreCase);
        foreach (var animation in copied.Where(c => c.Kind == "animation"))
        {
            var parts = animation.Name.Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 1) continue;
            var tics = parts[2].Split(',').Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(1, n) : 1).ToArray();
            var frames = Enumerable.Range(0, count).Select(i => parts[0] + i.ToString("000", CultureInfo.InvariantCulture)).ToArray();
            animations[frames[0]] = (frames, [.. Enumerable.Range(0, count).Select(i => i < tics.Length ? tics[i] : 1)]);
        }

        return animations;
    }

    /// <summary>Writes the project's ANIMDEFS from its animations; an ANIMDEFS of the user's own is left alone.</summary>
    public static void WriteAnimations(Project project)
    {
        var path = Path.Combine(project.Folder, AnimationFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != Mark) return;
        }

        var text = new StringBuilder(Mark).Append("\n\n");
        foreach (var animation in project.CopiedNow().Where(c => c.Kind == "animation"))
        {
            var parts = animation.Name.Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) continue;
            var tics = parts[2].Split(',');
            text.Append("texture ").Append(parts[0]).Append("000\n");
            for (var i = 0; i < count; i++) text.Append(CultureInfo.InvariantCulture, $"\tpic {parts[0]}{i:000} tics {(i < tics.Length ? tics[i] : "1")}\n");
            text.Append('\n');
        }

        // Camera screens (Phase 7): a texture the game draws from a camera, replacing the stand-in picture of the same name.
        foreach (var screen in WadSmith.Editor.Smart.Cameras.Screens(project.CopiedNow())) text.Append(CultureInfo.InvariantCulture, $"cameratexture {screen.Texture} {screen.Width} {screen.Height}\n");

        project.WriteFile(AnimationFile, Encoding.UTF8.GetBytes(text.ToString()));
    }
}
