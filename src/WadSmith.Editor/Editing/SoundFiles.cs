using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Audio;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>How a sound placed in the map plays: over and over, now and then at random, every few seconds, or once.</summary>
public enum SoundPlays
{
    /// <summary>Over and over without a gap (rain, machinery, a hum).</summary>
    Loop,

    /// <summary>Now and then, after a random wait between two lengths (drips, distant growls).</summary>
    Random,

    /// <summary>Every so many seconds.</summary>
    Every,

    /// <summary>Once, the first time a player stands in the room it is in (a creak, a voice).</summary>
    Once,
}

/// <summary>
/// A placed sound's settings, kept in the object's own arguments so undo, copy and paste carry them: the sound's number
/// (arg 0), its volume in percent (arg 1), how it plays plus 4 when it is heard everywhere (arg 2), and the waits in
/// seconds (arg 3, and arg 4 for the longest random wait).
/// </summary>
public sealed record SoundSettings(int Sound, SoundPlays Plays, int Volume, bool Everywhere, int Wait, int LongestWait)
{
    public static SoundSettings Of(Thing t) => new(t.Args[0], (SoundPlays)(t.Args[2] & 3), t.Args[1] <= 0 ? 100 : t.Args[1], (t.Args[2] & 4) != 0, t.Args[3], t.Args[4]);

    /// <summary>The arguments that hold these settings.</summary>
    public int[] Args => [Sound, Math.Clamp(Volume, 1, 100), (int)Plays | (Everywhere ? 4 : 0), Math.Max(0, Wait), Math.Max(0, LongestWait)];

    /// <summary>A new sound's: over and over, full volume, heard nearby, a random wait of 2 to 8 seconds when it is changed to one.</summary>
    public static SoundSettings New(int sound) => new(sound, SoundPlays.Loop, 100, false, 2, 8);
}

/// <summary>A sound brought in for placing: its number in the project, the name GZDoom knows it by and its file.</summary>
public sealed record ImportedSound(int Sound, string Title, string Lump, int Number);

/// <summary>
/// Sounds and music of the user's own (Phase 7): a music file becomes the map's music; a sound file becomes a sound
/// placed in the map, played by one class WadSmith writes (<see cref="ClassName"/>) as the object's settings say. The
/// sound's lump goes in <c>sounds/</c> with a line in the project's SNDINFO; the class picks the sound by its number.
/// </summary>
public static class SoundFiles
{
    /// <summary>The class every placed sound is.</summary>
    public const string ClassName = "WadSmithSound";

    /// <summary>The file declaring it, under the project's ZScript includes.</summary>
    public const string ScriptFile = "zscript/wadsmith/WadSmithSound.zs";

    private const string Logical = "wadsmith/";

    /// <summary>Whether <paramref name="path"/> names a sound or music file WadSmith brings in.</summary>
    public static bool IsAudio(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ogg" or ".mp3" or ".flac" or ".wav" or ".mid" or ".midi" or ".mus" or ".mod" or ".xm" or ".it" or ".s3m";

    /// <summary>Whether the file's content is music only (a score or a tracker module), which cannot be a sound in the map.</summary>
    public static bool IsMusicOnly(ReadOnlySpan<byte> data) => AudioFormats.Detect(data) is AudioKind.Mus or AudioKind.Midi or AudioKind.Module;

    /// <summary>
    /// Brings the file at <paramref name="path"/> in as music for the project's maps: copied into the project's music folder
    /// under a name the game does not use, and offered among the game's tracks. Writes files: call it on a worker.
    /// </summary>
    public static string ImportMusic(Project project, GameResources into, string path)
    {
        var data = Read(path);
        var name = FreeLump(into, project, path, ResourceNamespace.Music);
        var file = $"music/{name}{AudioFormats.Extension(AudioFormats.Detect(data), data)}";
        project.WriteFiles([(file, data)]);
        project.Record([new CopiedAsset { Kind = "music", Game = "file", Original = Path.GetFileName(path), Name = name, File = file }]);
        into.AddMusic(name);
        return name;
    }

    /// <summary>
    /// Brings the sound at <paramref name="path"/> into the project to be placed: its lump, its SNDINFO line, its number in
    /// <see cref="ClassName"/>'s list and the class itself (written again with every sound). Writes files: call it on a worker.
    /// </summary>
    public static ImportedSound ImportSound(Project project, GameResources into, string path)
    {
        var data = Read(path);
        if (IsMusicOnly(data)) throw new CorruptDataException($"{Path.GetFileName(path)} is music: it can be the map's music, not a sound in the map.");
        var kind = AudioFormats.Detect(data);
        if (kind is AudioKind.None) throw new CorruptDataException($"{Path.GetFileName(path)} is not a sound WadSmith knows (Ogg, MP3, FLAC or WAV).");

        var lump = FreeLump(into, project, path, ResourceNamespace.Sounds);
        var file = $"sounds/{lump}{AudioFormats.Extension(kind, data)}";
        var sound = Sounds(project.CopiedNow()).Count;
        var title = Path.GetFileNameWithoutExtension(path);
        project.WriteFiles([(file, data)]);
        project.Record([
            new CopiedAsset { Kind = "sound", Game = "file", Original = Path.GetFileName(path), Name = lump, File = file },
            new CopiedAsset { Kind = "sndinfo", Game = "file", Original = $"{Logical}{lump.ToLowerInvariant()} {lump}", Name = $"{Logical}{lump.ToLowerInvariant()}", File = ActorBorrowing.SoundFile },
            new CopiedAsset { Kind = "placedsound", Game = "file", Original = title, Name = $"{sound}|{Logical}{lump.ToLowerInvariant()}", File = ScriptFile },
        ]);
        ActorBorrowing.WriteSoundTable(project);

        var number = project.CopiedNow().FirstOrDefault(c => c.Kind == "actor" && c.Original == ClassName)?.Name is { } known ? int.Parse(known, CultureInfo.InvariantCulture) : FreeNumber(project, into);
        if (project.FindCopy("file", ClassName, "actor") is null)
        {
            project.Record([
                new CopiedAsset { Kind = "actor", Game = "file", Original = ClassName, Name = number.ToString(CultureInfo.InvariantCulture), File = "" },
                new CopiedAsset { Kind = "zscript", Game = "file", Original = ClassName, Name = ClassName, File = ScriptFile },
            ]);
        }

        project.WriteFile(ScriptFile, Encoding.UTF8.GetBytes(Declare(Sounds(project.CopiedNow()))));
        DukeBorrowing.WriteScript(project);
        into.Carry([Thing(number)], project.Folder);
        return new ImportedSound(sound, title, lump, number);
    }

    /// <summary>The project's placed sounds by number: the title it came with and the name SNDINFO gives it.</summary>
    public static IReadOnlyList<(int Sound, string Title, string Name)> Sounds(IReadOnlyList<CopiedAsset> copied) =>
        [.. copied.Where(c => c.Kind == "placedsound").Select(c => (Parts: c.Name.Split('|'), c.Original))
            .Where(p => p.Parts.Length == 2 && int.TryParse(p.Parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(p => (int.Parse(p.Parts[0], CultureInfo.InvariantCulture), p.Original, p.Parts[1]))];

    /// <summary>The class's object type for the editor's catalogue, when the project has placed sounds.</summary>
    public static IEnumerable<ThingType> Recorded(ProjectInfo info)
    {
        if (info.Copied.FirstOrDefault(c => c.Kind == "actor" && c.Original == ClassName) is { } actor && int.TryParse(actor.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) yield return Thing(number);
    }

    /// <summary>Whether <paramref name="t"/> is a sound placed with WadSmith in <paramref name="project"/>.</summary>
    public static bool IsPlacedSound(Project project, Thing t) => project.FindCopy("file", ClassName, "actor") is { } actor && actor.Name == t.Type.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a placed sound does, in a sentence.</summary>
    public static string Describe(SoundSettings s, string title)
    {
        var how = s.Plays switch
        {
            SoundPlays.Loop => "over and over",
            SoundPlays.Random => string.Create(CultureInfo.InvariantCulture, $"now and then, every {s.Wait} to {Math.Max(s.Wait, s.LongestWait)} seconds"),
            SoundPlays.Every => string.Create(CultureInfo.InvariantCulture, $"every {Math.Max(1, s.Wait)} seconds"),
            _ => "once, when a player first comes into its room",
        };
        return string.Create(CultureInfo.InvariantCulture, $"Plays {title} {how}, at {s.Volume}% volume, heard {(s.Everywhere ? "everywhere in the map" : "nearby")}.");
    }

    private static ThingType Thing(int number) => new(number, ClassName, "Sound", null, 'A', 16, 16, 1, 1, false);

    /// <summary>
    /// The class: it takes its sound by number and plays it as its arguments say. A looping sound starts with the map; a
    /// random or timed one counts tics; a once-only one waits for a player in its room. Its sounds keep playing where they
    /// are when the game is saved and loaded, as GZDoom keeps every actor's sounds.
    /// </summary>
    public static string Declare(IReadOnlyList<(int Sound, string Title, string Name)> sounds)
    {
        var text = new StringBuilder();
        text.Append("// Written by WadSmith: the sounds placed in the map. Edits here are replaced.\n");
        text.Append("class ").Append(ClassName).Append(" : Actor\n{\n");
        text.Append("\tint wait;\n\tbool done;\n\n");
        text.Append("\tDefault\n\t{\n\t\tTag \"Sound\";\n\t\t+NOBLOCKMAP;\n\t\t+NOSECTOR;\n\t\t+NOGRAVITY;\n\t\t+DONTSPLASH;\n\t\t+NOTONAUTOMAP;\n\t}\n\n");
        text.Append("\tstatic string SoundOf(int n)\n\t{\n\t\tswitch (n)\n\t\t{\n");
        foreach (var (sound, title, name) in sounds) text.Append(CultureInfo.InvariantCulture, $"\t\t\tcase {sound}: return \"{name}\"; // {title.Replace("\n", " ", StringComparison.Ordinal)}\n");
        text.Append("\t\t}\n\t\treturn \"\";\n\t}\n\n");
        text.Append("\tint Plays() { return args[2] & 3; }\n\n");
        text.Append("\tvoid Emit(int channel, int flags)\n\t{\n\t\tstring name = SoundOf(args[0]);\n\t\tif (name == \"\") return;\n");
        text.Append("\t\tA_StartSound(name, channel, flags, args[1] > 0 ? args[1] / 100.0 : 1.0, (args[2] & 4) != 0 ? ATTN_NONE : ATTN_NORM);\n\t}\n\n");
        text.Append("\tint NextWait()\n\t{\n\t\tint shortest = max(1, args[3]);\n\t\treturn (Plays() == 1 ? random(shortest, max(shortest, args[4])) : shortest) * TICRATE;\n\t}\n\n");
        text.Append("\toverride void PostBeginPlay()\n\t{\n\t\tSuper.PostBeginPlay();\n\t\tif (Plays() == 0) Emit(CHAN_BODY, CHANF_LOOPING);\n\t\telse if (Plays() != 3) wait = NextWait();\n\t}\n\n");
        text.Append("\toverride void Tick()\n\t{\n\t\tif (isFrozen() || done) return;\n\t\tint plays = Plays();\n");
        text.Append("\t\tif (plays == 1 || plays == 2)\n\t\t{\n\t\t\tif (--wait <= 0)\n\t\t\t{\n\t\t\t\tEmit(CHAN_AUTO, CHANF_DEFAULT);\n\t\t\t\twait = NextWait();\n\t\t\t}\n\t\t}\n");
        text.Append("\t\telse if (plays == 3)\n\t\t{\n\t\t\tSector here = Level.PointInSector(pos.xy);\n\t\t\tfor (int i = 0; i < MAXPLAYERS; i++)\n\t\t\t{\n");
        text.Append("\t\t\t\tif (playeringame[i] && players[i].mo && players[i].mo.CurSector == here)\n\t\t\t\t{\n\t\t\t\t\tEmit(CHAN_AUTO, CHANF_DEFAULT);\n\t\t\t\t\tdone = true;\n\t\t\t\t\treturn;\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n\n");
        text.Append("\tStates\n\t{\n\tSpawn:\n\t\tTNT1 A -1;\n\t\tStop;\n\t}\n}\n");
        return text.ToString();
    }

    private static byte[] Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length > 64 * 1024 * 1024) throw new CorruptDataException($"{Path.GetFileName(path)} is over 64 MB; GZDoom loads every sound whole.");
        return data;
    }

    /// <summary>A lump name of up to eight letters from the file's name that neither the game nor the project uses.</summary>
    private static string FreeLump(GameResources into, Project project, string path, ResourceNamespace ns)
    {
        var letters = new string([.. Path.GetFileNameWithoutExtension(path).ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);
        if (letters.Length == 0) letters = ns == ResourceNamespace.Music ? "MUSIC" : "SOUND";
        bool Taken(string n) => into.Find(n, ns) is not null || into.Find(n, ResourceNamespace.Global) is not null || project.CopiedNow().Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase) && c.Kind is "sound" or "music");
        var name = letters[..Math.Min(8, letters.Length)];
        for (var n = 1; Taken(name); n++)
        {
            var suffix = n.ToString(CultureInfo.InvariantCulture);
            name = letters[..Math.Min(letters.Length, 8 - suffix.Length)] + suffix;
        }

        return name;
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
