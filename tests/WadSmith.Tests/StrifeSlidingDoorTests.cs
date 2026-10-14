using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Strife's sliding doors (D62): a door whose picture slides open (Door_Animated, an ANIMDEFS <c>animateddoor</c> picture on its
/// faces) is a door like any other — found in Strife's own maps, made from any door by the Inspector's "Opens", changed and
/// changed back, and opened by a switch.
/// </summary>
public sealed class StrifeSlidingDoorTests
{
    [Fact]
    public void AnimdefsGivesEachSlidingDoorItsFramesAndSounds()
    {
        var doors = new Dictionary<string, AnimatedDoor>(StringComparer.OrdinalIgnoreCase);
        AnimatedDoors.Animdefs("""
            switch strife SWKNOB01 on pic SWKNOB02 tics 0
            // a comment, animateddoor NOTME
            animateddoor  DORQTR01
            	opensound DoorOpenAirlock
            	closesound DoorCloseAirlock
            	pic DORQTR01
            	pic DORQTR02
            /* another */ animateddoor GATE01 pic 1 pic 2 pic 3
            warp texture b@ckdrop 0.83445
            """, doors);
        Assert.Equal(["DORQTR01", "GATE01"], doors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("DORQTR01", "DoorOpenAirlock", "DoorCloseAirlock"), (doors["dorqtr01"].Name, doors["dorqtr01"].OpenSound, doors["dorqtr01"].CloseSound));
        Assert.Equal(["DORQTR01", "DORQTR02"], doors["DORQTR01"].Frames);
        Assert.Equal(["GATE01", "GATE02", "GATE03"], doors["GATE01"].Frames);
        Assert.Null(doors["GATE01"].OpenSound);
    }

    /// <summary>Two Strife rooms and a door between them (64 wide, Strife's look), with its history.</summary>
    private static (EditorMap Map, History History, Door Door) MadeDoor()
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP02", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\ndoor 256 128", Look.For("strife"), "strife"), s => Assert.True(s.Done, s.Said));
        var geometry = MapGeometry.Build(map.Frame);
        return (map, new History(map), map.Sectors.Select(s => Doors.Recognise(geometry, s)).Single(d => d is not null)!);
    }

    [Fact]
    public void ADoorBecomesAStrifeSlidingDoorAndBack()
    {
        var (map, history, door) = MadeDoor();
        Assert.Equal(DoorKind.Rises, door.Kind);
        string? why = null;
        history.Record("Make the door slide open like Strife's", e => why = Doors.SetKind(e, MapGeometry.Build(map.Frame), door, DoorKind.Animated, "DORQTR01", (64, 72)));
        Assert.Null(why);
        var sliding = Doors.Recognise(MapGeometry.Build(map.Frame), door.Area)!;
        Assert.Equal(DoorKind.Animated, sliding.Kind);
        Assert.All(sliding.Own, l => Assert.Equal((Doors.DoorAnimated, 0, Doors.AnimatedSpeed, Doors.Wait, 0, "DORQTR01"), (l.Special, l.Args[0], l.Args[1], l.Args[2], l.Args[3], l.Front.Top)));
        Assert.Equal(2, sliding.Own.Count());
        Assert.Equal(door.Area.FloorHeight, door.Area.CeilingHeight);

        // Faster, staying open, locked: Door_Animated's own arguments.
        history.Record("Change door speed", e => Doors.Change(e, sliding, sliding.Settings with { Fast = true, StaysOpen = true, Lock = 4 }, true, false));
        var changed = Doors.Recognise(MapGeometry.Build(map.Frame), door.Area)!;
        Assert.Equal((DoorKind.Animated, true, true, 4), (changed.Kind, changed.Settings.Fast, changed.Settings.StaysOpen, changed.Settings.Lock));
        Assert.All(changed.Own, l => Assert.Equal((Doors.DoorAnimated, Doors.AnimatedFastSpeed, 0, 4), (l.Special, l.Args[1], l.Args[2], l.Args[3])));

        // Upward again: Door_LockedRaise with the same lock; one undo each way.
        history.Record("Make the door open upward", e => Doors.SetKind(e, MapGeometry.Build(map.Frame), changed, DoorKind.Rises));
        var rising = Doors.Recognise(MapGeometry.Build(map.Frame), door.Area)!;
        Assert.Equal((DoorKind.Rises, 4, Doors.DoorLockedRaise), (rising.Kind, rising.Settings.Lock, rising.Own.First().Special));
        history.Undo();
        history.Undo();
        history.Undo();
        Assert.Equal(DoorKind.Rises, Doors.Recognise(MapGeometry.Build(map.Frame), door.Area)!.Kind);
        Assert.All(door.Own, l => Assert.Equal((Doors.DoorRaise, "DORMS01"), (l.Special, l.Front.Top)));
    }

    [Fact]
    public void ASwitchForAStrifeSlidingDoorSlidesItOpen()
    {
        var (map, history, door) = MadeDoor();
        history.Record("Make the door slide open like Strife's", e => Doors.SetKind(e, MapGeometry.Build(map.Frame), door, DoorKind.Animated, "DORQTR01", (64, 72)));
        Assert.All(EditScript.Run(map, "switch 264 128 128 0", Look.For("strife"), "strife"), s => Assert.True(s.Done, s.Said));
        var found = Doors.Recognise(MapGeometry.Build(map.Frame), door.Area)!;
        var remote = Assert.Single(found.Remote);
        Assert.Equal((Doors.DoorAnimated, door.Area.Tag, Doors.AnimatedSpeed), (remote.Special, remote.Args[0], remote.Args[1]));
        Assert.Equal("opens a door", Switches.Recognise(MapGeometry.Build(map.Frame), remote)!.Does);
    }

    [Fact]
    public void EveryOneOfStrifesSlidingDoorsIsFoundAsOne()
    {
        // STRIFE1 has 39 areas opened by line 144 (Door_Animated from their own walls); each is a door that slides, and the doors
        // the control marks by Strife's own line numbers are exactly the doors found.
        var wad = GameFiles.Require("STRIFE1.WAD");
        var engine = new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]);
        var container = ResourceContainer.Open(wad);
        var (sliding, found, marked, right) = (0, 0, 0, 0);
        foreach (var lumps in MapLocator.Find(container))
        {
            var model = MapConversion.Read(lumps, "strife");
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(model, XlatTranslator.Load(engine, XlatTranslator.PathFor(model.Namespace)), []));
            var geometry = MapGeometry.Build(map.Frame);
            sliding += map.Sectors.Count(s => s.OnMap && Doors.Recognise(geometry, s) is { Kind: DoorKind.Animated });
            var doors = Recognition.Find(map, geometry).ByKind["doors"];
            var expected = Recognition.Expected(model).ByKind["doors"];
            (found, marked, right) = (found + doors.Count, marked + expected.Count, right + doors.Count(expected.Contains));
        }

        Assert.Equal(39, sliding);
        Assert.Equal((marked, marked), (found, right));
    }

    [Fact]
    public async Task TheInspectorMakesAStrifeDoorSlideAndOffersOnlySlidingPictures()
    {
        using var temp = new TempFolder();
        var game = GameCatalog.Get("strife")!;
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "strife");
        var made = MadeDoor();
        project.SaveMap(made.Map.Frame.ToModel());
        var wad = GameFiles.Require("STRIFE1.WAD");
        var resources = GameResources.Load("strife", project.Folder, [new FoundGame(game, wad, new FileInfo(wad).Length)], GameFiles.Require("gzdoom.pk3"))!;
        var ui = new ConcurrentQueue<Action>();
        using var session = EditorSession.Open(project, "MAP02", new SessionHooks(ui.Enqueue, resources));
        async Task Settle()
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            do
            {
                while (ui.TryDequeue(out var a)) a();
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            while ((session.Geometry.Version != session.Map.Version || !ui.IsEmpty) && DateTime.UtcNow < until);
        }

        await Settle();
        var area = session.Map.Sectors.Single(s => s.OnMap && Doors.Recognise(session.Geometry, s) is not null);
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(area);
        await Settle();
        var opens = inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Opens");
        Assert.Equal(["Upward", "Slides aside", "Swings", "Like Strife's doors"], opens.Options);
        Assert.Contains("DORML01", inspector.Rows.OfType<PictureChoiceRow>().Single(r => r.Label == "Looks like").Choices.Select(c => c.Name));

        opens.Value = 3;
        await Settle();
        var door = Doors.Recognise(session.Geometry, area)!;
        Assert.Equal(DoorKind.Animated, door.Kind);
        Assert.All(door.Own, l => Assert.Equal("DORQTR01", l.Front.Top));
        Assert.StartsWith("Slides open, like Strife's doors,", inspector.Summary, StringComparison.Ordinal);
        var looks = inspector.Rows.OfType<PictureChoiceRow>().Single(r => r.Label == "Looks like").Choices.Select(c => c.Name).ToList();
        Assert.Equal(["DORALN01", "DORCHN01", "DORCRG01", "DORIRS01", "DORQTR01", "DORSTN01", "SIGLDR01"], looks);
        Assert.Equal("72, 72 tall", inspector.Rows.OfType<InfoRow>().Single(r => r.Label == "Opens to").Value);
        Assert.DoesNotContain(inspector.Rows, r => r.Label == "Starts open");
    }
}
