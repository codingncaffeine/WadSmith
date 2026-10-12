using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void TheDataFilesAreBuiltInUnderForwardSlashNames()
    {
        Assert.Contains("games/games.json", DataFiles.All);
    }

    [Fact]
    public void TheGameListLoadsAndEveryEntryIsValid()
    {
        Assert.Contains(GameCatalog.All, g => g.Id == "doom2");
        Assert.All(GameCatalog.All, g => Assert.NotEmpty(g.MustContain));
    }

    [Fact]
    public void ADuplicateIdIsRejected()
    {
        const string json = """{ "games": [ { "id": "a", "title": "A", "engine": "doom", "mapNamespace": "doom", "mapNames": "MAPxx", "mustContain": ["MAP01"] }, { "id": "a", "title": "B", "engine": "doom", "mapNamespace": "doom", "mapNames": "MAPxx", "mustContain": ["MAP01"] } ] }""";
        var ex = Assert.Throws<InvalidDataException>(() => GameCatalog.Parse(json));
        Assert.Contains("lists \"a\" twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFieldIsRejectedSoTyposCannotHide()
    {
        const string json = """{ "games": [ { "id": "a", "title": "A", "engine": "doom", "mapNamespace": "doom", "mapNames": "MAPxx", "mustContian": ["MAP01"] } ] }""";
        Assert.ThrowsAny<Exception>(() => GameCatalog.Parse(json));
    }

    [Fact]
    public void AnUnmatchedWadIsNoGame()
    {
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [("STUFF", Array.Empty<byte>())]));
        Assert.Null(GameCatalog.Identify(wad));
    }

    [Theory]
    [InlineData("DOOM.WAD", "doom-ultimate")]
    [InlineData("DOOM2.WAD", "doom2")]
    [InlineData("PLUTONIA.WAD", "plutonia")]
    [InlineData("TNT.WAD", "tnt")]
    [InlineData("HERETIC.WAD", "heretic")]
    [InlineData("HEXEN.WAD", "hexen")]
    [InlineData("STRIFE1.WAD", "strife")]
    [InlineData("DOOM64.WAD", "doom64")]
    [InlineData("freedoom1.wad", "freedoom1")]
    [InlineData("freedoom2.wad", "freedoom2")]
    public void RealBaseGamesAreIdentifiedByTheirLumps(string fileName, string id)
    {
        var game = GameCatalog.Identify(WadContainer.Open(GameFiles.Require(fileName)));
        Assert.Equal(id, game?.Id);
    }

    [Fact]
    public void AnAddOnIsNotABaseGame()
    {
        Assert.Null(GameCatalog.Identify(WadContainer.Open(GameFiles.Require("SIGIL_v1_21.wad"))));
    }
}

/// <summary>Identification over every copy of each file in the test folders (classic, re-release and add-ons).</summary>
public sealed class GameIdentificationTests
{
    [Theory]
    [InlineData("doom.wad", "doom-ultimate")]
    [InlineData("doom2.wad", "doom2")]
    [InlineData("tnt.wad", "tnt")]
    [InlineData("plutonia.wad", "plutonia")]
    [InlineData("heretic.wad", "heretic-sotsr|heretic")]
    [InlineData("hexen.wad", "hexen")]
    [InlineData("hexdd.wad", "hexen-deathkings")]
    [InlineData("strife1.wad", "strife")]
    [InlineData("sve.wad", "strife-veteran")]
    [InlineData("freedoom1.wad", "freedoom1")]
    [InlineData("freedoom2.wad", "freedoom2")]
    public void EveryCopyOfABaseGameIsIdentified(string fileName, string ids)
    {
        var copies = GameFiles.FindAll(fileName);
        if (copies.Count == 0) Assert.Skip($"no {fileName} in {GameFiles.Variable}");
        var allowed = ids.Split('|');
        foreach (var path in copies)
        {
            var game = GameCatalog.Identify(WadContainer.Open(path));
            Assert.True(game is not null && allowed.Contains(game.Id), $"{path} was identified as {game?.Id ?? "nothing"}");
        }
    }

    [Theory]
    [InlineData("id1.wad")]
    [InlineData("iddm1.wad")]
    [InlineData("masterlevels.wad")]
    [InlineData("nerve.wad")]
    [InlineData("sigil.wad")]
    [InlineData("sigil2.wad")]
    [InlineData("heretic_ex.wad")]
    [InlineData("heretic_fr.wad")]
    [InlineData("hexen_ex.wad")]
    [InlineData("hexen_vog.wad")]
    [InlineData("SIGIL_v1_21.wad")]
    public void AnAddOnIsNeverTakenForABaseGame(string fileName)
    {
        var copies = GameFiles.FindAll(fileName);
        if (copies.Count == 0) Assert.Skip($"no {fileName} in {GameFiles.Variable}");
        foreach (var path in copies)
        {
            var game = GameCatalog.Identify(WadContainer.Open(path));
            Assert.True(game is null || !game.IsBaseGame, $"{path} was taken for the base game {game?.Id}");
        }
    }
}

public sealed class MapTitleTests
{
    [Theory]
    [InlineData("Level 1: Entryway", "Entryway")]
    [InlineData("E1M1: Hangar", "Hangar")]
    [InlineData("E1M1:  THE DOCKS", "The Docks")]
    [InlineData("WINNOWING HALL", "Winnowing Hall")]
    public void TitlesLoseTheirPrefixAndTheirShouting(string raw, string clean) => Assert.Equal(clean, MapTitles.Clean(raw));

    [Theory]
    [InlineData("DOOM2.WAD", "MAP01", "Entryway")]
    [InlineData("DOOM.WAD", "E1M1", "Hangar")]
    [InlineData("HEXEN.WAD", "MAP01", "Winnowing Hall")]
    public void TheEngineNamesTheBaseGamesMaps(string file, string slot, string title)
    {
        var wad = WadContainer.Open(GameFiles.Require(file));
        var titles = MapTitles.For(GameCatalog.Identify(wad)!, wad, GameFiles.Require("gzdoom.pk3"));
        Assert.Equal(title, titles.GetValueOrDefault(slot));
    }

    [Fact]
    public void FreedoomNamesItsOwnMaps()
    {
        var wad = WadContainer.Open(GameFiles.Require("freedoom2.wad"));
        var titles = MapTitles.For(GameCatalog.Identify(wad)!, wad, GameFiles.Require("gzdoom.pk3"));
        Assert.True(titles.TryGetValue("MAP01", out var t) && t != "Entryway", $"Freedoom's MAP01 was named {t ?? "nothing"}");
    }
}
