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
    public void TheOldFormatIsFlaggedAndTheRestStillRead()
    {
        var maps = new Dictionary<string, MapDefinition>(LumpName.Comparer);
        var old = MapInfo.Parse("map MAP01 \"Winnowing Hall\"\nwarptrans 1\nnext 2\nsky1 SKY2 2\nmap MAP02 \"Seven Portals\" { next = \"MAP03\" }\n", "MAPINFO", _ => null, maps, []);
        Assert.True(old);
        Assert.Equal(["MAP02"], maps.Keys);
        Assert.Equal("MAP03", maps["MAP02"].Text("next"));
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
