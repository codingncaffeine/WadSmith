using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Translation;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Switches: made on a wall for a door or a lift, recognised in any map from the walls the player uses.</summary>
public sealed class SwitchTests(ITestOutputHelper output)
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    /// <summary>
    /// Doom's switches and buttons (S1 and SR, vanilla P_UseSpecialLine) that work on tagged areas; the exits
    /// (11, 51) have their own view, and the doors opened from their own walls (1, 26–28, 31–34, 117, 118) name no tag.
    /// </summary>
    private static readonly HashSet<int> ClassicSwitches = [7, 9, 14, 15, 18, 20, 21, 23, 29, 41, 49, 50, 55, 71, 101, 102, 103, 111, 112, 113, 122, 127, 131, 133, 135, 137, 140, 42, 43, 45, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 114, 115, 116, 123, 132, 99, 134, 136, 138, 139];

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static MapGeometry Shapes(EditorMap map) => MapGeometry.Build(map.Frame);

    /// <summary>A hall (0–256 square) and a small room east of it behind a door made by the Door tool.</summary>
    private static (EditorMap Map, Editor.Editing.History History, List<Sector> Rooms, Sector Door) HallWithDoor()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 384, 192));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        Sector? door = null;
        history.Record("Make a door", e => door = Doors.Make(e, Shapes(map), shared, Look).Door);
        return (map, history, rooms, door!);
    }

    private static Line WestWall(EditorMap map) => map.Lines.Single(l => l.OnMap && l.Start.X == 0 && l.End.X == 0);

    [Fact]
    public void ASwitchForADoorOpensItByItsTagAsItsOwnWallsDo()
    {
        var (map, history, rooms, door) = HallWithDoor();
        var before = Udmf(map.ToModel());
        var west = WestWall(map);
        (Line? Switch, string? Why) made = (null, null);
        history.Record("Make a switch", e => made = Switches.Make(e, Shapes(map), west, new MapPoint(0, 128), door, Look));
        Assert.Null(made.Why);
        var piece = made.Switch!;
        DrawingTests.AssertSound(map);

        // A piece 64 wide round the click, the rest of the wall on either side of it.
        Assert.Equal(64, piece.Length);
        Assert.Equal((96.0, 160.0), (Math.Min(piece.Start.Y, piece.End.Y), Math.Max(piece.Start.Y, piece.End.Y)));
        Assert.Equal(3, map.Lines.Count(l => l.OnMap && l.Start.X == 0 && l.End.X == 0));
        Assert.Equal((Look.Switch, 0.0, 0.0), (piece.Front.Middle, piece.Front.OffsetX, piece.Front.OffsetY));
        Assert.True(piece.Flag("playeruse") && piece.Flag("repeatspecial") && !piece.Flag("monsteruse"));

        // The door got a tag of its own, and the switch does what the door's walls do, by that tag.
        Assert.Equal(1, door.Tag);
        Assert.Equal(Doors.DoorRaise, piece.Special);
        Assert.Equal([1, 16, 150, 0, 0], piece.Args);
        var sw = Switches.Recognise(Shapes(map), piece)!;
        Assert.Equal("opens a door", sw.Does);
        Assert.Same(door, Assert.Single(sw.Targets));
        Assert.Same(piece, Assert.Single(Switches.Of(Shapes(map), door)));
        Assert.Contains(piece, Doors.Recognise(Shapes(map), door)!.Openers);

        // The door's own walls are not switches.
        Assert.All(Doors.Recognise(Shapes(map), door)!.Openers.Where(l => l != piece), l => Assert.Null(Switches.Recognise(Shapes(map), l)));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void ASwitchForALiftLowersItAndAShortWallIsAllSwitch()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 384, 256), (-64, 96, 0, 160));
        rooms[1].FloorHeight = 64;
        rooms[1].CeilingHeight = 192;
        history.Record("Make a lift", e => Lifts.Make(e, Shapes(map), rooms[1]));
        Assert.NotNull(Lifts.Recognise(Shapes(map), rooms[1]));

        // The alcove's back wall is 64 long: all of it becomes the switch.
        var back = map.Lines.Single(l => l.OnMap && l.Back is null && l.Start.X == -64 && l.End.X == -64);
        (Line? Switch, string? Why) made = (null, null);
        history.Record("Make a switch", e => made = Switches.Make(e, Shapes(map), back, new MapPoint(-64, 100), rooms[1], Look));
        Assert.Same(back, made.Switch);
        Assert.Equal(Lifts.DownWaitUpStayLip, back.Special);
        Assert.Equal([rooms[1].Tag, Lifts.NormalSpeed, Lifts.Wait, 0, 0], back.Args);
        Assert.Equal("lowers a lift", Switches.Recognise(Shapes(map), back)!.Does);
        Assert.Contains(back, Lifts.Recognise(Shapes(map), rooms[1])!.Triggers);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void NoSwitchOnAnOpenWallAWallThatDoesSomethingOrForAPlainRoom()
    {
        var (map, history, rooms, door) = HallWithDoor();
        string? Why(Line wall, Sector target)
        {
            (Line?, string? Why) made = (null, null);
            history.Record("Make a switch", e => made = Switches.Make(e, Shapes(map), wall, new MapPoint(wall.Start.X, wall.Start.Y), target, Look));
            return made.Why;
        }

        var opener = Doors.Recognise(Shapes(map), door)!.Openers[0];
        Assert.Contains("solid wall", Why(opener, door));
        Assert.Contains("door or a lift", Why(WestWall(map), rooms[1]));
        Assert.Contains("outside it", Why(map.Lines.First(l => l.OnMap && l.Back is null && l.Front.Sector == door), door));
        Assert.Contains("outside it", Why(WestWall(map), rooms[0]));

        // A wall that already does something.
        var west = WestWall(map);
        west.Special = 80;
        Assert.Contains("already does something", Why(west, door));
        Assert.Equal(0, door.Tag);
    }

    /// <summary>
    /// The control is the map as the game shipped it: lines with a switch or button type and a tag are its switches.
    /// The recogniser sees only the translated map (GZDoom's actions and activation flags).
    /// </summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void EveryClassicSwitchIsRecognisedAndNothingElse(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var (switches, targets, maps) = (0, 0, 0);
        foreach (var lumps in MapLocator.Find(wad))
        {
            var classic = MapConversion.Read(lumps, "doom");
            var expected = Enumerable.Range(0, classic.Lines.Count).Where(i => ClassicSwitches.Contains(classic.Lines[i].Special) && classic.Lines[i].Id > 0).ToHashSet();
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(classic, xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            var found = map.Lines.Select(l => Switches.Recognise(geometry, l)).OfType<Switch>().ToList();
            var slots = found.Select(s => s.Wall.Slot).ToHashSet();
            Assert.True(expected.SetEquals(slots), $"{fileName} {lumps.Name}: missed {string.Join(", ", expected.Except(slots).Select(i => $"{i} (type {classic.Lines[i].Special})"))}; not switches {string.Join(", ", slots.Except(expected).Select(i => $"{i} (type {classic.Lines[i].Special})"))}");
            switches += found.Count;
            targets += found.Count(s => s.Targets.Count > 0);
            maps++;
        }

        output.WriteLine($"{fileName}: {switches} switches in {maps} maps, all recognised, nothing else; {targets} work on areas that exist");
    }
}
