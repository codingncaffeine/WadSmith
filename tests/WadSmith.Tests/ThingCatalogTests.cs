using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

/// <summary>Objects read from GZDoom's own definitions: numbers, names, first sprite frames and sizes.</summary>
public sealed class ThingCatalogTests
{
    private static readonly Lazy<ResourceContainer> Engine = new(() => ResourceContainer.Open(GameFiles.Require("gzdoom.pk3")));

    [Theory]
    [InlineData("doom", 3001, "DoomImp", "Imp", "TROO", 'A', 20, 56)]
    [InlineData("doom", 3004, "ZombieMan", "Zombieman", "POSS", 'A', 20, 56)]
    [InlineData("doom", 2035, "ExplosiveBarrel", "Explosive barrel", "BAR1", 'A', 10, 42)]
    [InlineData("doom", 30, "TallGreenColumn", "Tall green column", "COL1", 'A', 16, 52)]
    [InlineData("heretic", 66, "HereticImp", "Gargoyle", "IMPX", 'A', 16, 36)]
    [InlineData("hexen", 107, "Centaur", "Centaur", "CENT", 'A', 20, 64)]
    // The game calls every acolyte "Acolyte"; the shelf tells the tan, red and rust ones apart.
    [InlineData("strife", 3002, "AcolyteTan", "Tan acolyte", "AGRD", 'A', 24, 64)]
    public void KnownObjectsReadAsTheEngineDefinesThem(string family, int number, string className, string name, string sprite, char frame, double radius, double height)
    {
        var t = ThingCatalog.Load(Engine.Value, family).Find(number);
        Assert.NotNull(t);
        Assert.Equal(className, t.ClassName);
        Assert.Equal(name, t.Name);
        Assert.Equal(sprite, t.Sprite);
        Assert.Equal(frame, t.Frame);
        Assert.Equal(radius, t.Radius);
        Assert.Equal(height, t.Height);
    }

    [Theory]
    [InlineData("doom", 1, "DoomPlayer", "Player 1 start")]
    [InlineData("doom", 11, "DoomPlayer", "Deathmatch start")]
    [InlineData("heretic", 1, "HereticPlayer", "Player 1 start")]
    [InlineData("hexen", 9100, "FighterPlayer", "Player 5 start")]
    public void PlayerStartsAreKnown(string family, int number, string className, string name)
    {
        var t = ThingCatalog.Load(Engine.Value, family).Find(number);
        Assert.NotNull(t);
        Assert.Equal(className, t.ClassName);
        Assert.Equal(name, t.Name);
        Assert.Equal("PLAY", t.Sprite);
    }

    [Theory]
    [InlineData("doom", 3001, ThingCategory.Monster)]
    [InlineData("doom", 2001, ThingCategory.Weapon)]
    [InlineData("doom", 2007, ThingCategory.Ammo)]
    [InlineData("doom", 2011, ThingCategory.Health)]
    [InlineData("doom", 2013, ThingCategory.Health)]
    [InlineData("doom", 2018, ThingCategory.Armor)]
    [InlineData("doom", 13, ThingCategory.Key)]
    [InlineData("doom", 2024, ThingCategory.Powerup)]
    [InlineData("doom", 8, ThingCategory.Powerup)]
    [InlineData("doom", 2035, ThingCategory.Decoration)]
    [InlineData("doom", 14, ThingCategory.Marker)]
    [InlineData("doom", 1, ThingCategory.Start)]
    [InlineData("heretic", 66, ThingCategory.Monster)]
    [InlineData("hexen", 107, ThingCategory.Monster)]
    [InlineData("strife", 3002, ThingCategory.Monster)]
    public void ObjectsAreSortedByKindFromTheEnginesClasses(string family, int number, ThingCategory category)
    {
        Assert.Equal(category, ThingCatalog.Load(Engine.Value, family).Find(number)?.Category);
    }

    [Theory]
    [InlineData(20, "Dead imp", ThingCategory.Decoration, "TROO", 'M')]
    [InlineData(18, "Dead zombieman", ThingCategory.Decoration, "POSS", 'L')]
    [InlineData(9057, "Stealth imp", ThingCategory.Monster, "TROO", 'A')]
    [InlineData(3001, "Imp", ThingCategory.Monster, "TROO", 'A')]
    public void DeadAndStealthVariantsAreNamedAndSortedAsWhatTheyAre(int number, string name, ThingCategory category, string sprite, char frame)
    {
        var t = ThingCatalog.Load(Engine.Value, "doom").Find(number);
        Assert.NotNull(t);
        Assert.Equal((name, category, sprite, frame), (t.Name, t.Category, t.Sprite, t.Frame));
    }

    [Fact]
    public void EveryDoomMonsterIsAMonster()
    {
        var doom = ThingCatalog.Load(Engine.Value, "doom");
        int[] monsters = [3004, 9, 65, 3001, 3002, 58, 3006, 3005, 69, 3003, 68, 71, 66, 67, 64, 16, 7, 84, 72];
        Assert.All(monsters, n => Assert.Equal(ThingCategory.Monster, doom.Find(n)?.Category));
    }

    [Fact]
    public void EveryDoomObjectHasANameAndEveryGameObjectASprite()
    {
        var all = ThingCatalog.Load(Engine.Value, "doom").All;
        Assert.True(all.Count > 120, $"only {all.Count} Doom objects");
        var nameless = all.Where(t => string.IsNullOrWhiteSpace(t.Name)).Select(t => t.ClassName).ToList();
        Assert.True(nameless.Count == 0, "nameless: " + string.Join(", ", nameless));

        // Doom's own objects (numbers below 4000) all show in the game, except the invisible markers:
        // teleport destinations, the boss brain's targets and Vavoom lights.
        int[] markers = [14, 87, 1502, 1503];
        var invisible = all.Where(t => t.Number < 4000 && !markers.Contains(t.Number) && t.Sprite is null).Select(t => $"{t.Number}={t.ClassName}").ToList();
        Assert.True(invisible.Count == 0, "without a sprite: " + string.Join(", ", invisible));
    }
}
