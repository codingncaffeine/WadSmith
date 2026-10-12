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
