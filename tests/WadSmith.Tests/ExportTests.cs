using WadSmith.Core.Containers;
using WadSmith.Core.Exporting;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// Export for the classic ports (Phase 12): only the types each port understands, GZDoom's spellings rewritten where a
/// classic one plays the same, and a report that lists exactly what the map lost — read back from the written file.
/// </summary>
public sealed class ExportTests
{
    private static readonly Lazy<XlatTranslator> Xlat = new(() => XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt"));

    private static ClassicPort Port(string id) => ClassicPorts.Get(id)!;

    private static int Action(string name) => ActionCatalog.Find(name)!.Number;

    private static MapModel Tools(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        return map.ToModel();
    }

    private static (ExportReport Report, MapModel Back) Export(MapModel m, string port, PictureNames? pictures = null)
    {
        var report = new ExportReport();
        var lumps = MapExport.ForPort(m, Port(port), "doom", Xlat.Value, report, pictures);
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))));
        return (report, MapConversion.Read(MapLocator.Find(wad).Single(), "doom"));
    }

    private static string[] Keys(ExportReport r) => [.. r.Losses.Select(l => l.Key).Order(StringComparer.Ordinal)];

    [Fact]
    public void ThePortsListHasEveryFamilyForVanillaAndDoomOnlyForBoomAndMbf21()
    {
        Assert.Equal(["vanilla", "limit-removing", "boom", "mbf21"], ClassicPorts.All.Select(p => p.Id));
        var vanilla = Port("vanilla").For("doom")!;
        Assert.True(vanilla.Lines.Contains(1) && vanilla.Lines.Contains(141));
        Assert.False(vanilla.Lines.Contains(78) || vanilla.Lines.Contains(85) || vanilla.Lines.Contains(142));
        Assert.False(vanilla.Sectors.Contains(6) || vanilla.Sectors.Contains(15) || vanilla.Sectors.Contains(18));
        Assert.Same(Port("vanilla").For("heretic"), Port("limit-removing").For("heretic"));
        Assert.Null(Port("boom").For("heretic"));
        Assert.True(Port("boom").For("doom")!.Generalized && Port("boom").For("doom")!.Things.Contains(5001));
        Assert.False(Port("boom").For("doom")!.Lines.Contains(1024));
        Assert.True(Port("mbf21").For("doom")!.Lines.Contains(1024) && Port("mbf21").For("doom")!.Things.Contains(888));
        Assert.Equal(0x3000, Port("mbf21").For("doom")!.LineFlags & 0x3000);
        Assert.Equal(["gzdoom", "vanilla", "limit-removing", "boom", "mbf21"], ProjectExport.ChoicesFor("doom").Select(c => c.Id));
        Assert.Equal(["gzdoom", "vanilla", "limit-removing"], ProjectExport.ChoicesFor("hexen").Select(c => c.Id));
    }

    [Fact]
    public void TheToolsObjectsExportForEveryPortWithNothingLost()
    {
        // Doors, an exit, a secret, a teleporter (sent by the arrival spot's id: rewritten to its room's tag), a hurting floor.
        var model = Tools("room 0 0 256 256\nroom 256 96 384 160\nroom 384 0 640 256\nroom 0 256 128 384\nstart 128 128\ndoor 256 128\nexit 0 128\nsecret 512 128\ndamage 320 128 10\nteleport 64 320 192 192");
        foreach (var port in new[] { "vanilla", "limit-removing", "boom", "mbf21" })
        {
            var (report, back) = Export(model, port);
            Assert.True(report.Losses.Count == 0, port + ": " + report.ToText(verbose: true));
            Assert.Contains(report.Notes, n => n.Contains("teleporter", StringComparison.Ordinal));
            Assert.Contains(report.Notes, n => n.Contains("hurting floors", StringComparison.Ordinal));

            // The teleporter is Doom's WR teleporter (97) tagged to the room holding the arrival spot; the floor is slime (5).
            var teleporters = back.Lines.Where(l => l.Special == 97).ToList();
            Assert.NotEmpty(teleporters);
            var spot = back.Things.Single(t => t.Type == 14);
            Assert.All(teleporters, l => Assert.True(l.Id > 0));
            Assert.Contains(back.Sectors, s => s.Id == teleporters[0].Id && s.FloorHeight <= 0 && back.Sectors.IndexOf(s) >= 0);
            Assert.Equal(0, back.Things.Single(t => t.Type == 14).Id);
            Assert.Single(back.Sectors, s => s.Special == 5);
            Assert.Contains(back.Lines, l => l.Special == 1);
            Assert.Contains(back.Lines, l => l.Special == 11);
            Assert.True(spot.X > 128);
        }
    }

    [Fact]
    public void PlantedGZDoomFeaturesAreReportedExactlyAndNothingElse()
    {
        var model = Tools("room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\ndoor 256 128\nthing 9830 64 64\nthing 2014 192 64");
        var sector = model.Sectors[0];
        sector.Extra["lightcolor"] = UdmfValue.Int(0xFF0000);
        foreach (var f in new[] { "floorplane_a", "floorplane_b", "floorplane_c", "floorplane_d" }) sector.Extra[f] = UdmfValue.Float(f == "floorplane_c" ? 1 : 0.5);
        var bonus = model.Things.Single(t => t.Type == 2014);
        bonus.Height = 32;
        bonus.Special = Action("Door_Open");
        var plain = model.Lines.First(l => l.Special == 0 && l.Back < 0);
        plain.SetFlag("blockprojectiles", true);
        model.Sides[plain.Front].Middle = "VERYLONGNAME";
        // A walk-over floor raise by 512: Boom's type 142, which vanilla does not have.
        var raise = model.Lines.First(l => l.Special == 0 && l.Back < 0 && !ReferenceEquals(l, plain));
        raise.Special = Action("Floor_RaiseByValueTimes8");
        raise.Args[0] = 7;
        raise.Args[1] = 8;
        raise.Args[2] = 64;
        raise.SetFlag("playercross", true);
        // A door the player opens through another wall in front of it: Boom's pass-through use.
        model.Lines.First(l => l.Special != 0 && l.Flag("playeruse")).SetFlag("passuse", true);

        var (vanilla, _) = Export(model, "vanilla");
        Assert.Equal(["object:action", "object:height", "object:unknown-lights", "room:colour", "room:slope", "side:long-name", "wall:Floor_RaiseByValueTimes8", "wall:blockprojectiles", "wall:passuse"], Keys(vanilla));
        Assert.True(vanilla.Breaks);
        Assert.True(vanilla.Losses.Single(l => l.Key == "object:unknown-lights").Breaks);

        // Boom has the floor raise and the pass-through use, and leaves an object it does not know out instead of stopping.
        var (boom, back) = Export(model, "boom");
        Assert.Equal(["object:action", "object:height", "object:unknown-lights", "room:colour", "room:slope", "side:long-name", "wall:blockprojectiles"], Keys(boom));
        Assert.False(boom.Breaks);
        Assert.Contains(back.Lines, l => l.Special == 142 && l.Id == 7);
    }

    [Fact]
    public void AMonsterSwitchBecomesThePlayersSwitchAndASecretDoorMonstersOpenedIsReported()
    {
        var model = Tools("room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\ndoor 256 128");
        var door = model.Lines.First(l => l.Special != 0 && l.Flag("playeruse"));
        door.SetFlag("monsteruse", true);
        door.SetFlag("secret", true);
        // A lift switch monsters may press: no classic type has that.
        var wall = model.Lines.First(l => l.Special == 0 && l.Back < 0);
        wall.Special = Action("Plat_DownWaitUpStayLip");
        wall.Args[0] = 3;
        wall.Args[1] = 32;
        wall.Args[2] = 105;
        wall.SetFlag("playeruse", true);
        wall.SetFlag("monsteruse", true);
        wall.SetFlag("repeatspecial", true);
        var (report, back) = Export(model, "vanilla");
        // The secret door and the lift switch: one line of the report, both walls on it.
        Assert.Equal(2, Assert.Single(report.Losses, l => l.Key == "wall:monsters").Indexes.Count);
        Assert.Equal(["wall:monsters"], Keys(report));
        Assert.Contains(back.Lines, l => l.Special == 62 && l.Id == 3);
    }

    [Fact]
    public void ASecretThatHurtsIsBoomsAndVanillaReportsIt()
    {
        var model = Tools("room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\nsecret 320 128\ndamage 320 128 10");
        var (vanilla, _) = Export(model, "vanilla");
        Assert.Equal(["room:effect"], Keys(vanilla));
        var (boom, back) = Export(model, "boom");
        Assert.Empty(boom.Losses);
        Assert.Contains(back.Sectors, s => s.Special == 0x85);
    }

    [Fact]
    public void GeneralizedTypesAndBoomSectorBitsAreBoomsAndVanillaRefusesThem()
    {
        // A Boom map's walk-over door that waits 30 seconds (generalized 0x3F01) and a secret that hurts (0x80 | 7).
        var classic = new MapModel { Name = "MAP01", Namespace = "doom" };
        classic.Lines.Add(new MapLine { Special = 0x3F01, Id = 4, SourceFlags = 0 });
        classic.Sectors.Add(new MapSector { Special = 0x87 });
        var zdoom = MapTranslation.ToZDoom(classic, Xlat.Value, []);
        var boom = Port("boom").For("doom");
        var vanilla = Port("vanilla").For("doom");
        var found = ClassicTranslation.TypeOf(zdoom.Lines[0], Xlat.Value, boom);
        Assert.NotNull(found);
        Assert.Equal(4, found.Value.Tag);
        Assert.InRange(found.Value.Type, 0x2F80, 0x7FFF);
        Assert.Null(ClassicTranslation.TypeOf(zdoom.Lines[0], Xlat.Value, vanilla));
        Assert.Equal(0x87, ClassicTranslation.SectorTypeOf(zdoom.Sectors[0], Xlat.Value, boom));
        Assert.Null(ClassicTranslation.SectorTypeOf(zdoom.Sectors[0], Xlat.Value, vanilla));
        // Without the port, the old answer stands: the lowest type of any standard.
        Assert.NotNull(ClassicTranslation.SectorTypeOf(zdoom.Sectors[0].Special & 0xFF, false, Xlat.Value));
    }

    [Fact]
    public void PicturesAndDoomIIObjectsTheGameLacksStopVanilla()
    {
        var model = Tools("room 0 0 256 256\nstart 128 128\nthing 66 64 64");
        var pictures = new PictureNames(n => n != Look.For("doom").Wall, _ => true);
        var report = new ExportReport();
        MapExport.ForPort(model, Port("vanilla"), "doom", Xlat.Value, report, pictures, exMyNames: true);
        Assert.Equal(["object:doom2:66", "side:no-picture"], Keys(report));
        Assert.All(report.Losses, l => Assert.True(l.Breaks));
    }

    [Fact]
    public void AProjectExportsAsAWadWithUmapinfoForBoomAndAPk3ForGZDoom()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(Tools("room 0 0 256 256\nstart 128 128"), "Proving Grounds");
        project.UpdateMap("MAP01", m => m with { Sky = "SKY2", Next = ProjectMapInfo.EndToTitle });
        project.WriteFile("textures/MINE.png", [1, 2, 3]);

        var boomFile = Path.Combine(temp.Path, "out", "boom.wad");
        var boom = ProjectExport.Write(project, "boom", boomFile, engine);
        var wad = WadContainer.Open(boomFile);
        Assert.Equal("MAP01", MapLocator.Find(wad).Single().Name);
        var umapinfo = System.Text.Encoding.ASCII.GetString(wad.Entries.Single(e => e.Name == "UMAPINFO").Read());
        Assert.Contains("levelname = \"Proving Grounds\"", umapinfo);
        Assert.Contains("skytexture = \"SKY2\"", umapinfo);
        Assert.Contains("endgame = true", umapinfo);
        // A picture the maps do not use is not a loss: pictures are carried, or listed, by the maps' use of them.
        Assert.Empty(boom.Losses);

        var vanilla = ProjectExport.Write(project, "vanilla", Path.Combine(temp.Path, "out", "vanilla.wad"), engine);
        Assert.Equal(["map:info"], Keys(vanilla));
        Assert.DoesNotContain(WadContainer.Open(Path.Combine(temp.Path, "out", "vanilla.wad")).Entries, e => e.Name == "UMAPINFO");

        var pk3File = Path.Combine(temp.Path, "out", "p.pk3");
        Assert.Empty(ProjectExport.Write(project, "gzdoom", pk3File, engine).Losses);
        var names = ZipContainer.FromBytes(File.ReadAllBytes(pk3File)).Entries.Select(e => e.FullPath).ToList();
        Assert.Contains("maps/MAP01.wad", names);
        Assert.Contains("textures/MINE.png", names);
        Assert.DoesNotContain(names, n => n.Contains("wadsmith.json", StringComparison.Ordinal) || n.StartsWith(".wadsmith", StringComparison.Ordinal));
    }

    [Fact]
    public void APk3WritesTheMapsAgainSoAnOlderFilesFractionalOffsetIsCleanForGZDoom()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(Tools("room 0 0 256 256\nstart 128 128"));
        // The map file as WadSmith wrote it before offsets were split: a fraction in offsetx, which GZDoom reads as an error.
        var file = Path.Combine(project.Folder, "maps", "MAP01.wad");
        var lumps = MapLocator.Find(WadContainer.Open(file)).Single();
        var text = new System.Text.RegularExpressions.Regex("(sidedef[^\n]*\n\\{\n)").Replace(System.Text.Encoding.UTF8.GetString(lumps.Get("TEXTMAP")!), "$1offsetx = 479.99999999999994;\n", 1);
        File.WriteAllBytes(file, WadWriter.Write(WadKind.Pwad, [("MAP01", ReadOnlyMemory<byte>.Empty), ("TEXTMAP", System.Text.Encoding.UTF8.GetBytes(text)), ("ENDMAP", ReadOnlyMemory<byte>.Empty)]));
        Assert.Contains("offsetx = 479.99999999999994;", text, StringComparison.Ordinal);

        var pk3 = Path.Combine(temp.Path, "p.pk3");
        ProjectExport.Write(project, "gzdoom", pk3, null);
        var packed = ZipContainer.FromBytes(File.ReadAllBytes(pk3)).Entries.Single(e => e.FullPath == "maps/MAP01.wad").Read();
        var written = System.Text.Encoding.UTF8.GetString(MapLocator.Find(WadContainer.FromBytes(packed)).Single().Get("TEXTMAP")!);
        Assert.Contains("offsetx = 479;", written, StringComparison.Ordinal);
        Assert.DoesNotMatch("offsetx = [0-9]+\\.", written);
    }

    [Fact]
    public void AHexenMapsScriptsKeepTheirPlaceAndTheirBytesThroughTheNodeBuild()
    {
        // A map made in memory, with real scripts: vanilla Hexen reads BEHAVIOR as the eleventh lump after the marker.
        var model = Tools("room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\ndoor 256 128");
        byte[] scripts = [(byte)'A', (byte)'C', (byte)'S', 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7, 7];
        model.OtherLumps.Add(("BEHAVIOR", scripts));
        var report = new ExportReport();
        var lumps = MapExport.ForPort(model, Port("vanilla"), "hexen", null, report);
        Assert.Equal(["MAP01", "THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SEGS", "SSECTORS", "NODES", "SECTORS", "REJECT", "BLOCKMAP", "BEHAVIOR"], lumps.Select(l => l.Name));
        Assert.Equal(scripts, lumps[11].Data);
        Assert.NotEmpty(lumps[5].Data);

        // Scripts compiled for GZDoom (ACSE) cannot run in vanilla Hexen: reported, and an empty script file takes their place.
        model.OtherLumps.Clear();
        model.OtherLumps.Add(("BEHAVIOR", [(byte)'A', (byte)'C', (byte)'S', (byte)'E', 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        report = new ExportReport();
        lumps = MapExport.ForPort(model, Port("vanilla"), "hexen", null, report);
        Assert.Contains(report.Losses, l => l.Key == "map:scripts");
        Assert.Equal("ACS\0"u8.ToArray(), lumps[11].Data[..4]);
    }

    [Fact]
    public void AHexenMapKeepsItsActionsAndIsKnownByItsBehaviorLump()
    {
        var model = Tools("room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\ndoor 256 128");
        var report = new ExportReport();
        var lumps = MapExport.ForPort(model, Port("vanilla"), "hexen", null, report);
        Assert.Contains(lumps, l => l.Name == "BEHAVIOR");
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))));
        var read = MapLocator.Find(wad).Single();
        Assert.Equal(MapFormat.Hexen, read.Format);
        Assert.Contains(MapConversion.Read(read, "hexen").Lines, l => l.Special == model.Lines.First(m => m.Special != 0).Special);
    }
}
