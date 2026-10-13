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

    /// <summary>Whether a monster has to be carried into a map of these settings.</summary>
    public static bool NeedsCarrying(MonsterKind m, GeneratorSettings s) => m.Family != s.Family || (m.Doom2 && !s.Doom2Things);

    /// <summary>
    /// Carries in what <paramref name="plan"/> uses from other games: <paramref name="into"/> is the project's game,
    /// <paramref name="monsterFrom"/> and <paramref name="lookFrom"/> give the game each is taken from (null when none was found).
    /// Returns the numbers and looks to build the map with, and a sentence for each thing that could not come.
    /// </summary>
    public static (Func<MonsterKind, int?> Numbers, Func<LookChoice, Look> Looks, IReadOnlyList<string> Notes) Carry(Project project, GameResources into, Func<MonsterKind, GameResources?> monsterFrom, Func<LookChoice, GameResources?> lookFrom, MapPlan plan, string? engineData)
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

        if (numbers.Values.Any(n => n is not null)) ProjectMapInfo.Refresh(project, into.Game, into.Files, engineData);

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
                };
            }

            looks[choice] = look;
        }

        return (m => numbers.GetValueOrDefault(m), l => looks.GetValueOrDefault(l) ?? l.Look, notes);
    }
}
