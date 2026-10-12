using System.Globalization;
using WadSmith.Core.Games;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Checks;

/// <summary>
/// How one difficulty plays out: the monsters' health against the damage the ammunition can do (<c>Ratio</c>: 1 is exactly
/// enough), the health to pick up, and the sentence that says so.
/// </summary>
public sealed record BalanceReading(string Difficulty, int Monsters, double Ratio, int Health, string Sentence);

/// <summary>
/// The Balance meter (D29): per difficulty, the monsters' total health (the engine's own numbers) against the damage the
/// ammunition placed can do, counting the bullets the player starts with, and the health to pick up. Damage per unit is
/// the average of the weapon that uses it (decided 2026-10-12 from the engine's damage formulas: a bullet 1d3×5 = 10, a
/// shell seven pellets of 10 = 70, a rocket 1d8×20 = 90 plus its blast, taken as 100, a cell 1d8×5 = 22.5, taken as 22).
/// Infighting, the berserk fist and splash on crowds are left out, so the sentence errs on the safe side.
/// </summary>
public static class Balance
{
    private static readonly Dictionary<string, double> DoomDamage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Clip"] = 10,
        ["Shell"] = 70,
        ["RocketAmmo"] = 100,
        ["Cell"] = 22,
    };

    /// <summary>The bullets the player starts a map with (a pistol start).</summary>
    public const int StartingBullets = 50;

    private static readonly (string Name, string Flag)[] Difficulties = [("Easy", "skill2"), ("Normal", "skill3"), ("Hard", "skill4")];

    /// <summary>The reading for each difficulty; null for a game whose weapons the meter does not know yet (only the Doom family's so far).</summary>
    public static IReadOnlyList<BalanceReading>? Read(IEnumerable<Thing> things, ThingCatalog catalog, string engine)
    {
        if (engine is not ("doom" or "chex")) return null;
        var all = things.Where(t => t.OnMap && t.Flag("single")).ToList();
        var readings = new List<BalanceReading>();
        foreach (var (name, flag) in Difficulties)
        {
            double monsterHealth = 0, damage = StartingBullets * DoomDamage["Clip"];
            int monsters = 0, health = 0;
            foreach (var t in all.Where(t => t.Flag(flag)))
            {
                if (catalog.Find(t.Type) is not { } kind) continue;
                switch (kind.Category)
                {
                    case ThingCategory.Monster:
                        monsters++;
                        monsterHealth += kind.Health ?? 0;
                        break;
                    case ThingCategory.Ammo when kind.AmmoType is { } ammo && DoomDamage.TryGetValue(ammo, out var per):
                        damage += kind.Amount * per;
                        break;
                    case ThingCategory.Weapon when kind.AmmoType is { } ammo && DoomDamage.TryGetValue(ammo, out var per):
                        damage += kind.AmmoGive * per;
                        break;
                    case ThingCategory.Health:
                        health += kind.Amount;
                        break;
                }
            }

            var ratio = monsterHealth <= 0 ? double.PositiveInfinity : damage / monsterHealth;
            readings.Add(new(name, monsters, ratio, health, Sentence(name, monsters, ratio, health)));
        }

        return readings;
    }

    private static string Sentence(string difficulty, int monsters, double ratio, int health)
    {
        if (monsters == 0) return $"{difficulty}: no monsters to fight.";
        var ammo = ratio switch
        {
            >= 1.5 => string.Create(CultureInfo.InvariantCulture, $"plenty of ammunition, about {ratio:0.0} times what the monsters can take"),
            >= 1 => string.Create(CultureInfo.InvariantCulture, $"just enough ammunition, about {ratio:0.0} times what the monsters can take, with little to waste"),
            < 0.15 => "the player runs out of ammunition long before the monsters run out",
            _ => $"the player runs dry {Through(ratio)} the monsters",
        };
        var healing = health == 0 ? "no health to pick up" : string.Create(CultureInfo.InvariantCulture, $"{health} health to pick up");
        return $"{difficulty}: {ammo}; {healing}.";
    }

    private static string Through(double ratio) => ratio switch
    {
        < 0.3 => "about a quarter of the way through",
        < 0.45 => "about a third of the way through",
        < 0.6 => "about halfway through",
        < 0.75 => "about two thirds of the way through",
        _ => "about three quarters of the way through",
    };
}
