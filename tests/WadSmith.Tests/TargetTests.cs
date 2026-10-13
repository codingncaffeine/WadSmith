using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>A project's target (6b.1): a vanilla project keeps its map in the classic format, and the editor reads it back through the engine's table.</summary>
public sealed class TargetTests
{
    [Fact]
    public async Task AVanillaProjectKeepsItsMapInTheClassicFormatAndTheEditorReadsItBack()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom", Targets.Vanilla);
        Assert.True(project.ClassicFormat);
        Assert.Equal(Targets.Vanilla, Project.Open(project.Folder).Info.Target);

        // A map made by the tools, saved as the vanilla file: Doom's own format on disk.
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\ndoor 256 128\nexit 0 128", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        project.SaveMap(EditorSession.ForProject(project, map.ToModel(), engine));
        var file = Path.Combine(project.Folder, "maps", "MAP01.wad");
        var lumps = MapLocator.Find(WadContainer.Open(file)).Single();
        Assert.Equal(MapFormat.Doom, lumps.Format);
        Assert.Contains(lumps.Lumps, l => l.Name == "LINEDEFS");

        // The editor opens it in its own model: the door and the exit are what they were, and monsters still open the door.
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), EngineData: engine));
        Assert.Equal("zdoom", session.Map.ToModel().Namespace);
        var door = session.Map.Sectors.Single(s => s.OnMap && Doors.Recognise(session.Geometry, s) is not null);
        var found = Doors.Recognise(session.Geometry, door)!;
        Assert.True(found.Monsters);
        Assert.Single(session.Map.Lines, l => l.OnMap && Exits.IsExit(l));
        Assert.DoesNotContain(Problems.Check(session.Geometry, null, Look.For("doom"), "doom", null, session.ClassicRules), p => p.Rule == "not-classic");

        // A change saved from the session goes back out in the classic format.
        session.History.Record("Lock", e => Doors.Change(e, found, found.Settings with { Lock = 129 }, found.Monsters, found.StartsOpen));
        await session.FlushAsync();
        var again = MapLocator.Find(WadContainer.Open(file)).Single();
        Assert.Equal(MapFormat.Doom, again.Format);
        var reread = MapConversion.Read(again, "doom");
        Assert.Contains(reread.Lines, l => l.Special == 28);
    }

    [Fact]
    public void WhatHasNoClassicTypeIsListedForAClassicTargetOnly()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(engine)]), "xlat/doom.txt");
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var wall = map.Lines[0];
        history.Record("Odd door", e =>
        {
            e.Set(wall, l => l.Special, (l, v) => l.Special = v, Doors.DoorRaise);
            e.Set(wall, l => l.Args[1], (l, v) => l.Args[1] = v, 40);
            e.Flag(wall, "playeruse", true);
            e.Flag(wall, "repeatspecial", true);
        });
        var geometry = MapGeometry.Build(map.Frame);
        Assert.DoesNotContain(Problems.Check(geometry, null, Look.For("doom"), "doom"), p => p.Rule == "not-classic");
        var problem = Assert.Single(Problems.Check(geometry, null, Look.For("doom"), "doom", null, new ClassicRules("doom", xlat, Targets.Vanilla)), p => p.Rule == "not-classic");
        Assert.Contains("Door_Raise with these settings, has no type in Vanilla Doom (Chocolate Doom)", problem.Sentence);
        Assert.Same(wall, Assert.Single(problem.Show));
    }

    [Fact]
    public async Task ChangingTheTargetWritesTheMapAgainInTheNewFormat()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), EngineData: engine));
        var file = Path.Combine(project.Folder, "maps", "MAP01.wad");
        Assert.Equal(MapFormat.Udmf, MapLocator.Find(WadContainer.Open(file)).Single().Format);

        var changed = 0;
        session.TargetChanged += () => changed++;
        session.SetTarget(Targets.LimitRemoving);
        await session.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal((Targets.LimitRemoving, 1, true), (Project.Open(project.Folder).Info.Target, changed, session.ClassicRules is not null));
        Assert.Equal(MapFormat.Doom, MapLocator.Find(WadContainer.Open(file)).Single().Format);

        session.SetTarget(Targets.GZDoom);
        await session.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(MapFormat.Udmf, MapLocator.Find(WadContainer.Open(file)).Single().Format);
        Assert.Null(session.ClassicRules);
    }
}
