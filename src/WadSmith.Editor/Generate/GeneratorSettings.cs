namespace WadSmith.Editor.Generate;

/// <summary>How big a generated map is: its rooms and about how long it plays.</summary>
public enum MapSize
{
    Small,
    Medium,
    Large,
    Huge,
}

/// <summary>What the rooms are like: tech-base rectangles, caves with cut corners, castle halls with pillars, or a mix by part of the map.</summary>
public enum MapStyle
{
    Base,
    Caves,
    Castle,
    Mixed,
}

/// <summary>The share of rooms open to the sky.</summary>
public enum OutdoorShare
{
    None,
    Some,
    Lots,
}

/// <summary>A look a generated room can have: its name, the look itself, the game family it is from, and whether it is open to the sky.</summary>
public sealed record LookChoice(string Name, Look Look, string Family, bool Outdoors);

/// <summary>
/// Everything "Make me a map" asks (D50, §3.14): the game it plays in, what it may use from which games, its size, the share
/// outdoors, the challenge, locked doors, bosses, the features, and the two-word seed. The same settings and seed make the
/// same map on any machine.
/// </summary>
public sealed record GeneratorSettings
{
    /// <summary>The base game's id (games.json), which the project records.</summary>
    public string GameId { get; init; } = "doom2";

    /// <summary>The base game's family: "doom", "heretic", "hexen" or "strife".</summary>
    public string Family { get; init; } = "doom";

    /// <summary>The map's slot: MAP01 or E1M1.</summary>
    public string Slot { get; init; } = "MAP01";

    /// <summary>Whether the base game has the Doom II family's monsters and weapons (Doom II, Final Doom, Freedoom: Phase 2).</summary>
    public bool Doom2Things { get; init; } = true;

    /// <summary>Whether the map plays in GZDoom (dynamic lights allowed); false for a classic target.</summary>
    public bool GZDoom { get; init; } = true;

    public MapSize Size { get; init; } = MapSize.Medium;

    /// <summary>How many maps: one, or an episode of several in a row, rising in challenge, the bosses in the last.</summary>
    public int Maps { get; init; } = 1;

    public MapStyle Style { get; init; } = MapStyle.Base;

    public OutdoorShare Outdoors { get; init; } = OutdoorShare.Some;

    /// <summary>From 0 (gentle) to 1 (brutal): how many monsters, how tough, and how much ammunition and health to spare.</summary>
    public double Challenge { get; init; } = 0.5;

    /// <summary>How many locked doors, each with its key earlier on the way (0 to 3).</summary>
    public int Keys { get; init; } = 2;

    /// <summary>How many bosses wait in the arena before the exit (0 to 3).</summary>
    public int Bosses { get; init; } = 1;

    public bool Secrets { get; init; } = true;

    public bool Stairs { get; init; } = true;

    public bool Lifts { get; init; } = true;

    public bool Teleporters { get; init; } = true;

    /// <summary>Dim rooms with a light or two, beside bright ones.</summary>
    public bool DarkPlaces { get; init; } = true;

    /// <summary>Monsters lying in wait (deaf until they see the player) in some rooms.</summary>
    public bool Ambushes { get; init; } = true;

    /// <summary>Pools in some rooms: water, and the game's hurting floors (nukage, slime, lava).</summary>
    public bool Hazards { get; init; } = true;

    /// <summary>Monster closets: small rooms beside a room whose door opens when the player walks in.</summary>
    public bool Traps { get; init; } = true;

    /// <summary>Decorations in the rooms: lamps, barrels, columns, trees outside.</summary>
    public bool Decorations { get; init; } = true;

    /// <summary>The two words the map is made from, and named after.</summary>
    public string Seed { get; init; } = "misty-cavern";

    /// <summary>The monsters it may use; empty means the base game's own.</summary>
    public IReadOnlyList<MonsterKind> Monsters { get; init; } = [];

    /// <summary>The game families whose weapons (and their ammunition) join the base game's along the way.</summary>
    public IReadOnlyList<string> WeaponsFrom { get; init; } = [];

    /// <summary>The games (by id) whose music the map may play, a track picked by the seed; empty means the base game's.</summary>
    public IReadOnlyList<string> MusicFrom { get; init; } = [];

    /// <summary>The looks its rooms may have; empty means the base game's own.</summary>
    public IReadOnlyList<LookChoice> Looks { get; init; } = [];

    /// <summary>The settings for a map in <paramref name="game"/>: its family, its first map slot, and whether it has the Doom II family's monsters.</summary>
    public static GeneratorSettings ForGame(WadSmith.Core.Games.GameDefinition game) => new()
    {
        GameId = game.Id,
        Family = game.Engine,
        Slot = game.MapNames == "ExMy" ? "E1M1" : "MAP01",
        Doom2Things = game.MapNames != "ExMy",
    };

    /// <summary>Whether the generator makes maps for <paramref name="game"/>'s family.</summary>
    public static bool CanMake(WadSmith.Core.Games.GameDefinition game) => game.Supported && FamilyPalette.For(game.Engine) is not null;

    /// <summary>How many rooms a map of <paramref name="size"/> has.</summary>
    public static int RoomsFor(MapSize size) => size switch
    {
        MapSize.Small => 8,
        MapSize.Medium => 14,
        MapSize.Large => 24,
        _ => 40,
    };

    /// <summary>The base game's own monsters, as the palette lists them (the Doom II family's left out of a game without them).</summary>
    public IReadOnlyList<MonsterKind> OwnMonsters() =>
        FamilyPalette.For(Family) is { } p ? [.. p.Monsters.Where(m => Doom2Things || !m.Doom2)] : [];

    /// <summary>The looks the rooms may have: those chosen, else the base game's own named looks plus its outdoors look.</summary>
    public IReadOnlyList<LookChoice> LooksOrOwn() => Looks.Count > 0 ? Looks : OwnLooks(Family);

    /// <summary>A family's own looks: its named looks (those open to the sky marked) and an outdoors look when it has none.</summary>
    public static IReadOnlyList<LookChoice> OwnLooks(string family)
    {
        var named = Look.Named(family).Select(n => new LookChoice(n.Name, n.Look, family, IsSky(n.Look.Ceiling))).ToList();
        if (!named.Any(n => n.Outdoors) && OutdoorLook(family) is { } outside) named.Add(new LookChoice("Outdoors", outside, family, true));
        return named;
    }

    /// <summary>Whether a ceiling picture is the sky.</summary>
    public static bool IsSky(string flat) => flat.StartsWith("F_SKY", StringComparison.OrdinalIgnoreCase);

    /// <summary>An outdoors look for the families whose looks have none.</summary>
    private static Look? OutdoorLook(string family) => family switch
    {
        "hexen" => Look.For("hexen") with { Wall = "WASTE01", Floor = "F_001", Ceiling = "F_SKY", CeilingHeight = 256, Light = 208 },
        "strife" => Look.For("strife") with { Wall = "BRKGRY01", Floor = "F_DIRT01", Ceiling = "F_SKY001", CeilingHeight = 256, Light = 208 },
        _ => null,
    };
}

/// <summary>
/// An episode (D50 "later": maps that share a style and rise in challenge): the settings of each of its maps — its slot after
/// the first (MAP01, MAP02 … or E1M1, E1M2 …), a seed of its own (the first map keeps the episode's), the challenge rising by
/// 0.3 from the first map to the last round the chosen one, and the bosses in the last map (one waits halfway through an
/// episode of five or more).
/// </summary>
public static class Episodes
{
    public static IReadOnlyList<GeneratorSettings> Of(GeneratorSettings s)
    {
        var n = Math.Clamp(s.Maps, 1, 8);
        if (n == 1) return [s];
        var seed = SeedWords.Tidy(s.Seed);
        return [.. Enumerable.Range(0, n).Select(i => s with
        {
            Maps = 1,
            Slot = SlotAfter(s.Slot, i),
            Seed = i == 0 ? seed : $"{seed}-{i + 1}",
            Challenge = Math.Clamp(s.Challenge - 0.15 + 0.3 * i / (n - 1), 0, 1),
            Bosses = i == n - 1 ? s.Bosses : n >= 5 && i == n / 2 ? Math.Min(1, s.Bosses) : 0,
        })];
    }

    /// <summary>The slot <paramref name="i"/> maps after <paramref name="first"/>: MAP01 → MAP03, E1M1 → E1M3.</summary>
    public static string SlotAfter(string first, int i)
    {
        if (first.Length == 4 && first[0] is 'E' or 'e' && first[2] is 'M' or 'm') return $"E{first[1]}M{first[3] - '0' + i}";
        return $"MAP{int.Parse(first.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture) + i:00}";
    }

    /// <summary>The title of an episode's map: the first map has the episode's own; each other one, two words of its own from the seed.</summary>
    public static string TitleOf(GeneratorSettings episode, int i)
    {
        if (i == 0) return SeedWords.Title(episode.Seed);
        var words = new Seeded(SeedWords.Tidy(episode.Seed)).Fork($"title {i}");
        return SeedWords.Title(SeedWords.Pick(words));
    }
}
