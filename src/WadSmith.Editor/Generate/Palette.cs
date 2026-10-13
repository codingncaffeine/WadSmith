using System.Text.Json;
using WadSmith.Core;

namespace WadSmith.Editor.Generate;

/// <summary>A monster the generator can place: the game family it is from, its class, its number in that game, its health and size, its tier (1 weak .. 4 tough, 5 a boss), whether it flies, and whether only the Doom II family has it.</summary>
public sealed record MonsterKind(string Family, string Class, int Number, int Health, double Radius, int Tier, bool Flies, bool Boss, bool Doom2);

/// <summary>A weapon the generator hands out: its class and number, the ammunition it uses and how much of it comes with it; <paramref name="With"/> are the other classes' weapons placed beside it (Hexen).</summary>
public sealed record WeaponKind(string Class, int Number, string Ammo, int Gives, bool Doom2, IReadOnlyList<int> With);

/// <summary>Ammunition: the damage a unit does on average, and the small and big pickups with what each gives.</summary>
public sealed record AmmoKind(string Type, double Damage, int Small, int SmallAmount, int Big, int BigAmount);

/// <summary>A key: its colour (or name), its object, and the lock a door takes for it.</summary>
public sealed record KeyKind(string Colour, int Number, int Lock);

/// <summary>
/// What the generator places in a game family (data/generate/palette.json): monsters by tier, weapons in the order a map
/// hands them out, ammunition, health, armour, keys and decorations, with the engine's own editor numbers.
/// </summary>
public sealed record FamilyPalette(
    string Family,
    double StartDamage,
    IReadOnlyList<MonsterKind> Monsters,
    IReadOnlyList<WeaponKind> Weapons,
    IReadOnlyList<AmmoKind> Ammo,
    (int Small, int SmallAmount, int Big, int BigAmount, int Super) Health,
    (int Light, int Heavy) Armor,
    IReadOnlyList<KeyKind> Keys,
    IReadOnlyList<int> Decorations,
    IReadOnlyList<int> OutdoorDecorations)
{
    /// <summary>The game families the generator makes maps for.</summary>
    public static IReadOnlyList<string> Families => [.. Table.Value.Keys];

    /// <summary>The palette of <paramref name="family"/> ("doom", "heretic", "hexen", "strife"), or null when the generator does not know it.</summary>
    public static FamilyPalette? For(string family) => Table.Value.GetValueOrDefault(family);

    /// <summary>The ammunition of a type.</summary>
    public AmmoKind? AmmoOf(string type) => Ammo.FirstOrDefault(a => a.Type == type);

    private static readonly Lazy<Dictionary<string, FamilyPalette>> Table = new(() =>
    {
        using var doc = JsonDocument.Parse(DataFiles.ReadText("generate/palette.json"));
        var result = new Dictionary<string, FamilyPalette>(StringComparer.Ordinal);
        foreach (var family in doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
        {
            var raw = family.Value.Deserialize<Raw>(Json)!;
            result[family.Name] = new FamilyPalette(
                family.Name,
                raw.Start,
                [.. raw.Monsters.Select(m => new MonsterKind(family.Name, m.Class, m.Number, m.Health, m.Radius, m.Tier, m.Flies, m.Boss, m.Doom2))],
                [.. raw.Weapons.Select(w => new WeaponKind(w.Class, w.Number, w.Ammo, w.Gives, w.Doom2, w.With ?? []))],
                [.. raw.Ammo.Select(a => new AmmoKind(a.Type, a.Damage, a.Small, a.SmallAmount, a.Big, a.BigAmount))],
                (raw.Health.Small, raw.Health.SmallAmount, raw.Health.Big, raw.Health.BigAmount, raw.Health.Super),
                (raw.Armor.Light, raw.Armor.Heavy),
                [.. raw.Keys.Select(k => new KeyKind(k.Colour, k.Number, k.Lock))],
                raw.Decorations,
                raw.OutdoorDecorations);
        }

        return result;
    });

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Raw
    {
        public double Start { get; set; }

        public List<RawMonster> Monsters { get; set; } = [];

        public List<RawWeapon> Weapons { get; set; } = [];

        public List<RawAmmo> Ammo { get; set; } = [];

        public RawHealth Health { get; set; } = new();

        public RawArmor Armor { get; set; } = new();

        public List<RawKey> Keys { get; set; } = [];

        public List<int> Decorations { get; set; } = [];

        public List<int> OutdoorDecorations { get; set; } = [];
    }

    private sealed class RawMonster
    {
        public string Class { get; set; } = "";

        public int Number { get; set; }

        public int Health { get; set; }

        public double Radius { get; set; }

        public int Tier { get; set; }

        public bool Flies { get; set; }

        public bool Boss { get; set; }

        public bool Doom2 { get; set; }
    }

    private sealed class RawWeapon
    {
        public string Class { get; set; } = "";

        public int Number { get; set; }

        public string Ammo { get; set; } = "";

        public int Gives { get; set; }

        public bool Doom2 { get; set; }

        public List<int>? With { get; set; }
    }

    private sealed class RawAmmo
    {
        public string Type { get; set; } = "";

        public double Damage { get; set; }

        public int Small { get; set; }

        public int SmallAmount { get; set; }

        public int Big { get; set; }

        public int BigAmount { get; set; }
    }

    private sealed class RawHealth
    {
        public int Small { get; set; }

        public int SmallAmount { get; set; }

        public int Big { get; set; }

        public int BigAmount { get; set; }

        public int Super { get; set; }
    }

    private sealed class RawArmor
    {
        public int Light { get; set; }

        public int Heavy { get; set; }
    }

    private sealed class RawKey
    {
        public string Colour { get; set; } = "";

        public int Number { get; set; }

        public int Lock { get; set; }
    }
}
