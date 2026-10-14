using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>Map settings: the engine's MAPINFO read as it reads it, a project's written from the settings, a remix's switches found.</summary>
public sealed class MapInfoTests
{
    private static MapDefinitions DoomII()
    {
        var engine = ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"));
        var maps = new Dictionary<string, MapDefinition>(LumpName.Comparer);
        string? Include(string path) => engine.FindPath(path) is { } e ? System.Text.Encoding.Latin1.GetString(e.Read()) : null;
        var old = MapInfo.Parse(Include("mapinfo/doom2.txt")!, "mapinfo/doom2.txt", Include, maps, []);
        return new MapDefinitions(maps, old, new Dictionary<string, string> { ["MUSIC_RUNNIN"] = "D_RUNNIN" });
    }

    [Fact]
    public void TheEnginesDoomIIDefinitionsReadAsTheEngineReadsThem()
    {
        var d = DoomII();
        Assert.False(d.OldFormat);
        Assert.Equal(41, d.Maps.Count); // MAP01–MAP32 and the nine LEVEL01–LEVEL09 of No Rest for the Living
        var map01 = d.For("MAP01")!;
        Assert.Equal("lookup \"HUSTR_1\"", map01.Header);
        Assert.Equal("MAP02", map01.Text("next"));
        Assert.Equal("SKY1", map01.Text("sky1"));
        Assert.Equal("$MUSIC_RUNNIN", map01.Text("music"));
        Assert.Equal("D_RUNNIN", d.Resolve(map01.Text("music")));
        Assert.Equal("30", map01.Text("par"));

        // defaultmap's line comes first in every map's block; MAP06 says it again; MAP07 has a bare flag.
        Assert.Equal("sucktime = 1", map01.Properties[0]);
        Assert.Equal(2, d.For("MAP06")!.Properties.Count(p => MapDefinition.KeyOf(p) == "sucktime"));
        Assert.Contains("map07special", d.For("MAP07")!.Properties);
        Assert.Equal("EndGameC", d.For("MAP30")!.Text("next"));
        Assert.Equal(["MAP01", "MAP02", "MAP03"], d.Slots.Take(3));
        Assert.Equal("MAP32", d.Slots[31]);
        Assert.Equal(["LEVEL01", "LEVEL02"], d.Slots.Skip(32).Take(2));
    }

    [Fact]
    public void TheEnginesUntranslatedStringsReadFromLanguageDef()
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MUSIC_E1M1"] = "kept" };
        MapInfo.ReadLanguageDef("[default]\n\n// Music names for Doom.\nMUSIC_E1M1 = \"e1m1\";\nMUSIC_RUNNIN = \"runnin\";\nbad line\n", strings);
        Assert.Equal("kept", strings["MUSIC_E1M1"]);
        Assert.Equal("runnin", strings["music_runnin"]);
        Assert.Equal(2, strings.Count);

        // The engine's own file names Doom II's music.
        var engine = ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"));
        var real = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MapInfo.ReadLanguageDef(System.Text.Encoding.Latin1.GetString(engine.FindPath("language.def")!.Read()), real);
        Assert.Equal("runnin", real["MUSIC_RUNNIN"]);
    }

    [Fact]
    public void TheOldFormatIsReadAsTheBracesFormatSaysIt()
    {
        // Hexen's MAPINFO (Phase 9): maps by number, a property a line, next by warp number; braces maps beside them as ever.
        var maps = new Dictionary<string, MapDefinition>(LumpName.Comparer);
        var old = MapInfo.Parse("map 1 \"WINNOWING HALL\"\nwarptrans 1\nnext 2\ncluster 1\nsky1 SKY2 0\nsky2 SKY3 0\nlightning\ncdtrack 13\n\nmap 2 \"SEVEN PORTALS\"\nwarptrans 2\nnext 9\ncluster 1\nsky1 SKY4 0\nmap MAP03 \"Third\" { next = \"MAP04\" }\n", "MAPINFO", _ => null, maps, []);
        Assert.True(old);
        MapInfo.ResolveWarps(maps);
        Assert.Equal(["MAP01", "MAP02", "MAP03"], maps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("\"WINNOWING HALL\"", maps["MAP01"].Header);
        Assert.Equal(["levelnum = 1", "warptrans = 1", "next = \"MAP02\"", "cluster = 1", "sky1 = \"SKY2\", 0", "sky2 = \"SKY3\", 0", "lightning", "cdtrack = 13"], maps["MAP01"].Properties);
        Assert.Equal("&wt@09", maps["MAP02"].Text("next"));
        Assert.Equal("MAP04", maps["MAP03"].Text("next"));
    }

    [Fact]
    public void AHexenMapsSettingsKeepItsHubSkyAndNumber()
    {
        // A project block for Hexen's first map with a title of its own: the rest of HEXEN.WAD's definition stays, its number
        // (which Hexen's exits, scripts and music go by), its hub, its skies and lightning.
        var game = GameCatalog.Get("hexen")!;
        var definitions = MapInfo.For(game, [ResourceContainer.Open(GameFiles.Require("HEXEN.WAD"))], GameFiles.Require("gzdoom.pk3"));
        Assert.True(definitions.OldFormat);
        var block = ProjectMapInfo.Block(new ProjectMap { Slot = "MAP01", File = "maps/MAP01.wad", Title = "My hall" }, definitions.For("MAP01"), []);
        foreach (var line in new[] { "map MAP01 \"My hall\"", "levelnum = 1", "cluster = 1", "next = \"MAP02\"", "sky1 = \"SKY2\", 0", "lightning" }) Assert.Contains(line, block, StringComparison.Ordinal);
        // Every one of its maps read, each next named by its map (no warp number left), the second hub's first map in hub 2.
        Assert.True(definitions.Maps.Count(m => m.Value.Text("levelnum") is not null) >= 49);
        Assert.DoesNotContain(definitions.Maps.Values.SelectMany(m => m.Properties), p => p.Contains("&wt@", StringComparison.Ordinal));
        Assert.Equal(("2", "\"SHADOW WOOD\""), (definitions.For("MAP13")!.Text("cluster"), definitions.For("MAP13")!.Header));
    }

    [Fact]
    public void SlotsSortByTheirNumbers()
    {
        var maps = new[] { "E1M10", "E1M9", "E2M1", "E1M1" }.ToDictionary(s => s, s => new MapDefinition(s, "", []), LumpName.Comparer);
        Assert.Equal(["E1M1", "E1M9", "E1M10", "E2M1"], new MapDefinitions(maps, false, new Dictionary<string, string>()).Slots);
    }

    [Fact]
    public void ATitledMapKeepsTheRestOfTheGamesDefinition()
    {
        var d = DoomII();
        var block = ProjectMapInfo.Block(new ProjectMap { Slot = "MAP01", File = "maps/MAP01.wad", Title = "Gateway" }, d.For("MAP01"), []);
        Assert.StartsWith("map MAP01 \"Gateway\"\n{\n", block, StringComparison.Ordinal);
        Assert.Contains("\tnext = \"MAP02\"\n", block, StringComparison.Ordinal);
        Assert.Contains("\tsky1 = \"SKY1\"\n", block, StringComparison.Ordinal);
        Assert.Contains("\tmusic = \"$MUSIC_RUNNIN\"\n", block, StringComparison.Ordinal);
        Assert.Contains("\tpar = 30\n", block, StringComparison.Ordinal);
        Assert.DoesNotContain("titlepatch", block, StringComparison.Ordinal);
        Assert.DoesNotContain("lookup", block, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndSwitchesReplaceTheGamesLinesOnly()
    {
        var d = DoomII();
        var block = ProjectMapInfo.Block(new ProjectMap { Slot = "MAP14", File = "maps/MAP14.wad", Music = "D_RUNNIN", Sky = "SKY3", Next = ProjectMapInfo.EndToTitle }, d.For("MAP14"), ["light", "pointonline", "shorttex"]);
        Assert.StartsWith("map MAP14 lookup \"HUSTR_14\"\n{\n", block, StringComparison.Ordinal);
        Assert.Single(block.Split('\n'), l => l.StartsWith("\tmusic", StringComparison.Ordinal));
        Assert.Contains("\tmusic = \"D_RUNNIN\"\n", block, StringComparison.Ordinal);
        Assert.Contains("\tsky1 = \"SKY3\"\n", block, StringComparison.Ordinal);
        Assert.Contains("\tnext = \"EndTitle\"\n", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MAP15\"\n\tsecret", block, StringComparison.Ordinal);
        Assert.Contains("\tsecretnext = \"MAP15\"\n", block, StringComparison.Ordinal);
        Assert.EndsWith("\tcompat_light = 1\n\tcompat_pointonline = 1\n\tcompat_shorttex = 1\n}\n", block, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToSayMeansNoFileAndAForeignFileIsLeftAlone()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var plain = new ProjectMap { Slot = "MAP01", File = "maps/MAP01.wad" };
        Assert.Null(ProjectMapInfo.Text([plain], MapDefinitions.None, _ => []));
        Assert.NotNull(ProjectMapInfo.Text([plain], MapDefinitions.None, _ => ["shorttex"]));

        var path = Path.Combine(project.Folder, ProjectMapInfo.FileName);
        Assert.True(ProjectMapInfo.Write(project, ProjectMapInfo.Text([plain with { Title = "Gateway" }], MapDefinitions.None, _ => [])));
        Assert.StartsWith(ProjectMapInfo.Mark, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("map MAP01 \"Gateway\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.True(ProjectMapInfo.Write(project, null));
        Assert.False(File.Exists(path));

        File.WriteAllText(path, "map MAP01 \"Theirs\" { }\n");
        Assert.True(ProjectMapInfo.HasForeign(project));
        Assert.False(ProjectMapInfo.Write(project, "map MAP01 \"Ours\" { }\n"));
        Assert.Equal("map MAP01 \"Theirs\" { }\n", File.ReadAllText(path));
    }

    [Fact]
    public void TheIwadEntryIsTheFirstWhoseLumpsTheFileHas()
    {
        const string iwadinfo = """
            IWad
            {
                Name = "Final Doom: Plutonia"
                Compatibility = "Shorttex", "Stairs"
                MustContain = "MAP01", "CAMO1"
            }
            IWad
            {
                Name = "DOOM 2"
                SkipBexStringsIfLanguage
                Compatibility = "Shorttex", "nosectionmerge"
                MustContain = "MAP01"
            }
            """;
        Assert.Equal(["Shorttex", "nosectionmerge"], RemixCompat.Identify(iwadinfo, l => l == "MAP01"));
        Assert.Equal(["Shorttex", "Stairs"], RemixCompat.Identify(iwadinfo, l => l is "MAP01" or "CAMO1"));
        Assert.Empty(RemixCompat.Identify(iwadinfo, _ => false));
    }

    [Fact]
    public void TheSwitchesForAChecksumComeFromItsBlockAlone()
    {
        const string table = """
            // a comment
            1D1E7A6B6B1F2D2E3F4A5B6C7D8E9F00 // somewad.wad map01
            {
                setslopeoverflow
            }

            AAAABBBBCCCCDDDDEEEEFFFF00001111 // doom2.wad map14
            BBBBCCCCDDDDEEEEFFFF000011112222 // another map14
            {
                pointonline // one line
                rebuildnodes
            }
            """;
        Assert.Equal(["pointonline", "rebuildnodes"], RemixCompat.Switches(table, "aaaabbbbccccddddeeeeffff00001111"));
        Assert.Equal(["pointonline", "rebuildnodes"], RemixCompat.Switches(table, "BBBBCCCCDDDDEEEEFFFF000011112222"));
        Assert.Equal(["setslopeoverflow"], RemixCompat.Switches(table, "1D1E7A6B6B1F2D2E3F4A5B6C7D8E9F00"));
        Assert.Empty(RemixCompat.Switches(table, "00000000000000000000000000000000"));
    }

    [Fact]
    public void ClustersSayWhetherTheyAreHubsAsTheEngineReadsThem()
    {
        // A cluster block says whether it is a hub ("hub" as a word, not as a value); a map naming a cluster nothing has defined
        // defines it, a hub only when the map is in Hexen's own format (GZDoom's rule, which makes Hexen's fifth hub one); in
        // the old format a clusterdef's lines run to the next head, and a ";" starts a comment (Hexen's own file opens with
        // "; --- Cluster 1 ---"); a block read again defines its cluster afresh.
        var maps = new Dictionary<string, MapDefinition>(LumpName.Comparer);
        var clusters = new Dictionary<int, bool>();
        MapInfo.Parse("cluster 1 { hub exittext = \"x\" }\ncluster 2\n{\n\tmusic = \"hub\"\n}\nmap MAP03 \"Third\" { cluster = 6 }\n", "ZMAPINFO", _ => null, maps, [], clusters);
        MapInfo.Parse("; --- Cluster 9 ---\n\nmap 4 \"FOURTH\"\ncluster 5 ; the fourth\nmap 7 \"SEVENTH\"\ncluster 6\nclusterdef 7\nhub\nmap 8 \"EIGHTH\"\ncluster 1\n", "MAPINFO", _ => null, maps, [], clusters);
        Assert.Equal([(1, true), (2, false), (5, true), (6, false), (7, true)], clusters.OrderBy(c => c.Key).Select(c => (c.Key, c.Value)));
        Assert.Equal(["MAP04", "MAP07", "MAP08"], maps.Keys.Where(k => k != "MAP03").Order(StringComparer.Ordinal));
        MapInfo.Parse("cluster 5 { exittext = \"y\" }\n", "ZMAPINFO", _ => null, maps, [], clusters);
        Assert.False(clusters[5]);
        var definitions = new MapDefinitions(maps, true, new Dictionary<string, string>(), clusters);
        Assert.Equal([1, 7], definitions.HubClusters.Order());
        Assert.Equal(["MAP03", "MAP07"], definitions.MapsIn(6));
        Assert.Equal(1, definitions.ClusterOf("MAP08"));
    }

    [Fact]
    public void TheGamesOwnHubsAreRead()
    {
        // Hexen's five hubs (the fifth made one by its own MAPINFO's format), Strife's two, none in Doom II.
        var engine = GameFiles.Require("gzdoom.pk3");
        var hexen = MapInfo.For(GameCatalog.Get("hexen")!, [ResourceContainer.Open(GameFiles.Require("HEXEN.WAD"))], engine);
        Assert.Equal([1, 2, 3, 4, 5], hexen.HubClusters.Order());
        Assert.Equal(["MAP08", "MAP09", "MAP10", "MAP11", "MAP12", "MAP13"], hexen.MapsIn(2));
        Assert.Equal(2, hexen.ClusterOf("MAP13"));
        var doom2 = MapInfo.For(GameCatalog.Get("doom2")!, [ResourceContainer.Open(GameFiles.Require("DOOM2.WAD"))], engine);
        Assert.Empty(doom2.HubClusters);
        Assert.Equal(5, doom2.ClusterOf("MAP01"));
    }

    [Fact]
    public void AMapInAHubSaysItsClusterAndTheProjectsOwnHubsAreHubs()
    {
        var game = GameCatalog.Get("hexen")!;
        var hexen = MapInfo.For(game, [ResourceContainer.Open(GameFiles.Require("HEXEN.WAD"))], GameFiles.Require("gzdoom.pk3"));

        // Hexen's MAP02 moved to the second hub, MAP03 out of any, MAP40 and MAP41 into a hub of the project's own: each block's
        // cluster is the one asked for, in place of the game's; the project's hub is defined once, the game's never.
        var maps = new[]
        {
            new ProjectMap { Slot = "MAP02", File = "maps/MAP02.wad", Hub = 2 },
            new ProjectMap { Slot = "MAP03", File = "maps/MAP03.wad", Hub = 0 },
            new ProjectMap { Slot = "MAP40", File = "maps/MAP40.wad", Hub = 100 },
            new ProjectMap { Slot = "MAP41", File = "maps/MAP41.wad", Hub = 100 },
        };
        var text = ProjectMapInfo.Text(maps, hexen, _ => [])!;
        string BlockOf(string slot) => text[text.IndexOf($"map {slot} ", StringComparison.Ordinal)..][..(text[text.IndexOf($"map {slot} ", StringComparison.Ordinal)..].IndexOf("}\n", StringComparison.Ordinal) + 2)];
        Assert.Equal(["cluster = 2"], BlockOf("MAP02").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("cluster", StringComparison.Ordinal)));
        Assert.Equal(["cluster = 0"], BlockOf("MAP03").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("cluster", StringComparison.Ordinal)));
        Assert.Contains("\tcluster = 100\n", BlockOf("MAP41"), StringComparison.Ordinal);
        Assert.Contains("levelnum = 2", BlockOf("MAP02"), StringComparison.Ordinal);
        Assert.Equal(1, text.Split("cluster 100\n{\n\thub\n}\n").Length - 1);
        Assert.DoesNotContain("cluster 2\n", text, StringComparison.Ordinal);

        // A number below the project's own is never defined here (it would redefine the game's cluster).
        Assert.DoesNotContain("cluster 7\n", ProjectMapInfo.Text([maps[0] with { Hub = 7 }], hexen, _ => [])!, StringComparison.Ordinal);
    }

    /// <summary>The engine's real tables: every Doom II map runs with shorttex and light; MAP14 also pointonline (its checksum is in compatibility.txt).</summary>
    [Fact]
    public void ADoomIIRemixAsksForTheSwitchesTheEngineGivesTheOriginal()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "game_support.pk3"))) return;
        var container = ResourceContainer.Open(GameFiles.Require("DOOM2.WAD"));
        var map14 = Core.Maps.MapLocator.Find(container).First(m => m.Name == "MAP14");
        var (carried, lost) = RemixCompat.For(container, Core.Maps.MapChecksum.Compute(map14), engine);
        Assert.Equal(["light", "pointonline", "shorttex"], carried);
        Assert.Empty(lost);
        var map02 = Core.Maps.MapLocator.Find(container).First(m => m.Name == "MAP02");
        Assert.Equal(["light", "shorttex"], RemixCompat.For(container, Core.Maps.MapChecksum.Compute(map02), engine).Carried);
    }
}
