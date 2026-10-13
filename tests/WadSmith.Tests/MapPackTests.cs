using System.IO.Compression;
using WadSmith.Core.Containers;
using WadSmith.Core.Engines;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>Map packs found near the games (D52): matched to the base game each is for, and one click from editing with the pack loaded.</summary>
public sealed class MapPackTests
{
    private static readonly FoundGame Freedoom1 = new(GameCatalog.Get("freedoom1")!, "/g/freedoom1.wad", 1);
    private static readonly FoundGame Freedoom2 = new(GameCatalog.Get("freedoom2")!, "/g/freedoom2.wad", 1);
    private static readonly FoundGame Heretic = new(GameCatalog.Get("heretic")!, "/g/heretic.wad", 1);
    private static readonly FoundGame[] Games = [Freedoom1, Freedoom2, Heretic];

    /// <summary>What each game's file holds, in the test's eyes: Doom's names for the Freedooms, Heretic's for Heretic.</summary>
    private static IReadOnlySet<string> NamesOf(string path) => path.Contains("heretic", StringComparison.Ordinal)
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GRSTNPB", "FLOOR03", "FLAT520", "DOORWOOD" }
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "STARTAN3", "FLOOR4_8", "CEIL3_5", "DOOR3", "SW1EXIT" };

    /// <summary>A pack holding one UDMF map: the starter room of <paramref name="engine"/>, under <paramref name="slot"/>, its walls showing <paramref name="wall"/> and its floor and ceiling <paramref name="flat"/> when given.</summary>
    private static byte[] Pack(string slot, string engine, string? wall = null, string? ns = null, string? flat = null, params (string Name, byte[] Data)[] extra)
    {
        var model = StarterRoom.Create(slot, engine);
        if (ns is not null) model.Namespace = ns;
        foreach (var side in model.Sides) side.Middle = wall ?? side.Middle;
        foreach (var sector in model.Sectors)
        {
            sector.FloorTexture = flat ?? sector.FloorTexture;
            sector.CeilingTexture = flat ?? sector.CeilingTexture;
        }

        var text = System.Text.Encoding.UTF8.GetBytes(UdmfText.WriteString(MapConversion.ToUdmf(model)));
        List<(string, ReadOnlyMemory<byte>)> lumps = [(slot, ReadOnlyMemory<byte>.Empty), ("TEXTMAP", text), ("ENDMAP", ReadOnlyMemory<byte>.Empty)];
        lumps.AddRange(extra.Select(e => (e.Name, (ReadOnlyMemory<byte>)e.Data)));
        return WadWriter.Write(WadKind.Pwad, lumps);
    }

    private static MapFile Loose(TempFolder temp, string name, byte[] bytes, params string[] maps)
    {
        var path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, bytes);
        return new MapFile(path, null, maps);
    }

    [Fact]
    public void APackFitsTheGamesItsNamesNamespaceAndTexturesSay()
    {
        using var temp = new TempFolder();
        string[] Fits(MapFile pack) => [.. MapPacks.GamesFor(pack, Games, NamesOf).Select(g => g.Game.Id)];

        // MAP01 with Doom's textures: the MAPxx game. E1M1 with them: the ExMy Doom game, not Heretic, which has none of them.
        Assert.Equal(["freedoom2"], Fits(Loose(temp, "a.wad", Pack("MAP01", "doom"), "MAP01")));
        Assert.Equal(["freedoom1"], Fits(Loose(temp, "b.wad", Pack("E1M1", "doom"), "E1M1")));

        // E1M1 with Heretic's textures is Heretic's; one that says so in its namespace is Heretic's whatever it shows.
        Assert.Equal(["heretic"], Fits(Loose(temp, "c.wad", Pack("E1M1", "heretic"), "E1M1")));
        Assert.Equal(["heretic"], Fits(Loose(temp, "d.wad", Pack("E1M1", "doom", ns: "heretic"), "E1M1")));

        // A pack whose textures and flats are all its own fits every ExMy game as well as each other: listed under each.
        var own = Pack("E1M1", "doom", wall: "MYWALL", flat: "MYFLAT", extra: [("TX_START", []), ("MYWALL", new byte[8]), ("TX_END", []), ("F_START", []), ("MYFLAT", new byte[4096]), ("F_END", [])]);
        var both = Fits(Loose(temp, "e.wad", own, "E1M1"));
        Assert.Equal(["freedoom1", "heretic"], both.Order());

        // A name of another shape fits nothing; so does a pack for a game that is not here.
        Assert.Empty(Fits(Loose(temp, "f.wad", Pack("MAP01", "hexen", ns: "hexen"), "MAP01")));
        Assert.Empty(MapPacks.Fit([new MapFile(Path.Combine(temp.Path, "gone.wad"), null, ["MAP01"])], Games, NamesOf, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void APackInsideAnArchiveIsReadThereAndTakenOutToEdit()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "packs.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("mine/shreds.wad").Open();
            entry.Write(Pack("MAP03", "doom"));
        }

        var pack = new MapFile(zip, "mine/shreds.wad", ["MAP03"]);
        Assert.Equal(["freedoom2"], MapPacks.GamesFor(pack, Games, NamesOf).Select(g => g.Game.Id));
        var extracted = MapPacks.Materialise(pack, Path.Combine(temp.Path, "out"));
        Assert.Equal(Path.Combine(temp.Path, "out", "packs", "shreds.wad"), extracted);
        Assert.Single(MapLocator.Find(WadContainer.Open(extracted)));
        Assert.Equal(extracted, MapPacks.Materialise(pack, Path.Combine(temp.Path, "out")));
    }

    [Fact]
    public void AMapFromAPackBecomesAProjectThatLoadsThePackInTheEditorAndInPlay()
    {
        var iwad = GameFiles.Require("freedoom2.wad");
        var engine = GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var packPath = Path.Combine(temp.Path, "shreds.wad");
        File.WriteAllBytes(packPath, Pack("MAP03", "doom", wall: "MYWALL", extra: [("TX_START", []), ("MYWALL", new byte[8]), ("TX_END", [])]));
        var before = File.ReadAllBytes(packPath);

        var notes = new List<string>();
        var project = Remix.CreateFromPack(Path.Combine(temp.Path, "project"), packPath, "MAP03", Freedoom2.Game, engine, notes);
        Assert.Equal(before, File.ReadAllBytes(packPath));
        Assert.Equal(("freedoom2", "shreds MAP03", "import", "MAP03"), (project.Info.Game, project.Info.Name, project.Info.Maps[0].Origin?.Kind, project.Info.Maps[0].Origin?.Map));
        Assert.Equal([packPath], project.Info.AddOns);
        Assert.Equal(packPath, project.Info.Maps[0].Origin?.File);
        var map = Project.Open(project.Folder).LoadMap("MAP03");
        Assert.Equal((1, 4, "MYWALL"), (map.Sectors.Count, map.Lines.Count, map.Sides[0].Middle));

        // The editor's resources stack the pack between the game and the project; play loads it before the project.
        var games = new[] { new FoundGame(Freedoom2.Game, iwad, new FileInfo(iwad).Length) };
        var resources = GameResources.Load("freedoom2", project.Folder, games, engine, project.Info.AddOns);
        Assert.NotNull(resources);
        Assert.Equal([iwad, packPath], resources.Files);
        var (request, why) = PlayRequests.For(Project.Open(project.Folder), "MAP03", games, "/c", "/l", 3);
        Assert.Null(why);
        Assert.Equal([packPath, Path.GetFullPath(project.Folder)], request!.Files);
    }
}
