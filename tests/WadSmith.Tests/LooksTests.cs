using WadSmith.Core.Games;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>Looks: every texture a look puts on the map is one the game has (the default look in every game of its family).</summary>
public sealed class LooksTests(ITestOutputHelper output)
{
    /// <summary>The games to hold the looks against, one file each: Doom (both), Freedoom (both), Heretic, Hexen, Strife.</summary>
    public static TheoryData<string> Games() => new() { "DOOM.WAD", "DOOM2.WAD", "freedoom1.wad", "freedoom2.wad", "HERETIC.WAD", "HEXEN.WAD", "STRIFE1.WAD" };

    [Theory]
    [MemberData(nameof(Games))]
    public void EveryTextureOfTheDefaultLookIsInTheGameAndTheOtherLooksSayWhetherTheyAre(string fileName)
    {
        var wad = GameFiles.Require(fileName);
        var found = GameLocator.Scan([Path.GetDirectoryName(wad)!], cancel: TestContext.Current.CancellationToken).Where(g => string.Equals(g.Path, wad, StringComparison.OrdinalIgnoreCase)).ToList();
        var game = Assert.Single(found);
        var resources = GameResources.Load(game.Game.Id, null, found, GameFiles.Require("gzdoom.pk3"));
        Assert.NotNull(resources);
        var engine = game.Game.Engine;
        bool Has((string Name, bool Flat) t) => t.Flat ? resources.Textures.FindFlat(t.Name) is not null : resources.Textures.FindWall(t.Name) is not null;

        // The default: the starter room and every new room use it, so all of it must be there.
        var missing = Look.For(engine).Textures().Where(t => !Has(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, $"{fileName} ({engine}) lacks the default look's {string.Join(", ", missing)}");

        var named = Look.Named(engine);
        Assert.Equal(Look.For(engine), named[0].Look);
        var offered = Look.Available(engine, resources);
        foreach (var (name, look) in named)
        {
            var lacks = look.Textures().Where(t => !Has(t)).Select(t => t.Name).ToList();
            Assert.Equal(lacks.Count == 0, offered.Any(o => o.Name == name));
            output.WriteLine($"{fileName}: {name} {(lacks.Count == 0 ? "offered" : $"not offered, lacking {string.Join(", ", lacks)}")}");
        }

        // Doom's own looks are all in Doom II; Doom (1) lacks the outdoor rocks.
        if (fileName is "DOOM2.WAD" or "freedoom2.wad") Assert.Equal(named.Count, offered.Count);
        if (fileName == "DOOM.WAD") Assert.DoesNotContain(offered, o => o.Name == "Outdoors");
    }
}
