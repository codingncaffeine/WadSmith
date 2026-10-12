using System.Security.Cryptography;
using WadSmith.Core;
using WadSmith.Core.Checks;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

public sealed class ProjectTests
{
    private static MapModel Square()
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        m.Vertices.AddRange([new MapVertex { X = 0, Y = 0 }, new MapVertex { X = 256, Y = 0 }, new MapVertex { X = 256, Y = 256 }, new MapVertex { X = 0, Y = 256 }]);
        m.Sectors.Add(new MapSector { CeilingHeight = 128, FloorTexture = "FLOOR4_8", CeilingTexture = "CEIL3_5" });
        for (var i = 0; i < 4; i++)
        {
            m.Sides.Add(new MapSide { Middle = "STARTAN3", Sector = 0 });
            m.Lines.Add(new MapLine { V1 = i, V2 = (i + 1) % 4, Front = i });
            m.Lines[i].SetFlag("blocking", true);
        }

        m.Things.Add(new MapThing { X = 128, Y = 128, Type = 1, Angle = 90 });
        return m;
    }

    [Fact]
    public void AProjectSavesAndReopensItsMaps()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "My First Map");
        var p = Project.Create(folder, "My First Map", "freedoom2");
        p.SaveMap(Square(), "Entryway");

        var again = Project.Open(folder);
        Assert.Equal("freedoom2", again.Info.Game);
        Assert.Equal("Entryway", again.Info.Maps.Single().Title);
        var map = again.LoadMap("MAP01");
        Assert.Equal(4, map.Lines.Count);
        Assert.True(map.Lines[0].Flag("blocking"));
        Assert.Empty(MapValidator.Check(map));
        Assert.False(File.Exists(Path.Combine(folder, "maps", "MAP01.wad.saving")));
    }

    [Fact]
    public void ACreatedProjectIsNotOverwritten()
    {
        using var temp = new TempFolder();
        Project.Create(temp.Path, "One", "doom2");
        Assert.Throws<IOException>(() => Project.Create(temp.Path, "Two", "doom2"));
    }

    [Fact]
    public void AProjectFromANewerWadSmithIsRefusedPolitely()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, Project.SidecarName), """{ "format": 99, "name": "x", "game": "doom2" }""");
        var ex = Assert.Throws<CorruptDataException>(() => Project.Open(temp.Path));
        Assert.Contains("newer WadSmith", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemixingAMapLeavesTheBaseGameUntouched()
    {
        var iwad = GameFiles.Require("freedoom1.wad");
        var engine = GameFiles.Require("gzdoom.pk3");
        var before = SHA256.HashData(File.ReadAllBytes(iwad));
        using var temp = new TempFolder();
        var notes = new List<string>();
        var project = Remix.Create(Path.Combine(temp.Path, "remix"), iwad, "E1M1", engine, notes);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(iwad)));
        Assert.Equal("freedoom1", project.Info.Game);
        Assert.Equal("remix", project.Info.Maps[0].Origin?.Kind);
        var map = Project.Open(project.Folder).LoadMap("E1M1");
        Assert.Equal("zdoom", map.Namespace);
        var original = MapConversion.Read(MapLocator.Find(WadContainer.Open(iwad)).First(m => m.Name == "E1M1"));
        Assert.Equal(original.Lines.Count, map.Lines.Count);
        Assert.Equal(original.Things.Count, map.Things.Count);
        Assert.DoesNotContain(MapValidator.Check(map), p => p.Severity == Severity.Error);
    }

    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void EveryMapOfEveryBaseGameIsStructurallySound(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        foreach (var lumps in MapLocator.Find(wad))
        {
            var errors = MapValidator.Check(MapConversion.Read(lumps, game)).Where(p => p.Severity == Severity.Error).ToList();
            Assert.True(errors.Count == 0, $"{fileName} {lumps.Name}: {string.Join(" ", errors.Select(e => e.Message))}");
        }
    }

    [Fact]
    public void BrokenReferencesAreErrors()
    {
        var m = Square();
        m.Lines[0].V2 = 99;
        m.Sides[1].Sector = 7;
        m.Lines[2].Front = -1;
        var problems = MapValidator.Check(m);
        Assert.Equal(3, problems.Count(p => p.Severity == Severity.Error));
        Assert.Contains(problems, p => p.Message == "Line 0 ends at a corner that does not exist.");
    }
}

public sealed class StressMapTests
{
    [Fact]
    public void TheStressGridIsStructurallySoundAndEveryRoomIsClosed()
    {
        var m = Cli.Commands.StressGenCommand.Build(6);
        Assert.Equal(2 * 6 * 7, m.Lines.Count);
        Assert.Empty(MapValidator.Check(m));
        var map = Editor.Model.EditorMap.FromModel(m);
        var geometry = Editor.Geometry.MapGeometry.Build(map.Frame);
        foreach (var s in map.Sectors) Assert.Single(geometry.Loops(s));
        Assert.Same(map.Sectors[0], geometry.SectorAt(new Editor.Geometry.MapPoint(64, 64)));
    }
}
