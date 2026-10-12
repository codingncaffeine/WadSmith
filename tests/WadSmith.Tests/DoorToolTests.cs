using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>The Door tool: a click on a wall between two rooms makes Doom's own kind of door there.</summary>
public sealed class DoorToolTests
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static List<Line> WallList(EditorMap map) => [.. map.Lines.Where(l => l.OnMap)];

    private static (Sector? Door, string? Why) Make(EditorMap map, History history, Line wall)
    {
        (Sector?, string?) result = (null, null);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a door", e => result = Doors.Make(e, geometry, wall, Look));
        return result;
    }

    /// <summary>Two rooms joined by a corridor 64 wide (y 96–160), 128 long (x 256–384).</summary>
    private static (EditorMap Map, History History, List<Sector> Rooms) RoomsAndCorridor() => MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160), (384, 0, 640, 256));

    [Fact]
    public void ADoorAtACorridorsEndIsDoomsKindOfDoor()
    {
        var (map, history, rooms) = RoomsAndCorridor();
        var before = Udmf(map.ToModel());
        var wall = WallList(map).Single(l => l.Start.X == 256 && l.End.X == 256 && l.Back is not null);
        var (area, why) = Make(map, history, wall);
        Assert.Null(why);
        Assert.NotNull(area);
        DrawingTests.AssertSound(map);

        // Flush in the corridor, 16 deep, closed.
        var b = MapGeometry.Build(map.Frame).WallBoundsAt(area.Slot)!.Value;
        Assert.Equal((256.0, 96.0, 272.0, 160.0), b);
        Assert.Equal(area.FloorHeight, area.CeilingHeight);

        // Found as a door, opened from both rooms by players and monsters, again and again, closing after a while.
        var door = Doors.Recognise(MapGeometry.Build(map.Frame), area);
        Assert.NotNull(door);
        Assert.Equal(2, door.Openers.Count);
        Assert.Equal(new DoorSettings(false, false, 0), door.Settings);
        Assert.True(door.Monsters);
        Assert.All(door.Openers, l => Assert.True(l.Flag("playeruse") && l.Flag("repeatspecial") && l.Front.Top == "DOOR3"));
        Assert.Contains(door.Openers, l => l.Front.Sector == rooms[0]);
        Assert.Contains(door.Openers, l => l.Front.Sector == rooms[1]);

        // Its sides are the tracks, anchored so they stay put while the door moves.
        var tracks = WallList(map).Where(l => l.Front.Sector == area && l.Back is null).ToList();
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, l => Assert.True(l.Front.Middle == "DOORTRAK" && l.Flag("dontpegbottom")));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void AWideDoorGetsTheBigDoorTexture()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 384, 192), (384, 0, 640, 256));
        var wall = WallList(map).Single(l => l.Start.X == 384 && l.End.X == 384 && l.Back is not null);
        var (area, _) = Make(map, history, wall);
        Assert.NotNull(area);
        Assert.All(Doors.Recognise(MapGeometry.Build(map.Frame), area)!.Openers, l => Assert.Equal("BIGDOOR2", l.Front.Top));
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void AWallTooLongForADoorIsRefusedWithAWayForward()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 512, 512), (512, 0, 1024, 512));
        var before = Udmf(map.ToModel());
        var (area, why) = Make(map, history, WallList(map).Single(l => l.Back is not null));
        Assert.Null(area);
        Assert.Contains("corridor", why);
        Assert.Equal(before, Udmf(map.ToModel()));
        Assert.Equal("Draw a room", history.UndoLabel);
    }

    [Fact]
    public void ASolidWallIsNoPlaceForADoor()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128));
        var (area, why) = Make(map, history, WallList(map)[0]);
        Assert.Null(area);
        Assert.Contains("between two rooms", why);
    }

    [Fact]
    public void ADoorsOwnWallMakesNoSecondDoor()
    {
        var (map, history, _) = RoomsAndCorridor();
        var wall = WallList(map).Single(l => l.Start.X == 256 && l.End.X == 256 && l.Back is not null);
        Make(map, history, wall);
        var (area, why) = Make(map, history, wall);
        Assert.Null(area);
        Assert.Contains("already", why);
    }
}
