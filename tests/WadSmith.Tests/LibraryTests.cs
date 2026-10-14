using System.IO.Compression;
using System.Net;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Library;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>
/// The free sources (D61): Realm667's feeds, the idgames API and its readmes, the Doom Wiki's pack pages, the store that keeps
/// lists and downloads, the reading of a download's actors, and an object brought whole into a project. Every answer here is
/// made up in the shape the sites send; nothing is fetched.
/// </summary>
public sealed class LibraryTests
{
    private sealed class Fake(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string Entry(string title, string slug, string get, string info, string credits, string picture = "/images/content/repository/propstop/Crate.png") => $"""
        <item>
            <title>{title}</title>
            <link>https://www.realm667.com/repository/prop-stop/technical/{slug}</link>
            <description><![CDATA[<!-- START: Tabs --><div><div role="button"><p>Preview</p></div><div><p><img src="{picture}" alt="" /></p></div>
            <div role="button"><p>Info</p></div><div><p>{info.Replace("\n", "<br />", StringComparison.Ordinal)}</p></div>
            <div role="button"><p>Credits</p></div><div><p>{credits.Replace("\n", "<br />", StringComparison.Ordinal)}</p></div>
            <div role="button"><p>Description</p></div><div><p>A crate that &amp; waits.</p></div>
            <div role="button"><p>Download</p></div><div>{(get.Length > 0 ? $"<a href=\"https://www.realm667.com//get/prop-stop/{get}\">{title} ( 1.5 KB )</a>" : "")}</div></div>]]></description>
            <category>Technical</category>
        </item>
        """;

    private static string Feed(params string[] items) => $"""<?xml version="1.0" encoding="utf-8"?><rss version="2.0"><channel><title>Technical</title>{string.Concat(items)}</channel></rss>""";

    private static readonly Realm667.Category Technical = Realm667.Categories.First(c => c.Path == "prop-stop/technical");

    [Fact]
    public void ARealm667FeedIsReadIntoEntries()
    {
        var feed = Feed(
            Entry("Test Crate", "901-test-crate", "77-test-crate", "Name: Test Crate\nPalette: Doom\nSummon: TestCrate, TestCrateOpen\nDestroyable: No", "Submitted: Someone\nSprites: Someone Else"),
            Entry("No Download", "902-no-download", "", "Name: Nothing", ""),
            Entry("Heretic Lamp", "903-lamp", "78-lamp", "Name: Lamp\nPalette: Heretic\nSummon: TestLamp", "Submitted: Another"));
        var items = Realm667.Parse(feed, Technical);

        // The entry without a download is left out; the others keep what their page says.
        Assert.Equal(["r667-901", "r667-903"], items.Select(i => i.Id));
        var crate = items[0];
        Assert.Equal(("Test Crate", "Decorations", LibraryKind.Object), (crate.Title, crate.Group, crate.Kind));
        Assert.Equal("https://www.realm667.com/get/prop-stop/77-test-crate", crate.Download.ToString());
        Assert.Equal("https://www.realm667.com/images/content/repository/propstop/Crate.png", crate.Preview!.ToString());
        Assert.Equal(["TestCrate", "TestCrateOpen"], crate.Summons);
        Assert.Equal("Someone", crate.Field("Submitted") is "" ? crate.Credits.Split('\n')[0]["Submitted: ".Length..] : crate.Field("Submitted"));
        Assert.Contains("Sprites: Someone Else", crate.Credits, StringComparison.Ordinal);
        Assert.Equal("A crate that & waits.", crate.Description);
        Assert.Equal(1536, crate.Size);
        Assert.Equal(("Doom", "doom"), (crate.Style, crate.Family));

        // The palette line says which colours the pictures are in.
        Assert.Equal(("Heretic/Hexen", "heretic"), (items[1].Style, items[1].Family));
    }

    [Fact]
    public async Task AFeedIsReadPageAfterPageUntilAPageBringsNothingNew()
    {
        var first = Feed([.. Enumerable.Range(1, Realm667.PageSize).Select(n => Entry($"Thing {n}", $"{n}-thing", $"{n}-thing", "Name: x", ""))]);
        var second = Feed(Entry("Last", "999-last", "999-last", "Name: last", ""));
        var fake = new Fake(uri => Text(uri.Query.Contains("start=60", StringComparison.Ordinal) ? second : uri.Query.Contains("start=", StringComparison.Ordinal) ? Feed() : first));
        using var http = new HttpClient(fake);
        var items = await Realm667.FetchAsync(http, Technical, null, TestContext.Current.CancellationToken);
        Assert.Equal(Realm667.PageSize + 1, items.Count);
        Assert.Equal(2, fake.Asked.Count);
        Assert.EndsWith("&start=60", fake.Asked[1], StringComparison.Ordinal);
    }

    [Fact]
    public void IdgamesAnswersAndReadmePermissionsAreRead()
    {
        const string one = """{"content":{"id":42,"title":"Test Textures","dir":"graphics/","filename":"testtex.zip","size":12345,"author":"A. Mapper","description":"Walls.","credits":"Friends","textfile":"Title : Test\n\n* Copyright / Permissions *\n\nAuthors MAY use the contents of this file as a base for\nmodification or reuse.\n\n* Where to get the file *\n\nidgames"},"meta":{"version":3}}""";
        var file = Idgames.ParseFile(one)!;
        Assert.Equal((42, "graphics/testtex.zip", 12345L), (file.Id, file.Path, file.Size));
        Assert.Equal("https://mirrors.lug.mtu.edu/idgames/graphics/testtex.zip", file.Downloads[0].ToString());
        Assert.Equal("Authors MAY use the contents of this file as a base for modification or reuse.", Idgames.Permissions(file.TextFile));

        // A search of one answer is an object, of none a warning.
        Assert.Single(Idgames.ParseSearch("""{"content":{"file":{"id":1,"title":"x","dir":"graphics/","filename":"x.zip","size":1}}}"""));
        Assert.Equal(2, Idgames.ParseSearch("""{"content":{"file":[{"id":1,"dir":"a/","filename":"a.zip"},{"id":2,"dir":"b/","filename":"b.zip"}]}}""").Count);
        Assert.Empty(Idgames.ParseSearch("""{"warning":{"type":"No Results","message":"No files returned."},"meta":{"version":3}}"""));
        Assert.Null(Idgames.ParseFile("""{"error":{"type":"Bad id"}}"""));

        // A readme without the template's heading: the paragraph that speaks of permission.
        Assert.Equal("You may use these textures freely in your levels.", Idgames.Permissions("Some words.\n\nYou may use these\ntextures freely in your levels.\n\nMore."));
    }

    [Fact]
    public void ADoomWikiPagePointsAtItsTextureFile()
    {
        Assert.Equal(["Test Pack", "Test Megawad"], DoomWiki.ParseCategory("""{"query":{"categorymembers":[{"ns":0,"title":"Texture pack"},{"ns":0,"title":"Test Pack"},{"ns":0,"title":"Test Megawad"}]}}"""));
        const string page = """{"parse":{"title":"Test Megawad","wikitext":{"*":"{{Infobox wad\n|screenshot=Test shot.png\n}}\n'''Test Megawad''' is a [[megawad]] with new [[texture]]s.\n== Files ==\n* {{idgames|id=100|title=Test Megawad}}\n* {{idgames|id=101|title=Test Megawad Textures}}\n== Permissions ==\nThe textures may be used freely."}}}""";
        var pack = DoomWiki.ParsePage("Test Megawad", page)!;
        Assert.Equal((101, (string?)null), (pack.IdgamesId, pack.IdgamesPath));
        Assert.Equal("Test Megawad is a megawad with new textures.", pack.Intro);
        Assert.Equal("The textures may be used freely.", pack.Permissions);

        var byPath = DoomWiki.ParsePage("Test", """{"parse":{"wikitext":{"*":"x {{idgames|file=themes/test/testres|title=Test resources}}"}}}""")!;
        Assert.Equal("themes/test/testres.zip", byPath.IdgamesPath);
        Assert.Null(DoomWiki.ParsePage("None", """{"parse":{"wikitext":{"*":"no links"}}}"""));
    }

    [Fact]
    public void ADownloadsActorsAreReadAndRewrittenToBePlacedNotToReplace()
    {
        const string decorate = """
            // A crate.
            ACTOR TestCrate : SolidThing replaces ExplosiveBarrel 12345
            {
              Radius 12
              Height 24
              +SOLID
              States
              {
              Spawn:
                TNT1 A 0
                CRT1 A -1 /* still */
                Stop
              }
            }
            actor TestSpark { Projectile States { Spawn: SPRK AB 4 Loop } }
            #include "actors/more.txt"
            #include ACTORS/BARE
            """;
        var read = ActorScripts.Read(decorate, zscript: false);
        Assert.Equal(["TestCrate", "TestSpark"], read.Select(d => d.ClassName));
        var crate = read[0];
        Assert.Equal(("SolidThing", "ExplosiveBarrel", 12345, 12.0, 24.0, "CRT1", 'A', false), (crate.Parent, crate.Replaces, crate.DoomEdNum, crate.Radius, crate.Height, crate.Sprite, crate.Frame, crate.Missile));
        Assert.True(read[1].Missile);

        Assert.Equal(["actors/more.txt", "ACTORS/BARE"], ActorScripts.Includes(decorate));
        var (text, notes) = ActorScripts.Rewrite(decorate, false, new Dictionary<string, string> { ["CRT1"] = "WL00" }, p => p switch { "actors/more.txt" => "library/r667-1/actors/more.txt", "ACTORS/BARE" => "library/r667-1/ACTORS/BARE", _ => null });
        Assert.DoesNotContain("replaces", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345", text, StringComparison.Ordinal);
        Assert.Contains("WL00 A -1", text, StringComparison.Ordinal);
        Assert.Contains("#include \"library/r667-1/actors/more.txt\"", text, StringComparison.Ordinal);
        Assert.Contains("#include \"library/r667-1/ACTORS/BARE\"", text, StringComparison.Ordinal);
        Assert.Single(notes);

        const string zscript = """
            version "4.10"
            class TestLamp : Actor replaces TechLamp
            {
                Default { Radius 8; Height 40; Scale 0.5; +SOLID; }
                States { Spawn: LMP1 ABC 4 Bright; Loop; }
            }
            """;
        var lamp = ActorScripts.Read(zscript, zscript: true).Single();
        Assert.Equal(("TestLamp", "Actor", "TechLamp", 0.5, "LMP1"), (lamp.ClassName, lamp.Parent, lamp.Replaces, lamp.Scale, lamp.Sprite));
        Assert.DoesNotContain("replaces", ActorScripts.Rewrite(zscript, true, new Dictionary<string, string>(), _ => null).Text, StringComparison.Ordinal);
        Assert.Equal("frame WL01A", ActorScripts.RenameInDefinitions("frame LMP1A", new Dictionary<string, string> { ["LMP1"] = "WL01" }));
    }

    /// <summary>A WAD shaped like a Realm667 download: a sprite, a DECORATE, a SNDINFO and its sound, its credits.</summary>
    private static byte[] Download(string decorate, string sprite = "CRT1A0", byte[]? palette = null)
    {
        var picture = new PalettedImage(4, 6) { OffsetX = 2, OffsetY = 6 };
        for (var i = 0; i < picture.Indices.Length; i++)
        {
            picture.Indices[i] = (byte)(100 + i);
            picture.Opaque[i] = true;
        }

        byte[] sound = [3, 0, 0x11, 0x2B, 4, 0, 0, 0, 128, 128, 128, 128];
        return WadWriter.Write(WadKind.Pwad,
        [
            ("SS_START", ReadOnlyMemory<byte>.Empty), (sprite, DoomPicture.Encode(picture)), ("SS_END", ReadOnlyMemory<byte>.Empty),
            ("DECORATE", Encoding.UTF8.GetBytes(decorate)), ("SNDINFO", "crate/hit DSCRATE\n"u8.ToArray()), ("DSCRATE", sound),
            ("CREDITS", "Made by Someone for testing."u8.ToArray()),
            .. palette is null ? Array.Empty<(string, ReadOnlyMemory<byte>)>() : [("PLAYPAL", (ReadOnlyMemory<byte>)palette)],
        ]);
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }

        return memory.ToArray();
    }

    private static LibraryItem Item(string id, string title, string summon, string family = "doom", LibraryKind kind = LibraryKind.Object) =>
        new("realm667", id, title, kind, kind == LibraryKind.Object ? "Decorations" : "Texture packs", family == "doom" ? "Doom" : "Heretic/Hexen", family, null, new Uri($"https://www.realm667.com/get/prop-stop/{id}"), new Uri($"https://www.realm667.com/repository/prop-stop/technical/{id}"), "Submitted: Someone", "", $"Summon: {summon}");

    [Fact]
    public async Task TheStoreKeepsListsAndDownloadsAndUnpacksWhatIsInside()
    {
        using var temp = new TempFolder();
        var crate = Item("r667-901", "Test Crate", "TestCrate");
        var feed = Feed(Entry("Test Crate", "901-test-crate", "77-test-crate", "Name: Test Crate", ""));
        var fake = new Fake(uri => uri.Host == "www.realm667.com" && uri.AbsolutePath.StartsWith("/get/", StringComparison.Ordinal) // not a path
            ? Bytes(Zip(("readme.txt", "words"u8.ToArray()), ("Crate.wad", Download("actor TestCrate { States { Spawn: CRT1 A -1 Stop } }"))))
            : Text(feed));
        using var http = new HttpClient(fake);
        var store = new LibraryStore(temp.Path, http);

        // A list is kept: the second asking asks nothing.
        Assert.Single(await store.Realm667Async(Technical, TestContext.Current.CancellationToken));
        Assert.Single(await store.Realm667Async(Technical, TestContext.Current.CancellationToken));
        Assert.Single(fake.Asked);

        // A download is unpacked to the WAD inside it, once.
        Assert.Null(store.Fetched(crate));
        var files = await store.FetchAsync(crate, null, TestContext.Current.CancellationToken);
        Assert.Equal(["Crate.wad"], files.Select(Path.GetFileName));
        Assert.Equal(files, store.Fetched(crate));
        Assert.Equal(files, await store.FetchAsync(crate, null, TestContext.Current.CancellationToken));
        Assert.Equal(2, fake.Asked.Count);

        // Offline: the kept list and files, nothing asked.
        var offline = new LibraryStore(temp.Path, null);
        Assert.Single(await offline.Realm667Async(Technical, TestContext.Current.CancellationToken));
        Assert.Equal(files, offline.Fetched(crate));
        Assert.False(offline.Online);
    }

    [Fact]
    public void AZipIsUnpackedToItsResourcesAPackPrefersItsTextureFilesAndALaidOutZipIsAPk3()
    {
        using var temp = new TempFolder();
        var wad = WadWriter.Write(WadKind.Pwad, [("TEXTURES", (ReadOnlyMemory<byte>)"texture X, 64, 64 {}"u8.ToArray())]);

        // A texture pack shipped inside a megawad's zip, beside the maps: only the texture WAD.
        var megawad = temp.File("mega.zip");
        File.WriteAllBytes(megawad, Zip(("maps.wad", wad), ("megatex.zip", Zip(("megatex.wad", wad), ("shot.png", [1, 2, 3])))));
        var dir = Path.Combine(temp.Path, "mega");
        Directory.CreateDirectory(dir);
        var pack = Item("idgames-1", "Mega", "", kind: LibraryKind.TexturePack);
        Assert.Equal(["megatex.wad"], LibraryStore.Unpack(megawad, dir, pack).Select(Path.GetFileName));

        // A zip laid out as a PK3 is kept as one.
        var laid = temp.File("laid.zip");
        File.WriteAllBytes(laid, Zip(("DECORATE.txt", "actor X {}"u8.ToArray()), ("sprites/XXXXA0.png", [1])));
        var laidDir = Path.Combine(temp.Path, "laid");
        Directory.CreateDirectory(laidDir);
        Assert.Equal(["r667-2.pk3"], LibraryStore.Unpack(laid, laidDir, Item("r667-2", "Laid", "X")).Select(Path.GetFileName));

        // Something that is neither: refused in words.
        var junk = temp.File("junk.bin");
        File.WriteAllBytes(junk, "Rar!...."u8.ToArray());
        Assert.Throws<CorruptDataException>(() => LibraryStore.Unpack(junk, laidDir, Item("r667-3", "Junk", "")));
    }

    /// <summary>The game's own PLAYPAL: a download that ships its game's palette reads in the same colours, and the palette is never copied.</summary>
    private static byte[] Doom(GameResources game) => game.Find("PLAYPAL", ResourceNamespace.Global)!.Read();

    private static (Project Project, GameResources Game) Freedoom(TempFolder temp)
    {
        var path = GameFiles.Require("freedoom2.wad");
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "freedoom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, path, new FileInfo(path).Length)], null)!;
        return (project, game);
    }

    [Fact]
    public void AnObjectIsBroughtWholeIntoAProjectNumberedAndCredited()
    {
        using var temp = new TempFolder();
        var (project, game) = Freedoom(temp);
        var wad = temp.File("dl/Crate.wad");
        File.WriteAllBytes(wad, Download("""
            ACTOR TestCrate replaces ExplosiveBarrel 12345
            {
              Radius 12
              Height 24
              +SOLID
              States { Spawn: CRT1 A -1 Stop }
            }
            ACTOR TestCrateSpark { Projectile States { Spawn: CRT1 A 4 Stop } }
            """, palette: Doom(game)));
        var item = Item("r667-901", "Test Crate", "TestCrate");

        var brought = LibraryObjects.Bring(project, game, item, [wad], _ => null);
        Assert.True(brought.New);
        Assert.Equal(("TestCrate", "Test Crate", ActorBorrowing.FirstNumber), (brought.Main.Thing.ClassName, brought.Main.Thing.Name, brought.Main.Number));
        Assert.Single(brought.Actors);

        // The definition lumps as root files of their own, rewritten; the pictures and sounds under the entry's folder; the credits.
        var decorate = File.ReadAllText(Path.Combine(project.Folder, "DECORATE.r667-901"));
        Assert.DoesNotContain("replaces", decorate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345", decorate, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(project.Folder, "SNDINFO.r667-901")));
        Assert.True(File.Exists(Path.Combine(project.Folder, "sprites", "r667-901", "CRT1A0.lmp")));
        Assert.True(File.Exists(Path.Combine(project.Folder, "sounds", "r667-901", "DSCRATE.lmp")));
        Assert.False(File.Exists(Path.Combine(project.Folder, "PLAYPAL.r667-901")));
        var credits = File.ReadAllText(Path.Combine(project.Folder, "credits", "r667-901.txt"));
        Assert.Contains("Made by Someone for testing.", credits, StringComparison.Ordinal);
        Assert.Contains("https://www.realm667.com/repository/prop-stop/technical/r667-901", credits, StringComparison.Ordinal);
        Assert.Contains(brought.Notes, n => n.Contains("ExplosiveBarrel", StringComparison.Ordinal));

        // The game knows it at once and shows it; the project's MAPINFO numbers it; opened again it is still there.
        Assert.Equal("TestCrate", game.Things.Find(brought.Main.Number)?.ClassName);
        Assert.True(game.CanShow(brought.Main.Number));
        Assert.NotNull(game.SpriteFor(brought.Main.Number));
        ProjectMapInfo.Refresh(project, game.Game, game.Files, null);
        Assert.Contains($"{brought.Main.Number} = TestCrate", File.ReadAllText(Path.Combine(project.Folder, ProjectMapInfo.FileName)), StringComparison.Ordinal);
        var again = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, game.Files[0], 0)], null)!;
        Assert.Equal(("TestCrate", 12.0, 24.0), (again.Things.Find(brought.Main.Number)!.ClassName, again.Things.Find(brought.Main.Number)!.Radius, again.Things.Find(brought.Main.Number)!.Height));

        // Placed again: found, not brought twice.
        var second = LibraryObjects.Bring(Project.Open(project.Folder), game, item, [wad], _ => null);
        Assert.Equal((false, brought.Main.Number), (second.New, second.Main.Number));
        Assert.Single(Project.Open(project.Folder).Info.Copied, c => c.Kind == "library");

        // Another download declaring the same class would stop GZDoom loading: refused in words.
        var clash = Item("r667-902", "Other Crate", "TestCrate");
        Assert.Throws<CorruptDataException>(() => LibraryObjects.Bring(Project.Open(project.Folder), game, clash, [wad], _ => null));
    }

    [Fact]
    public void ASpriteNameTheGameShowsIsRenamedAndPicturesInAnotherFamilysColoursBecomePng()
    {
        using var temp = new TempFolder();
        var (project, game) = Freedoom(temp);
        Assert.NotEmpty(game.Sprites.Frames("POSS"));
        var wad = temp.File("dl/Lamp.wad");
        File.WriteAllBytes(wad, Download("actor TestHereticLamp { Radius 8 Height 40 States { Spawn: POSS A -1 Stop } }", "POSSA0"));
        var heretic = Item("r667-903", "Heretic Lamp", "TestHereticLamp", family: "heretic");
        var colours = new byte[768];
        for (var i = 0; i < 256; i++) colours[i * 3] = (byte)i;

        var brought = LibraryObjects.Bring(project, game, heretic, [wad], family => family == "heretic" ? new Palette(colours) : null);
        var renamed = brought.Main.Thing.Sprite!;
        Assert.NotEqual("POSS", renamed);
        Assert.StartsWith("WL", renamed, StringComparison.Ordinal);
        Assert.Contains($"{renamed} A -1", File.ReadAllText(Path.Combine(project.Folder, "DECORATE.r667-903")), StringComparison.Ordinal);
        var png = Path.Combine(project.Folder, "sprites", "r667-903", renamed + "A0.png");
        Assert.True(File.Exists(png));
        Assert.Equal((2, 6), ImageCodec.ReadGrab(File.ReadAllBytes(png)));
        Assert.Contains(brought.Notes, n => n.Contains("POSS", StringComparison.Ordinal));

        // The game's own zombieman keeps its own pictures; the renamed frames are the project's.
        Assert.All(game.Sprites.Frames("POSS"), f => Assert.False(game.IsProjectFile(f.Entry)));
        Assert.All(game.Sprites.Frames(renamed), f => Assert.True(game.IsProjectFile(f.Entry)));
    }
}
