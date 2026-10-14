using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Sliding and swinging doors (D54, D62): a door the Door tool made, turned into a polyobject door — one, or two halves that
/// part — that stands closed in its doorway, slides into the wall beside it or swings into the room asked for, is found again
/// as that door, and turns back into a rising door; each one undo step.
/// </summary>
public sealed class PolyDoorTests
{
    /// <summary>
    /// A room 256 square (x 0–256) and a second east of it (x 256–512, y 0–256) sharing their whole wall: a door clicked onto
    /// that wall is 64 wide in its middle with wall either side, in the second room's side, frames 72 tall (DOOR3) when
    /// <paramref name="frames"/>.
    /// </summary>
    internal static (EditorMap Map, History History, Sector DoorArea, Sector West, Sector East) TwoRoomsAndADoor(bool frames = true)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nroom 256 0 512 256", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        var (west, east) = (map.Sectors.Single(s => s.OnMap && s.Lines(map).All(l => Math.Max(l.Start.X, l.End.X) <= 256)), map.Sectors.Single(s => s.OnMap && s.Lines(map).All(l => Math.Min(l.Start.X, l.End.X) >= 256)));
        var history = new History(map);
        var opening = map.Lines.Single(l => l.OnMap && l.Back is not null);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Look.For("doom"), Doors.Centred(opening, Doors.StandardWidth), fill: true, heightOf: frames ? t => t == "DOOR3" ? 72 : 128 : null));
        Assert.NotNull(made.Door);
        return (map, history, made.Door!, west, east);
    }

    private static PolyDoor Found(EditorMap map, Sector doorway) => PolyDoors.Recognise(MapGeometry.Build(map.Frame), doorway, "doom") ?? throw new InvalidOperationException("no sliding or swinging door there");

    /// <summary>Where the polyobjects stand as the map starts, their corners carried by spot less anchor.</summary>
    private static List<MapPoint> Standing(EditorMap map)
    {
        var g = MapGeometry.Build(map.Frame);
        var f = g.Frame;
        return [.. g.Polyobjects("doom").SelectMany(p => p.Lines.SelectMany(slot => new[] { f.Lines[slot].V1, f.Lines[slot].V2 }).Distinct().Select(v => new MapPoint(f.Vertices[v].X + p.Dx, f.Vertices[v].Y + p.Dy)))];
    }

    [Fact]
    public void ADoorSlidesAsideIntoTheWallAndOpensFromBothRooms()
    {
        var (map, history, area, west, east) = TwoRoomsAndADoor();
        var rising = Doors.Recognise(MapGeometry.Build(map.Frame), area)!;
        string? why = "not run";
        history.Record("Make the door slide aside", e => why = PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout()));
        Assert.Null(why);

        // The doorway opens to its frames' height (the picture's), and the old door is gone as a rising door.
        Assert.Equal(72, area.CeilingHeight - area.FloorHeight);
        Assert.Null(Doors.Recognise(MapGeometry.Build(map.Frame), area));

        var door = Found(map, area);
        Assert.Equal((DoorKind.Slides, 1, false), (door.Kind, door.Halves.Count, door.Double));
        Assert.Equal(rising.Settings, door.Settings);
        Assert.True(door.Monsters);

        // Its two faces open it: Polyobj_DoorSlide(1, normal speed, along the wall, its width, Doom's wait), used again and again.
        Assert.Equal(2, door.Own.Count());
        Assert.All(door.Own, l => Assert.Equal((PolyDoors.DoorSlide, 1, Doors.NormalSpeed, 64, Doors.Wait), (l.Special, l.Args[0], l.Args[1], l.Args[3], l.Args[4])));
        Assert.All(door.Own, l => Assert.True(l.Flag("playeruse") && l.Flag("repeatspecial") && l.Flag("monsteruse")));
        Assert.Contains(door.Own.First().Args[2], new[] { 64, 192 });
        Assert.All(door.Own, l => Assert.Equal("DOOR3", l.Front.Middle));

        // It stands closed in the doorway, 8 thick across its middle, the full 64 of it.
        var at = Standing(map);
        var areaXs = area.Lines(map).SelectMany(l => new[] { l.Start.X, l.End.X }).ToList();
        Assert.Equal((areaXs.Min() + 4, areaXs.Max() - 4), (at.Min(p => p.X), at.Max(p => p.X)));
        Assert.Equal((96.0, 160.0), (at.Min(p => p.Y), at.Max(p => p.Y)));

        // Its pen lies below the map, outside everything; its anchor in the pen, its spot in the doorway.
        var half = door.Halves[0];
        Assert.True(half.Pen!.Flag(PolyDoors.PenField));
        Assert.True(half.Anchor.Y < 0 && MapGeometry.Build(map.Frame).SectorAt(new MapPoint(half.Spot.X, half.Spot.Y)) == area);
        Assert.Equal((Polyobjects.Anchor, Polyobjects.Spot, 1, 1), (half.Anchor.Type, half.Spot.Type, half.Anchor.Angle, half.Spot.Angle));

        // One undo step brings the rising door back.
        history.Undo();
        Assert.NotNull(Doors.Recognise(MapGeometry.Build(map.Frame), area));
        Assert.Empty(MapGeometry.Build(map.Frame).Polyobjects("doom"));
        Assert.Equal((west.Tag, east.Tag), (0, 0));
    }

    [Fact]
    public void ADoubleDoorPartsIntoTheWallOnEitherSide()
    {
        var (map, history, area, _, _) = TwoRoomsAndADoor();
        history.Record("Make the door slide aside", e => PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout(Double: true)));
        var door = Found(map, area);
        Assert.Equal((2, true), (door.Halves.Count, door.Double));

        // The first half names the second as its mirror; all four faces name the first, sliding half the width.
        var polys = MapGeometry.Build(map.Frame).Polyobjects("doom");
        Assert.Equal(door.Halves[1].Number, polys.Single(p => p.Number == door.Number).Mirror);
        Assert.Equal(4, door.Own.Count());
        Assert.All(door.Own, l => Assert.Equal((door.Number, 32), (l.Args[0], l.Args[3])));

        // In the pen no corner of a polyobject lies where another corner does: GZDoom's node builder welds corners in one place,
        // and halves sharing corners would each take in the other's walls (in GZDoom, a double door that did not move).
        var polyCorners = door.Halves.SelectMany(h => h.Lines).SelectMany(l => new[] { l.Start, l.End }).Distinct().ToList();
        foreach (var v in polyCorners)
        {
            Assert.DoesNotContain(map.Vertices, o => o.OnMap && !ReferenceEquals(o, v) && Math.Abs(o.X - v.X) < 0.5 && Math.Abs(o.Y - v.Y) < 0.5);
        }

        // The halves meet in the doorway's middle; the first slides toward its own end (the mirror the other way).
        var first = door.Halves[0].Lines.SelectMany(l => new[] { l.Start, l.End }).Select(v => v.Y + (door.Halves[0].Spot.Y - door.Halves[0].Anchor.Y)).ToList();
        var middle = 128.0;
        var firstBelow = first.Max() <= middle + 0.5;
        var angle = door.Own.First().Args[2];
        Assert.Equal(firstBelow ? 192 : 64, angle);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ADoorSwingsIntoTheRoomAskedFor(bool intoWest, bool twoHalves)
    {
        var (map, history, area, west, east) = TwoRoomsAndADoor();
        string? why = "not run";
        history.Record("Make the door swing", e => why = PolyDoors.FromRising(e, area, DoorKind.Swings, Look.For("doom"), "doom", new PolyDoors.Layout(Double: twoHalves, Into: new MapPoint(intoWest ? -1 : 1, 0))));
        Assert.Null(why);
        var door = Found(map, area);
        Assert.Equal((DoorKind.Swings, twoHalves ? 2 : 1), (door.Kind, door.Halves.Count));
        Assert.All(door.Own, l => Assert.Equal((PolyDoors.DoorSwing, door.Number, PolyDoors.QuarterTurn, Doors.Wait), (l.Special, l.Args[0], l.Args[2], l.Args[3])));

        // A swinging door is as tall as the room it swings into: the frames rise to it.
        Assert.Equal(128, area.CeilingHeight - area.FloorHeight);

        // Turned a quarter about its spot — the first half counter-clockwise, its mirror the other way — each half's free end
        // comes to rest in the room asked for, and the door says so.
        var g = MapGeometry.Build(map.Frame);
        var f = g.Frame;
        var target = intoWest ? west : east;
        Assert.Equal(intoWest ? "west" : "east", PolyDoors.Compass(PolyDoors.SwingDirection(door)));
        foreach (var (half, i) in door.Halves.Select((h, i) => (h, i)))
        {
            var poly = g.Polyobjects("doom").Single(p => p.Number == half.Number);
            var turn = i == 0 ? Math.PI / 2 : -Math.PI / 2;
            var corners = poly.Lines.SelectMany(slot => new[] { f.Lines[slot].V1, f.Lines[slot].V2 }).Distinct().Select(v => new MapPoint(f.Vertices[v].X + poly.Dx, f.Vertices[v].Y + poly.Dy)).ToList();
            var (sx, sy) = (half.Spot.X, half.Spot.Y);
            var free = corners.OrderByDescending(p => ((p.X - sx) * (p.X - sx)) + ((p.Y - sy) * (p.Y - sy))).First();
            var (cx, cy) = (free.X, free.Y);
            var swung = new MapPoint(sx + ((cx - sx) * Math.Cos(turn)) - ((cy - sy) * Math.Sin(turn)), sy + ((cx - sx) * Math.Sin(turn)) + ((cy - sy) * Math.Cos(turn)));
            var landed = g.SectorAt(swung);
            Assert.True(ReferenceEquals(landed, target), $"half {i} swung to {swung} in {(landed is null ? "nothing" : ReferenceEquals(landed, west) ? "the west room" : ReferenceEquals(landed, east) ? "the east room" : "another area")}");
        }
    }

    [Fact]
    public void ASlidingDoorTurnsBackIntoARisingDoorWithItsSettingsAndPicture()
    {
        var (map, history, area, _, _) = TwoRoomsAndADoor();
        history.Record("Make the door slide aside", e => PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout(Double: true)));
        var sliding = Found(map, area);
        history.Record("Change the door", e => PolyDoors.Change(e, sliding, sliding.Settings with { Fast = true, StaysOpen = true, Lock = 129 }, true));
        var changed = Found(map, area);
        Assert.Equal((true, true, 129, false), (changed.Settings.Fast, changed.Settings.StaysOpen, changed.Settings.Lock, changed.Monsters));
        Assert.All(changed.Own, l => Assert.Equal((Doors.FastSpeed, -1, 129), (l.Args[1], l.Args[4], PolyDoors.LockOf(l))));
        history.Record("Change the door's look", e => PolyDoors.Dress(e, changed, "BIGDOOR2", 128));
        Assert.Equal(128, area.CeilingHeight - area.FloorHeight);

        string? why = "not run";
        history.Record("Make the door open upward", e => why = PolyDoors.ToRising(e, Found(map, area), Look.For("doom"), t => t == "BIGDOOR2" ? 128 : 72));
        Assert.Null(why);
        var rising = Doors.Recognise(MapGeometry.Build(map.Frame), area)!;
        Assert.Equal((DoorKind.Rises, true, true, 129), (rising.Kind, rising.Settings.Fast, rising.Settings.StaysOpen, rising.Settings.Lock));
        Assert.All(rising.Own, l => Assert.Equal("BIGDOOR2", l.Front.Top));
        Assert.Equal(0, area.CeilingHeight - area.FloorHeight);
        Assert.Empty(MapGeometry.Build(map.Frame).Polyobjects("doom"));
        Assert.DoesNotContain(map.Sectors, s => s.OnMap && s.Flag(PolyDoors.PenField));
        Assert.DoesNotContain(map.Things, t => t.OnMap && t.Type is Polyobjects.Anchor or Polyobjects.Spot);
        Assert.DoesNotContain(map.Vertices, v => v.OnMap && v.Y < 0);
    }

    [Fact]
    public void ADoorWithRoomsRightBesideItCannotSlideAndSaysWhy()
    {
        // A corridor 64 wide from the first room to the second, its end filled by the door: rooms lie right beside the doorway
        // on both sides, so a door 64 wide has nowhere to slide.
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\nroom 384 0 640 256\nroom 256 160 384 256\nroom 256 0 384 96\ndoor 384 128", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        var area = map.Sectors.Single(s => s.OnMap && Doors.Recognise(MapGeometry.Build(map.Frame), s) is not null);
        string? why = null;
        new History(map).Record("Make the door slide aside", e => why = PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout()));
        Assert.StartsWith("A sliding door needs wall 64 wide beside the doorway", why, StringComparison.Ordinal);
        Assert.NotNull(Doors.Recognise(MapGeometry.Build(map.Frame), area));
    }
}

/// <summary>The walls round an area, from the map as it is.</summary>
internal static class SectorLines
{
    public static IEnumerable<Line> Lines(this Sector s, EditorMap map) => map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, s) || ReferenceEquals(l.Back?.Sector, s)));
}

/// <summary>Sliding and swinging doors in the editor (D62): made, laid out and turned back from the Inspector, deleted whole.</summary>
public sealed class PolyDoorInspectorTests
{
    [Fact]
    public async Task TheInspectorMakesADoorSlideSwingPartAndRiseAgain()
    {
        using var temp = new TempFolder();
        var game = WadSmith.Core.Games.GameCatalog.Get("freedoom2")!;
        var project = WadSmith.Core.Projects.Project.Create(Path.Combine(temp.Path, "p"), "p", "freedoom2");
        var (map, _, _, _, _) = PolyDoorTests.TwoRoomsAndADoor();
        project.SaveMap(map.Frame.ToModel());
        var wad = GameFiles.Require("freedoom2.wad");
        var resources = GameResources.Load("freedoom2", project.Folder, [new WadSmith.Core.Games.FoundGame(game, wad, new FileInfo(wad).Length)], GameFiles.Find("gzdoom.pk3"))!;
        var ui = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(ui.Enqueue, resources));
        var told = new List<string>();
        session.Notice += told.Add;
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
        var inspector = new WadSmith.App.ViewModels.InspectorViewModel(session);
        session.Selection.Set(area);
        await Settle();
        T Row<T>(string label) where T : WadSmith.App.ViewModels.InspectorRow => inspector.Rows.OfType<T>().Single(r => r.Label == label);
        PolyDoor Now() => PolyDoors.Recognise(session.Geometry, area, "doom") ?? throw new InvalidOperationException($"no sliding or swinging door: {string.Join(" / ", told)}");

        var opens = Row<WadSmith.App.ViewModels.ChoiceRow>("Opens");
        Assert.Equal(["Upward", "Slides aside", "Swings"], opens.Options);
        opens.Value = 1;
        await Settle();
        Assert.Equal((DoorKind.Slides, false), (Now().Kind, Now().Double));
        Assert.StartsWith("Slides ", inspector.Summary, StringComparison.Ordinal);
        Assert.Contains(inspector.Rows, r => r.Label == "Slides toward");

        // Two halves that part, then the other way of opening: swinging, into the west room.
        Row<WadSmith.App.ViewModels.FlagRow>("Two halves that part").Value = true;
        await Settle();
        Assert.Equal((DoorKind.Slides, true), (Now().Kind, Now().Double));
        Row<WadSmith.App.ViewModels.ChoiceRow>("Opens").Value = 2;
        await Settle();
        Assert.Equal((DoorKind.Swings, true), (Now().Kind, Now().Double));

        // As tall as the room it swings into, it shows a door picture that tall (DOOR3 is 72 and would repeat up it).
        Assert.All(Now().Own, l => Assert.Equal("SPCDOOR0", l.Front.Middle));
        var into = Row<WadSmith.App.ViewModels.ChoiceRow>("Swings into");
        Assert.Equal(["east", "west"], into.Options.Order(StringComparer.Ordinal));
        var swungFirst = PolyDoors.Compass(PolyDoors.SwingDirection(Now()));
        Assert.Equal(into.Options[into.Value], swungFirst);
        into.Value = into.Options.ToList().IndexOf(swungFirst == "west" ? "east" : "west");
        await Settle();
        into.Value = into.Options.ToList().IndexOf("west");
        await Settle();
        Assert.Equal("west", PolyDoors.Compass(PolyDoors.SwingDirection(Now())));
        Assert.Contains("swing open to the west", inspector.Summary, StringComparison.Ordinal);

        // Locked and staying open, then upward again: a rising door with the same settings, and no polyobject left.
        Row<WadSmith.App.ViewModels.ChoiceRow>("Locked with").Value = 2;
        Row<WadSmith.App.ViewModels.ChoiceRow>("After opening").Value = 1;
        await Settle();
        Assert.Equal((130, true), (Now().Settings.Lock, Now().Settings.StaysOpen));
        Row<WadSmith.App.ViewModels.ChoiceRow>("Opens").Value = 0;
        await Settle();
        var rising = Doors.Recognise(session.Geometry, area)!;
        Assert.Equal((DoorKind.Rises, 130, true), (rising.Kind, rising.Settings.Lock, rising.Settings.StaysOpen));
        Assert.Empty(session.Geometry.Polyobjects("doom"));
        Assert.Empty(told);

        // Each change was one undo step: undone back to the double swinging door.
        session.History.Undo();
        await Settle();
        Assert.Equal((DoorKind.Swings, true), (Now().Kind, Now().Double));
    }

    [Fact]
    public async Task DeletingASlidingDoorsDoorwayTakesItsPolyobjectsAndPenToo()
    {
        using var temp = new TempFolder();
        var project = WadSmith.Core.Projects.Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var (map, history, area, _, _) = PolyDoorTests.TwoRoomsAndADoor();
        history.Record("Make the door slide aside", e => PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout(Double: true)));
        project.SaveMap(map.Frame.ToModel());
        var ui = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var session = EditorSession.Open(project, "MAP01", ui.Enqueue);
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
        var doorway = session.Map.Sectors.Single(s => s.OnMap && PolyDoors.Recognise(session.Geometry, s, "doom") is not null);
        Assert.Equal(2, session.Geometry.Polyobjects("doom").Count);
        session.Selection.Set(doorway);
        session.DeleteSelection();
        await Settle();
        Assert.Empty(session.Geometry.Polyobjects("doom"));
        Assert.DoesNotContain(session.Map.Things, t => t.OnMap && t.Type is Polyobjects.Anchor or Polyobjects.Spot);
        Assert.DoesNotContain(session.Map.Sectors, s => s.OnMap && s.Flag(PolyDoors.PenField));
        Assert.DoesNotContain(session.Map.Lines, l => l.OnMap && (l.Special is Polyobjects.StartLine || PolyDoors.IsDoorAction(l.Special)));
    }
}

/// <summary>The checks know sliding and swinging doors (D62): their spots are no unknown objects, their faces no switches.</summary>
public sealed class PolyDoorProblemTests
{
    [Fact]
    public void ASlidingDoorsSpotsAndFacesAreNoProblems()
    {
        var (map, history, area, _, _) = PolyDoorTests.TwoRoomsAndADoor();
        history.Record("Make the door slide aside", e => PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout(Double: true)));
        var wad = GameFiles.Require("freedoom2.wad");
        var game = WadSmith.Core.Games.GameCatalog.Get("freedoom2")!;
        var resources = GameResources.Load("freedoom2", null, [new WadSmith.Core.Games.FoundGame(game, wad, new FileInfo(wad).Length)], GameFiles.Require("gzdoom.pk3"))!;
        Assert.NotEmpty(resources.SwitchPictures);
        Assert.False(resources.CanShow(Polyobjects.Anchor));
        var rules = WadSmith.Editor.Checks.Problems.Check(MapGeometry.Build(map.Frame), resources, Look.For("doom"), "doom").Select(p => p.Rule).ToList();
        Assert.DoesNotContain("cannot-show", rules);
        Assert.DoesNotContain("still-switch", rules);

        // A real unknown object and a real switch with no picture are still called out beside them.
        var model = map.Frame.ToModel();
        model.Things.Add(new MapThing { X = 64, Y = 64, Type = 31999 });
        var plain = model.Lines.First(l => l.Back < 0 && l.Special == 0 && model.Sides[l.Front].Middle == "STARTAN3");
        plain.Special = 12;
        plain.Args[0] = 99;
        plain.SetFlag("playeruse", true);
        var both = WadSmith.Editor.Checks.Problems.Check(MapGeometry.Build(EditorMap.FromModel(model).Frame), resources, Look.For("doom"), "doom");
        Assert.Contains(both, p => p.Rule == "cannot-show" && p.Show.Count == 1);
        Assert.Contains(both, p => p.Rule == "still-switch" && p.Show.Count == 1);
    }
}

/// <summary>Sliding and swinging doors in an export (D62): Hexen's format keeps them in its own numbers; Doom's has none and says so.</summary>
public sealed class PolyDoorExportTests
{
    private static MapModel WithSlidingDoor(bool locked)
    {
        var (map, history, area, _, _) = PolyDoorTests.TwoRoomsAndADoor();
        map.Things.Add(new Thing { X = 64, Y = 64, Type = 1 });
        history.Record("Make the door slide aside", e => PolyDoors.FromRising(e, area, DoorKind.Slides, Look.For("doom"), "doom", new PolyDoors.Layout()));
        var door = PolyDoors.Recognise(MapGeometry.Build(map.Frame), area, "doom")!;
        if (locked) history.Record("Lock it", e => PolyDoors.Change(e, door, door.Settings with { Lock = 1, StaysOpen = true }, false));
        return map.ToModel();
    }

    [Fact]
    public void AHexenExportKeepsTheDoorInHexensOwnNumbersAndSaysWhatItLoses()
    {
        var report = new WadSmith.Core.Exporting.ExportReport();
        var lumps = WadSmith.Core.Exporting.MapExport.ForPort(WithSlidingDoor(locked: true), WadSmith.Core.Exporting.ClassicPorts.Get("vanilla")!, "hexen", null, report);
        var wad = WadSmith.Core.Containers.WadContainer.FromBytes(WadSmith.Core.Containers.WadWriter.Write(WadSmith.Core.Containers.WadKind.Pwad, lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))));
        var back = MapConversion.Read(MapLocator.Find(wad).Single(), "hexen");
        Assert.Equal([3000, 3001], back.Things.Where(t => t.Type is >= 3000 and <= 3002 or >= 9300 and <= 9303).Select(t => t.Type).Order());
        Assert.Contains(back.Lines, l => l.Special == 8 && l.Args[4] == 255);
        Assert.Contains(report.Losses, l => l.Title == "Sliding and swinging doors locked with a key");
        Assert.Contains(report.Losses, l => l.Title == "Sliding and swinging doors that stay open");
        Assert.DoesNotContain(report.Losses, l => l.Title.StartsWith("Sliding and swinging doors (polyobjects)", StringComparison.Ordinal));
    }

    [Fact]
    public void ADoomExportSaysItHasNoSlidingDoors()
    {
        var xlat = WadSmith.Core.Translation.XlatTranslator.Load(new WadSmith.Core.Containers.ResourceStack([WadSmith.Core.Containers.ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var report = new WadSmith.Core.Exporting.ExportReport();
        WadSmith.Core.Exporting.MapExport.ForPort(WithSlidingDoor(locked: false), WadSmith.Core.Exporting.ClassicPorts.Get("vanilla")!, "doom", xlat, report);
        var loss = Assert.Single(report.Losses, l => l.Title == "Sliding and swinging doors (polyobjects): their anchors and spots");
        Assert.True(loss.Breaks);
        Assert.Equal(2, loss.Count);
    }
}
