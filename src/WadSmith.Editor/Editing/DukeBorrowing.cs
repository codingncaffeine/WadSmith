using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core;
using WadSmith.Core.Build;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Duke Nukem 3D's enemies in a GZDoom map (D48, D55): "looks like a Pig Cop, behaves like a Shotgun guy". The enemy's
/// frames become sprites (Build's five views onto Doom's eight rotations, mirrored on the far side); a ZScript class in the
/// project inherits the Doom monster, so it keeps that monster's mind, attacks, health and sounds, and gets that monster's
/// states rewritten onto Duke's frames, role by role (standing, walking, attacking, hurt, dying, dead); its scale makes it
/// as tall as the monster it behaves like. Duke's own behaviour lives in its CON scripts, which GZDoom cannot run.
/// </summary>
public static partial class DukeBorrowing
{
    /// <summary>The project's ZScript WadSmith writes: it includes the classes of the enemies carried in.</summary>
    public const string ScriptFile = "zscript.txt";

    private const string ScriptMark = "// Written by WadSmith: the classes of objects carried in or brought in as models. Edits here are replaced.";

    /// <summary>The mark the file carried before props from models shared it; a file with it is WadSmith's too.</summary>
    private const string OldScriptMark = "// Written by WadSmith: Duke Nukem 3D's enemies with Doom monsters' behaviour. Edits here are replaced.";

    /// <summary>The Duke enemy and its behaviour behind a placed object's number in <paramref name="project"/>, and the group file it came from; null for any other object.</summary>
    public static (string Enemy, string Behaviour, string Grp)? Of(Project project, int number)
    {
        var copied = project.Info.Copied;
        var actor = copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "duke3d" && c.Name == number.ToString(CultureInfo.InvariantCulture));
        if (actor is null || copied.FirstOrDefault(c => c.Kind == "dukeactor" && c.Original == actor.Original) is not { } duke) return null;
        var parts = duke.Name.Split('|');
        return parts.Length >= 6 ? (parts[4], parts[0], parts[5]) : null;
    }

    /// <summary>The class an enemy with a behaviour is declared as: "DukePigcopAsShotgunGuy".</summary>
    public static string ClassName(DukeEnemy enemy, string behaviour) => $"Duke{char.ToUpperInvariant(enemy.Key[0])}{enemy.Key[1..].ToLowerInvariant()}As{behaviour}";

    /// <summary>
    /// Carries <paramref name="enemy"/> of <paramref name="duke"/> into <paramref name="project"/> with the behaviour of the Doom
    /// monster <paramref name="behaviour"/> (a class of <paramref name="into"/>'s game), once per pair. Writes files: call it on a worker.
    /// </summary>
    public static CarriedActor Carry(Project project, GameResources into, BuildArtContainer duke, DukeEnemy enemy, string behaviour)
    {
        var className = ClassName(enemy, behaviour);
        if (project.FindCopy("duke3d", className, "actor") is { } done && int.TryParse(done.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var had) && into.Things.Find(had) is { } known)
        {
            return new CarriedActor(had, known, 0, 0, false);
        }

        var parent = into.Things.FindClass(behaviour) ?? throw new CorruptDataException($"{into.Game.Title} has no monster {behaviour} to lend its behaviour.");
        var sprite = SpriteName(enemy, into, project);

        // The frames as sprites: a letter for each tile, A first (a frame two roles share keeps one letter); five views as
        // rotations 1 to 5, the middle three mirrored onto 8, 7 and 6, as Doom pairs them.
        var letters = new Dictionary<DukeFrame, char>();
        var byTile = new Dictionary<(int Tile, int Views), char>();
        var sprites = 0;
        foreach (var frame in enemy.Frames)
        {
            if (byTile.TryGetValue((frame.Tile, frame.Views), out var shared))
            {
                letters[frame] = shared;
                continue;
            }

            if (byTile.Count >= 29) continue;
            var letter = (char)('A' + byTile.Count);
            byTile[(frame.Tile, frame.Views)] = letter;
            letters[frame] = letter;
            for (var view = 0; view < frame.Views; view++)
            {
                if (duke.Art.Tile(frame.Tile + view) is not { } tile) continue;
                var rotation = view + 1;
                var name = frame.Views == 1 ? $"{sprite}{letter}0" : rotation is 1 or 5 ? $"{sprite}{letter}{rotation}" : $"{sprite}{letter}{rotation}{letter}{10 - rotation}";
                project.WriteFile($"sprites/{name}.png", ImageCodec.EncodePng(duke.Art.Picture(tile)));
                project.Record(new CopiedAsset { Kind = "sprite", Game = "duke3d", Original = tile.Number.ToString(CultureInfo.InvariantCulture), Name = name, File = $"sprites/{name}.png" });
                sprites++;
            }
        }

        // As tall as the monster it behaves like.
        var standing = duke.Art.Tile(enemy.PictureTile);
        var scale = standing is null ? 1.0 : Math.Round(Math.Clamp(parent.Height * 1.1 / standing.Height, 0.25, 3.0), 2);
        var text = Declare(className, parent.ClassName, enemy.Name, scale, sprite, letters, enemy, into.Things.StateBlocks(parent.ClassName));
        var file = $"zscript/wadsmith/{className}.zs";
        project.WriteFile(file, Encoding.UTF8.GetBytes(text));
        project.Record(new CopiedAsset { Kind = "zscript", Game = "duke3d", Original = className, Name = className, File = file });
        WriteScript(project);

        var taken = new HashSet<int>(into.Things.All.Select(t => t.Number));
        foreach (var c in project.Info.Copied.Where(c => c.Kind == "actor")) taken.Add(int.Parse(c.Name, CultureInfo.InvariantCulture));
        var number = ActorBorrowing.FirstNumber;
        while (taken.Contains(number)) number++;
        var title = $"{enemy.Name} (Duke Nukem 3D, as a {parent.Name.ToLowerInvariant()})";
        project.Record(new CopiedAsset { Kind = "actor", Game = "duke3d", Original = className, Name = number.ToString(CultureInfo.InvariantCulture), File = file });
        project.Record(new CopiedAsset { Kind = "dukeactor", Game = "duke3d", Original = className, Name = string.Join('|', behaviour, sprite, scale.ToString(CultureInfo.InvariantCulture), title, enemy.Key, duke.Path ?? ""), File = file });

        var thing = parent with { Number = number, ClassName = className, Name = title, Sprite = sprite, Frame = 'A', ScaleX = scale, ScaleY = scale };
        into.Carry([thing], project.Folder);
        return new CarriedActor(number, thing, sprites, 0, true);
    }

    /// <summary>
    /// The ZScript class: the Doom monster's states, each line's sprite made the enemy's and its frames mapped onto the
    /// enemy's frames of the same role, in the order the monster first shows them; a dying sequence ends on the dead frame.
    /// </summary>
    public static string Declare(string className, string parent, string name, double scale, string sprite, IReadOnlyDictionary<DukeFrame, char> letters, DukeEnemy enemy, IReadOnlyList<(IReadOnlyList<string> Labels, IReadOnlyList<string> Lines)> blocks)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"// {name} from Duke Nukem 3D, with the behaviour of {parent}.\nclass {className} : {parent}\n{{\n\tDefault\n\t{{\n\t\tTag \"{name}\";\n\t\tScale {scale.ToString(CultureInfo.InvariantCulture)};\n\t}}\n\n\tStates\n\t{{\n");
        char[] Letters(DukeRole role) => [.. enemy.Of(role).Where(letters.ContainsKey).Select(f => letters[f])];
        foreach (var (labels, lines) in blocks)
        {
            var role = RoleOf(labels);
            var ours = Letters(role);
            var dead = role == DukeRole.Dying ? Letters(DukeRole.Dead) : [];
            if (ours.Length == 0) ours = Letters(DukeRole.Walk);
            var mapping = new Dictionary<char, char>();
            foreach (var label in labels) text.Append('\t').Append(label).Append(":\n");
            foreach (var line in lines)
            {
                var m = StateLine().Match(line);
                if (!m.Success || m.Groups[1].Value is "TNT1" or "####" or "----" || IsKeyword(m.Groups[1].Value))
                {
                    text.Append("\t\t").Append(line).Append(";\n");
                    continue;
                }

                var final = Regex.IsMatch(m.Groups[3].Value, @"^\s*-1\b");
                var frames = new StringBuilder();
                foreach (var c in m.Groups[2].Value)
                {
                    if (c is '"' or '#') continue;
                    if (final && dead.Length > 0)
                    {
                        frames.Append(dead[0]);
                        continue;
                    }

                    if (!mapping.TryGetValue(c, out var to)) mapping[c] = to = ours[mapping.Count % ours.Length];
                    frames.Append(to);
                }

                text.Append("\t\t").Append(sprite).Append(' ').Append(frames.Length == 0 ? "A" : frames.ToString()).Append(m.Groups[3].Value).Append(";\n");
            }
        }

        text.Append("\t}\n}\n");
        return text.ToString();
    }

    /// <summary>What a block of states is for, by its labels.</summary>
    private static DukeRole RoleOf(IReadOnlyList<string> labels)
    {
        var label = labels[0].ToUpperInvariant();
        if (label == "SPAWN") return DukeRole.Stand;
        if (label is "MISSILE" or "MELEE") return DukeRole.Attack;
        if (label == "PAIN" || label.StartsWith("PAIN.", StringComparison.Ordinal)) return DukeRole.Pain;
        if (label.StartsWith("DEATH", StringComparison.Ordinal) || label is "XDEATH" or "RAISE" || label.StartsWith("XDEATH", StringComparison.Ordinal)) return DukeRole.Dying;
        return DukeRole.Walk;
    }

    private static bool IsKeyword(string word) => word.ToUpperInvariant() is "GOTO" or "STOP" or "LOOP" or "WAIT" or "FAIL";

    /// <summary>A four-letter sprite name for the enemy that neither this game nor the project's other carried objects use.</summary>
    private static string SpriteName(DukeEnemy enemy, GameResources into, Project project)
    {
        var used = new HashSet<string>(project.Info.Copied.Where(c => c.Kind == "dukeactor").Select(c => c.Name.Split('|')[1]), StringComparer.Ordinal);
        var key = new string([.. enemy.Key.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
        IEnumerable<string> Candidates()
        {
            yield return "D" + key[..Math.Min(3, key.Length)].PadRight(3, 'X');
            for (var i = 0; i < 36 * 36; i++) yield return "D" + key[0] + "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"[i / 36] + "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"[i % 36];
        }

        return Candidates().First(c => !used.Contains(c) && into.Sprites.Frames(c).Count == 0);
    }

    /// <summary>Writes the project's ZScript: the version, then an include for every class WadSmith wrote (Duke's enemies, props from models); a ZScript of the user's own is left alone.</summary>
    public static void WriteScript(Project project)
    {
        var path = Path.Combine(project.Folder, ScriptFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() is not (ScriptMark or OldScriptMark)) return;
        }

        var includes = project.Info.Copied.Where(c => c.Kind == "zscript").Select(c => $"#include \"{c.File}\"").Distinct(StringComparer.Ordinal).ToList();
        if (includes.Count == 0) return;
        project.WriteFile(ScriptFile, Encoding.UTF8.GetBytes(ScriptMark + "\nversion \"4.10\"\n\n" + string.Join("\n", includes) + "\n"));
    }

    [GeneratedRegex(@"^""?([A-Za-z0-9_#-]{4})""?\s+(""?[A-Za-z\[\]\\^#""]+)(.*)$")]
    private static partial Regex StateLine();
}
