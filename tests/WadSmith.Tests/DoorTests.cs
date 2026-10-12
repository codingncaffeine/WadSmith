using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Doors: found in any map from their walls' actions, and changed as one thing.</summary>
public sealed class DoorTests(ITestOutputHelper output)
{
    /// <summary>Doom's classic door actions that open the area behind the wall used (no tag): DR, D1, keyed and blazing.</summary>
    private static readonly HashSet<int> ClassicLocalDoors = [1, 26, 27, 28, 31, 32, 33, 34, 117, 118];

    /// <summary>
    /// Doom's classic actions that open doors by tag (switches, lines walked over or shot): W1/WR/S1/SR/GR open
    /// and raise, blazing and locked blazing. Closing ones (2, 3, 16, …) do not make a door.
    /// </summary>
    private static readonly HashSet<int> ClassicRemoteDoors = [2, 4, 29, 46, 61, 63, 86, 90, 99, 103, 105, 106, 108, 109, 111, 112, 114, 115, 133, 134, 135, 136, 137];

    public static TheoryData<string> DoomFormatGames() => new() { "DOOM.WAD", "DOOM2.WAD", "PLUTONIA.WAD", "TNT.WAD", "SIGIL_v1_21.wad", "SIGIL_II_V1_0.WAD", "freedoom1.wad", "freedoom2.wad" };

    /// <summary>
    /// The control is the map as the game shipped it: the areas behind walls with a classic local door
    /// action are its doors. The recogniser sees only the translated map (GZDoom's actions), so the two
    /// agree only if recognition is right: every door found (recall) and nothing else (precision).
    /// </summary>
    [Theory]
    [MemberData(nameof(DoomFormatGames))]
    public void EveryClassicDoorIsRecognisedAndNothingElse(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var (doors, maps) = (0, 0);
        foreach (var lumps in MapLocator.Find(wad))
        {
            var classic = MapConversion.Read(lumps, "doom");
            var expected = classic.Lines.Where(l => ClassicLocalDoors.Contains(l.Special) && l.Back >= 0).Select(l => classic.Sides[l.Back].Sector).ToHashSet();

            // Remote doors: every area with the tag a remote door action names. With no tag, GZDoom (unlike
            // the original game) works on the area behind the wall, so the translated map does too.
            foreach (var l in classic.Lines.Where(l => ClassicRemoteDoors.Contains(l.Special)))
            {
                var tag = Math.Max(l.Id, 0);
                if (tag == 0 && l.Back >= 0) expected.Add(classic.Sides[l.Back].Sector);
                for (var s = 0; s < classic.Sectors.Count; s++)
                {
                    if (tag != 0 && classic.Sectors[s].Id == tag) expected.Add(s);
                }
            }

            var map = EditorMap.FromModel(MapTranslation.ToZDoom(classic, xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            var found = map.Sectors.Where(s => Doors.Recognise(geometry, s) is not null).Select(s => s.Slot).ToHashSet();
            var missed = expected.Except(found).Order().ToList();
            var wrong = found.Except(expected).Order().ToList();
            Assert.True(missed.Count == 0 && wrong.Count == 0, $"{fileName} {lumps.Name}: missed doors {string.Join(", ", missed)}; not doors {string.Join(", ", wrong)}");
            doors += found.Count;
            maps++;
        }

        output.WriteLine($"{fileName}: {doors} doors in {maps} maps, all recognised, nothing else");
        Assert.True(doors > maps, $"{fileName}: only {doors} doors in {maps} maps");
    }

    [Fact]
    public void ADoorsSettingsGoThroughItsActionAndBack()
    {
        foreach (var settings in new[] { new DoorSettings(false, false, 0), new DoorSettings(true, false, 0), new DoorSettings(false, true, 0), new DoorSettings(false, false, 130), new DoorSettings(true, true, 131) })
        {
            var (special, args) = Doors.Encode(settings, lightTag: 7);
            Assert.Equal(settings, Doors.Decode(special, args));
            Assert.Equal(7, Doors.LightTag(special, args));
            Assert.Equal(0, args[0]);
        }

        // Doom's DR door, as the engine translates it.
        var (dr, drArgs) = Doors.Encode(new DoorSettings(false, false, 0), 0);
        Assert.Equal(Doors.DoorRaise, dr);
        Assert.Equal([0, 16, 150, 0, 0], drArgs);
    }

    /// <summary>A room between two rooms, its walls facing them opening it as Doom's DR door does (closed: ceiling on the floor).</summary>
    private static (EditorMap Map, History History, Sector Door) Doorway()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 32, 144, 96), (144, 0, 272, 128));
        var area = rooms[1];
        history.Record("Make a door", e =>
        {
            e.Set(area, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, area.FloorHeight);
            foreach (var l in map.Lines.Where(l => l.OnMap && l.Back?.Sector == area || (l.OnMap && l.Front.Sector == area && l.Back is not null)))
            {
                // Each wall between the door and a room faces the room, so using it from the room opens the door.
                if (l.Front.Sector == area)
                {
                    var (s, en, f, b) = (l.Start, l.End, l.Front, l.Back!);
                    e.Set(l, x => x.Start, (x, v) => x.Start = v, en);
                    e.Set(l, x => x.End, (x, v) => x.End = v, s);
                    e.Set(l, x => x.Back, (x, v) => x.Back = v, null);
                    e.Set(l, x => x.Front, (x, v) => x.Front = v, b);
                    e.Set(l, x => x.Back, (x, v) => x.Back = v, f);
                }

                e.Set(l, x => x.Special, (x, v) => x.Special = v, Doors.DoorRaise);
                e.Set(l, x => x.Args[1], (x, v) => x.Args[1] = v, 16);
                e.Set(l, x => x.Args[2], (x, v) => x.Args[2] = v, 150);
                e.Flag(l, "playeruse", true);
            }
        });
        return (map, history, area);
    }

    [Fact]
    public void ADoorBetweenTwoRoomsIsFoundWithBothItsWallsAndOpensBelowTheLowerCeiling()
    {
        var (map, _, area) = Doorway();
        var door = Doors.Recognise(MapGeometry.Build(map.Frame), area);
        Assert.NotNull(door);
        Assert.Equal(2, door.Openers.Count);
        Assert.Equal(124, door.OpensTo);
        Assert.False(door.StartsOpen);
        Assert.Equal(new DoorSettings(false, false, 0), door.Settings);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ChangingADoorChangesEveryWallThatOpensItInOneStep()
    {
        var (map, history, area) = Doorway();
        var door = Doors.Recognise(MapGeometry.Build(map.Frame), area)!;
        history.Record("Change the door", e => Doors.Change(e, door, new DoorSettings(true, false, 129), monsters: false, startsOpen: true));
        var changed = Doors.Recognise(MapGeometry.Build(map.Frame), area)!;
        Assert.Equal(new DoorSettings(true, false, 129), changed.Settings);
        Assert.All(changed.Openers, l => Assert.Equal((Doors.DoorLockedRaise, 129), (l.Special, l.Args[3])));
        Assert.True(changed.StartsOpen);
        Assert.Equal(124, area.CeilingHeight);
        history.Undo();
        Assert.Equal(new DoorSettings(false, false, 0), Doors.Recognise(MapGeometry.Build(map.Frame), area)!.Settings);
        Assert.False(area.CeilingHeight > area.FloorHeight);
    }

    [Fact]
    public void ASwitchElsewhereNamingTheDoorsTagOpensItAndKeepsItsTag()
    {
        var (map, history, area) = Doorway();
        var room = map.Sectors.First(s => s.OnMap && s != area);
        var wall = map.Lines.First(l => l.OnMap && l.Back is null && l.Front.Sector == room);
        history.Record("Make a switch", e =>
        {
            e.Set(area, s => s.Tag, (s, v) => s.Tag = v, 5);
            e.Set(wall, x => x.Special, (x, v) => x.Special = v, Doors.DoorOpen);
            e.Set(wall, x => x.Args[0], (x, v) => x.Args[0] = v, 5);
            e.Set(wall, x => x.Args[1], (x, v) => x.Args[1] = v, 16);
            e.Flag(wall, "playeruse", true);
        });
        var door = Doors.Recognise(MapGeometry.Build(map.Frame), area)!;
        Assert.Equal(3, door.Openers.Count);
        Assert.Same(wall, Assert.Single(door.Remote));
        Assert.Equal(["a switch"], door.RemoteWays());

        history.Record("Change the door", e => Doors.Change(e, door, new DoorSettings(true, true, 0), monsters: true, startsOpen: false));
        Assert.Equal((Doors.DoorOpen, 5, 64), (wall.Special, wall.Args[0], wall.Args[1]));
        Assert.All(door.Own, l => Assert.Equal((Doors.DoorOpen, 0), (l.Special, l.Args[0])));
    }

    [Fact]
    public void AnOrdinaryRoomIsNotADoor()
    {
        var (map, _, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var geometry = MapGeometry.Build(map.Frame);
        Assert.All(rooms, r => Assert.Null(Doors.Recognise(geometry, r)));
    }
}
