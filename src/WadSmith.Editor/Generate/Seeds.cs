namespace WadSmith.Editor.Generate;

/// <summary>
/// The generator's randomness: a SplitMix64 stream seeded from a seed's words by FNV-1a, so the same words give the same map
/// on any machine and any .NET (System.Random's sequence is not promised across versions). Seeds are two words, an
/// adjective and a noun ("misty-cavern"), which also name the map and its folder.
/// </summary>
public sealed class Seeded
{
    private ulong _state;

    public Seeded(string seed)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in seed.Trim().ToLowerInvariant())
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        _state = hash;
    }

    private Seeded(ulong state) => _state = state;

    /// <summary>A stream of its own for one part of the work, so a change in one part leaves the others' draws alone.</summary>
    public Seeded Fork(string part)
    {
        var child = new Seeded(part);
        return new Seeded(_state ^ child._state ^ 0x9E3779B97F4A7C15UL);
    }

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A whole number from 0 up to but not including <paramref name="max"/>.</summary>
    public int Next(int max) => max <= 1 ? 0 : (int)(NextUInt64() % (ulong)max);

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>, both included.</summary>
    public int Between(int min, int max) => max <= min ? min : min + Next(max - min + 1);

    /// <summary>A number from 0 up to 1.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double p) => NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>The two-word seeds: an adjective and a noun, picked by a shuffle or typed by the user.</summary>
public static class SeedWords
{
    private static readonly string[] Adjectives =
    [
        "misty", "rusty", "silent", "molten", "hollow", "broken", "frozen", "crimson", "ashen", "forgotten",
        "sunken", "burning", "shadow", "iron", "bitter", "grim", "toxic", "ancient", "lonely", "savage",
        "hidden", "twisted", "pale", "dusty", "echoing", "flooded", "jagged", "smoky", "restless", "cursed",
        "golden", "rotten", "bleak", "copper", "crumbling", "dread", "eerie", "feral", "gloomy", "haunted",
    ];

    private static readonly string[] Nouns =
    [
        "cavern", "foundry", "citadel", "outpost", "reactor", "crypt", "bastion", "refinery", "temple", "harbor",
        "warrens", "keep", "vault", "depot", "sanctum", "pit", "spire", "canal", "bunker", "hangar",
        "labyrinth", "mines", "garrison", "chapel", "furnace", "barracks", "archive", "quarry", "tower", "gate",
        "cistern", "dungeon", "factory", "hollow", "lab", "ruins", "station", "tomb", "works", "yard",
    ];

    /// <summary>A new seed from <paramref name="random"/>.</summary>
    public static string Shuffle(Random random) => $"{Adjectives[random.Next(Adjectives.Length)]}-{Nouns[random.Next(Nouns.Length)]}";

    /// <summary>A seed tidied for use: lower case, words joined by '-', nothing but letters, digits and '-'.</summary>
    public static string Tidy(string seed)
    {
        var words = seed.Trim().ToLowerInvariant().Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)])).Where(w => w.Length > 0);
        var tidy = string.Join('-', words);
        return tidy.Length == 0 ? "misty-cavern" : tidy;
    }

    /// <summary>The seed as a map's name: "Misty Cavern".</summary>
    public static string Title(string seed) => string.Join(' ', Tidy(seed).Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
