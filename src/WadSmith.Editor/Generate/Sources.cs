using WadSmith.Core;
using WadSmith.Core.Projects;
using WadSmith.Editor.Editing;

namespace WadSmith.Editor.Generate;

/// <summary>
/// Brings what a made map uses from the user's other games into its project (D43, D55): each monster from another game (or
/// one the base game lacks, such as Doom II's in Doom) carried in with its pictures and sounds, each look's pictures copied
/// (a sky stays the base game's own sky). Only what the plan places is carried. Writes files: call it on a worker.
/// </summary>
public static class Sources
{
    /// <summary>The sky's ceiling picture in each family.</summary>
    public static string SkyOf(string family) => family switch
    {
        "hexen" => "F_SKY",
        "strife" => "F_SKY001",
        _ => "F_SKY1",
    };

    /// <summary>
    /// The sky picture the game itself shows in <paramref name="slot"/>, as the original engines pick it: Doom II by map number
    /// (SKY1 to MAP11, SKY2 to MAP20, SKY3 after), Doom and Heretic by episode, Hexen's first sky, Strife's mountains. A made
    /// map with outdoors names it in its settings, so its sky never rests on the game's own definition of the slot.
    /// </summary>
    public static string SkyPicture(string family, string slot)
    {
        var upper = slot.ToUpperInvariant();
        if (upper.Length == 4 && upper[0] == 'E' && upper[2] == 'M' && char.IsDigit(upper[1]))
        {
            var episode = upper[1] - '0';
            return family == "heretic" ? episode switch { 2 => "SKY2", 3 or 5 => "SKY3", _ => "SKY1" } : $"SKY{Math.Clamp(episode, 1, 4)}";
        }

        return family switch
        {
            "hexen" => "SKY1",
            "strife" => "SKYMNT02",
            _ => upper.StartsWith("MAP", StringComparison.Ordinal) && int.TryParse(upper[3..], out var n) ? n < 12 ? "SKY1" : n < 21 ? "SKY2" : "SKY3" : "SKY1",
        };
    }

    /// <summary>Whether a monster has to be carried into a map of these settings.</summary>
    public static bool NeedsCarrying(MonsterKind m, GeneratorSettings s) => m.Family != s.Family || (m.Doom2 && !s.Doom2Things);

    /// <summary>
    /// Carries in what <paramref name="plan"/> uses from other games: <paramref name="into"/> is the project's game,
    /// <paramref name="monsterFrom"/>, <paramref name="lookFrom"/> and <paramref name="itemFrom"/> (by family) give the game each
    /// is taken from (null when none was found). Returns how they are named in the project to build the map with, and a
    /// sentence for each thing that could not come.
    /// </summary>
    public static (Lent Lent, IReadOnlyList<string> Notes) Carry(Project project, GameResources into, Func<MonsterKind, GameResources?> monsterFrom, Func<LookChoice, GameResources?> lookFrom, Func<string, GameResources?> itemFrom, MapPlan plan, string? engineData)
    {
        var s = plan.Settings;
        var notes = new List<string>();
        var numbers = new Dictionary<MonsterKind, int?>();
        foreach (var m in plan.Things.Select(t => t.Monster).OfType<MonsterKind>().Distinct().Where(m => NeedsCarrying(m, s)))
        {
            int? number = null;
            try
            {
                if (monsterFrom(m) is { } from && from.Things.FindClass(m.Class) is { } thing)
                {
                    if (ActorBorrowing.WhyNot(into, from, thing) is { } why) notes.Add(why);
                    else
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var carried = ActorBorrowing.Carry(project, into, from, thing);
                        number = carried.Number;
                        App.Log.Info(System.FormattableString.Invariant($"Make me a map: {m.Class} carried in from {from.Game.Title} ({carried.Sprites} sprites, {carried.Sounds} sounds) in {clock.ElapsedMilliseconds} ms"));
                    }
                }
                else
                {
                    notes.Add($"{m.Class} could not be found in your games, so it was left out.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
            {
                notes.Add($"{m.Class} could not be brought in: {ex.Message}");
            }

            numbers[m] = number;
        }

        // Weapons and their ammunition from other games, with their slots.
        var items = new Dictionary<(string, int), int?>();
        foreach (var (family, original) in plan.Things.Where(t => t.Family is { } f && f != s.Family).Select(t => (t.Family!, t.Number)).Distinct())
        {
            int? number = null;
            try
            {
                if (itemFrom(family) is { } from && from.Things.Find(original) is { } thing)
                {
                    if (ActorBorrowing.WhyNot(into, from, thing) is { } why) notes.Add(why);
                    else number = ActorBorrowing.Carry(project, into, from, thing).Number;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
            {
                notes.Add($"Object {original} of the {family} games could not be brought in: {ex.Message}");
            }

            items[(family, original)] = number;
        }

        if (numbers.Values.Concat(items.Values).Any(n => n is not null)) ProjectMapInfo.Refresh(project, into.Game, into.Files, engineData);

        var looks = new Dictionary<LookChoice, Look>();
        foreach (var choice in plan.Rooms.Select(r => r.Look).OfType<LookChoice>().Distinct().Where(l => l.Family != s.Family))
        {
            var look = choice.Look;
            if (lookFrom(choice) is { } from)
            {
                string Copy(string name, bool flat)
                {
                    try
                    {
                        return Borrowing.CopyPicture(project, into, from, name, flat);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                    {
                        notes.Add($"{name} could not be copied from {from.Game.Title}: {ex.Message}");
                        return name;
                    }
                }

                look = look with
                {
                    Wall = Copy(look.Wall, false),
                    Floor = Copy(look.Floor, true),
                    Ceiling = GeneratorSettings.IsSky(look.Ceiling) ? SkyOf(s.Family) : Copy(look.Ceiling, true),
                    Trim = look.Trim is { } trim ? Copy(trim, false) : null,
                    LightFlat = look.LightFlat is { } lit ? Copy(lit, true) : null,
                };
            }

            looks[choice] = look;
        }

        return (new Lent(m => numbers.GetValueOrDefault(m), l => looks.GetValueOrDefault(l) ?? l.Look, (family, number) => items.GetValueOrDefault((family, number)), name => into.Textures.FindWall(name)?.Height), notes);
    }
}

/// <summary>A made map's music (D50): a track picked by the seed from the games whose music the user checked, copied in when it is another game's.</summary>
public static class MadeMusic
{
    /// <summary>The track the seed picks from <paramref name="pool"/>'s level music (each game's tracks, in a fixed order), or null for none.</summary>
    public static (string Game, string Track)? Pick(string seed, IEnumerable<(string Game, string Track)> pool)
    {
        var tracks = pool.Where(p => IsLevelMusic(p.Track)).Distinct().OrderBy(p => p.Game, StringComparer.Ordinal).ThenBy(p => p.Track, StringComparer.OrdinalIgnoreCase).ToList();
        return tracks.Count == 0 ? null : tracks[new Seeded(SeedWords.Tidy(seed)).Fork("music").Next(tracks.Count)];
    }

    /// <summary>
    /// Whether a track plays in the games' levels, not on their title, story, intermission or ending screens (Doom's D_INTER,
    /// D_DM2TTL, D_READ_M, D_VICTOR, D_BUNNY; Heretic's MUS_TITL, MUS_INTR, MUS_CPTD; Hexen's HEXEN and HUB; Strife's D_LOGO).
    /// </summary>
    public static bool IsLevelMusic(string track)
    {
        var name = track.ToUpperInvariant();
        string[] screens = ["TITL", "TTL", "INTER", "INTRO", "INTR", "LOGO", "VICTOR", "BUNNY", "READ", "CPTD", "DM2INT", "_END", "ENDING", "CREDIT"];
        return !screens.Any(name.Contains) && name is not ("HEXEN" or "HUB");
    }

    /// <summary>
    /// Makes <paramref name="track"/> of <paramref name="from"/> the music of the project's map in <paramref name="slot"/>: the
    /// base game's own is named as it is; another game's is copied into the project's music folder (under a name of its own
    /// when the base game has one like it). Writes files: call it on a worker.
    /// </summary>
    public static string Use(Project project, GameResources into, GameResources from, string track, string slot, string? engineData)
    {
        var name = track.ToUpperInvariant();
        if (from.Game.Id != into.Game.Id)
        {
            var entry = from.Find(track, Core.Containers.ResourceNamespace.Music) ?? from.Find(track, Core.Containers.ResourceNamespace.Global) ?? throw new CorruptDataException($"{track} is not in {from.Game.Title}.");
            for (var n = 2; into.Find(name, Core.Containers.ResourceNamespace.Music) is not null || into.Find(name, Core.Containers.ResourceNamespace.Global) is not null; n++) name = $"{track.ToUpperInvariant()[..Math.Min(6, track.Length)]}M{n}";
            var file = $"music/{name}.lmp";
            project.WriteFiles([(file, entry.Read())]);
            project.Record([new CopiedAsset { Kind = "music", Game = from.Game.Id, Original = track, Name = name, File = file }]);
        }

        project.UpdateMap(slot, m => m with { Music = name });
        ProjectMapInfo.Refresh(project, into.Game, into.Files, engineData);
        return name;
    }
}
