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
    [InlineData("strife", 3002, "AcolyteTan", "Acolyte", "AGRD", 'A', 24, 64)]
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
