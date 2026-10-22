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
        Assert.Empty(MapPacks.Fit([new MapFile(Path.Combine(temp.Path, "gone.wad"), null, ["MAP01"])], Games, NamesOf, cancel: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ABigCollectionIsReadOnceAndRememberedUntilAFileOrTheGamesChange()
    {
        using var temp = new TempFolder();
        MapFile[] packs = [Loose(temp, "a.wad", Pack("MAP01", "doom"), "MAP01"), Loose(temp, "b.wad", Pack("E1M1", "heretic"), "E1M1")];
        var cache = PackCache.Empty();
        var first = MapPacks.Fit(packs, Games, NamesOf, cache, TestContext.Current.CancellationToken);
        Assert.Equal(["freedoom2", "heretic"], first.Select(f => f.Games.Single().Game.Id));
        Assert.Equal(0, cache.Hits);

        // The second scan takes both from the cache, saved and loaded again in between.
        var path = Path.Combine(temp.Path, "pack-scan.json");
        cache.Save(path);
        var loaded = PackCache.Load(path);
        var second = MapPacks.Fit(packs, Games, NamesOf, loaded, TestContext.Current.CancellationToken);
        Assert.Equal(first.Select(f => f.Games.Single().Game.Id), second.Select(f => f.Games.Single().Game.Id));
        Assert.Equal(2, loaded.Hits);

        // A file written again is read again; so is every pack when the games it is judged among change.
        File.WriteAllBytes(packs[0].Path, Pack("MAP01", "doom", wall: "MYWALL", flat: "MYFLAT", extra: [("TX_START", []), ("MYWALL", new byte[8]), ("TX_END", []), ("F_START", []), ("MYFLAT", new byte[4096]), ("F_END", [])]));
        File.SetLastWriteTimeUtc(packs[0].Path, DateTime.UtcNow.AddMinutes(1));
        var third = MapPacks.Fit(packs, Games, NamesOf, loaded, TestContext.Current.CancellationToken);
        Assert.Equal(3, loaded.Hits);
        Assert.Equal(["freedoom2", "heretic"], third.Select(f => f.Games.Single().Game.Id));
        var fewer = MapPacks.Fit(packs, [Freedoom1, Heretic], NamesOf, loaded, TestContext.Current.CancellationToken);
        Assert.Equal(3, loaded.Hits);
        Assert.Equal(["heretic"], fewer.Select(f => f.Games.Single().Game.Id));
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

    [Fact]
    public void ASetsCardShowsThePacksTitleScreenElseItsFirstMapDrawnAndIsMadeOnce()
    {
        using var temp = new TempFolder();
        var iwad = GameFiles.Require("freedoom2.wad");
        var title = new WadSmith.Core.Graphics.RgbaImage(32, 20);
        for (var i = 3; i < title.Pixels.Length; i += 4) title.Pixels[i] = 255;
        var titled = Path.Combine(temp.Path, "titled.wad");
        File.WriteAllBytes(titled, Pack("MAP01", "doom", extra: ("TITLEPIC", WadSmith.Core.Graphics.ImageCodec.EncodePng(title))));
        var plain = Path.Combine(temp.Path, "plain.wad");
        File.WriteAllBytes(plain, Pack("MAP02", "doom"));
        var folder = Path.Combine(temp.Path, "pictures");

        var first = SetPictures.For([new MapFile(titled, null, ["MAP01"])], iwad, "doom", folder);
        var drawn = SetPictures.For([new MapFile(plain, null, ["MAP02"])], iwad, "doom", folder);
        // A collection: the first of its packs with a title screen gives the picture, whatever order they come in.
        var both = SetPictures.For([new MapFile(plain, null, ["MAP02"]), new MapFile(titled, null, ["MAP01"])], iwad, "doom", folder);
        Assert.Equal((true, false, true), (first!.TitleScreen, drawn!.TitleScreen, both!.TitleScreen));
        // A pack whose only map is empty (a sound pack with a stand-in MAP01) has none: its card shows the plain icon.
        var empty = Path.Combine(temp.Path, "sounds.wad");
        File.WriteAllBytes(empty, WadWriter.Write(WadKind.Pwad, [("MAP01", ReadOnlyMemory<byte>.Empty), ("TEXTMAP", System.Text.Encoding.UTF8.GetBytes("namespace = \"zdoom\";")), ("ENDMAP", ReadOnlyMemory<byte>.Empty)]));
        Assert.Null(SetPictures.For([new MapFile(empty, null, ["MAP01"])], iwad, "doom", folder));
        Assert.Equal((32, 20), Size(first.File));
        Assert.Equal((320, 320), Size(drawn.File));

        // Asked again while the pack is the same, it comes from the folder as it was made.
        var made = File.GetLastWriteTimeUtc(drawn.File);
        var again = SetPictures.For([new MapFile(plain, null, ["MAP02"])], iwad, "doom", folder);
        Assert.Equal((drawn.File, made), (again!.File, File.GetLastWriteTimeUtc(again.File)));

        static (int, int) Size(string png)
        {
            var image = WadSmith.Core.Graphics.ImageCodec.Decode(File.ReadAllBytes(png));
            return (image.Width, image.Height);
        }
    }
}
