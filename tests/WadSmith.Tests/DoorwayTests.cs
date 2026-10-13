using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A door in a room taller than its picture gets a doorway as tall as the picture — a frame either side of the door with the
/// wall above — so the picture does not repeat above the door (a first map, 2026-10-12: "instead of wall being above the
/// door it's a repeating texture of the door").
/// </summary>
public sealed class DoorwayTests
{
    private static readonly Look Doom = Look.For("doom");

    /// <summary>The door pictures' heights as Doom's are: the 64-wide door 72 tall, the big door 128.</summary>
    private static double? Heights(string texture) => texture == Doom.Door ? 72 : texture == Doom.BigDoor ? 128 : null;

    private static Line Opening(EditorMap map) => Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);

    private static List<Sector> Between(EditorMap map, IEnumerable<Sector> rooms) => map.Sectors.Where(s => s.OnMap && !rooms.Contains(s)).ToList();

    private static (double MinX, double MaxX) Across(EditorMap map, Sector area)
    {
        var b = MapGeometry.Build(map.Frame).WallBoundsAt(area.Slot)!.Value;
        return (b.MinX, b.MaxX);
    }

    [Fact]
    public void ADoorInATallRoomGetsADoorwayAsTallAsItsPicture()
    {
        // A corridor 64 wide east of a room, both 128 tall: the door's picture is 72.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160));
        var opening = Opening(map);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Doom, null, fill: true, heightOf: Heights));
        DrawingTests.AssertSound(map);
        Assert.Null(made.Why);
        var door = made.Door!;

        // Frame, door, frame along the corridor: 8, 16 and 8 deep; the frames 72 tall, the door closed.
        var slabs = Between(map, rooms);
        Assert.Equal(3, slabs.Count);
        var frames = slabs.Where(s => !ReferenceEquals(s, door)).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(72, f.CeilingHeight - f.FloorHeight));
        Assert.Equal(door.FloorHeight, door.CeilingHeight);
        Assert.Equal((264.0, 280.0), Across(map, door));
        Assert.Equal([(256.0, 264.0), (280.0, 288.0)], frames.Select(f => Across(map, f)).OrderBy(b => b.MinX));

        // The door's faces: a frame in front, the door behind, the picture hung from the door's top edge (so it rises with it).
        var faces = map.Lines.Where(l => l.OnMap && l.Back is not null && ReferenceEquals(l.Back.Sector, door)).ToList();
        Assert.Equal(2, faces.Count);
        Assert.All(faces, f =>
        {
            Assert.Contains(f.Front.Sector, frames);
            Assert.Equal((Doom.Door, Doors.DoorRaise, false, true), (f.Front.Top, f.Special, f.Flag("dontpegtop"), f.Flag("playeruse")));
        });

        // The frames against the rooms: the wall above the door, hung from the ceiling like the walls beside it.
        var lintels = map.Lines.Where(l => l.OnMap && l.Back is not null && ((rooms.Contains(l.Front.Sector) && frames.Contains(l.Back.Sector)) || (rooms.Contains(l.Back.Sector) && frames.Contains(l.Front.Sector)))).ToList();
        Assert.Equal(2, lintels.Count);
        Assert.All(lintels, l =>
        {
            var roomSide = rooms.Contains(l.Front.Sector) ? l.Front : l.Back!;
            Assert.Equal((Doom.Wall, true, 0), (roomSide.Top, l.Flag("dontpegtop"), l.Special));
        });

        // The frames' sides in the corridor's walls hang their texture from the floor, in line with the wall beside them.
        var sides = map.Lines.Where(l => l.OnMap && l.Back is null && frames.Contains(l.Front.Sector)).ToList();
        Assert.Equal(4, sides.Count);
        Assert.All(sides, s => Assert.True(s.Flag("dontpegbottom")));

        // It is a door, opening to 4 below the frames' ceiling: the player (56) still fits.
        var found = Doors.Recognise(MapGeometry.Build(map.Frame), door);
        Assert.NotNull(found);
        Assert.Equal(68, found.OpensTo);
        Assert.Equal(2, found.Openers.Count);

        // One undo takes the doorway back.
        history.Undo();
        Assert.Equal(2, map.Sectors.Count(s => s.OnMap));
        Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
    }

    [Fact]
    public void ADoorInAWallGetsItsDoorwayToo()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var opening = Opening(map);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Doom, Doors.Centred(opening, Doors.StandardWidth), fill: true, heightOf: Heights));
        DrawingTests.AssertSound(map);
        Assert.Null(made.Why);
        var door = made.Door!;

        // Wall, then frame · door · frame, then wall: five slabs, the walls as deep as the doorway. The walls are closed up on
        // the room's ceiling, not on the floor: the engine opens a door to the lowest ceiling beside it, and a slab closed on the
        // floor beside the door would keep it shut.
        var slabs = Between(map, rooms);
        Assert.Equal(5, slabs.Count);
        var frames = slabs.Where(s => s.CeilingHeight > s.FloorHeight).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(72, f.CeilingHeight - f.FloorHeight));
        var walls = slabs.Where(s => !frames.Contains(s) && !ReferenceEquals(s, door)).ToList();
        Assert.Equal(2, walls.Count);
        Assert.All(walls, w => Assert.Equal((32.0, rooms[0].CeilingHeight, rooms[0].CeilingHeight), (Across(map, w).MaxX - Across(map, w).MinX, w.FloorHeight, w.CeilingHeight)));
        Assert.Equal(door.FloorHeight, door.CeilingHeight);

        // The door's sides against the wall pieces show the track below the wall's raised floor; it opens to 68.
        var doorSides = map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, door) || ReferenceEquals(l.Back.Sector, door)) && walls.Contains(ReferenceEquals(l.Front.Sector, door) ? l.Back.Sector : l.Front.Sector)).ToList();
        Assert.Equal(2, doorSides.Count);
        Assert.All(doorSides, s => Assert.Equal(Doom.Track, (ReferenceEquals(s.Front.Sector, door) ? s.Front : s.Back!).Bottom));
        Assert.Equal(68, Doors.Recognise(MapGeometry.Build(map.Frame), door)!.OpensTo);

        // The room's faces on the wall pieces show the wall, as they did when the pieces were closed on the floor.
        var wallFaces = map.Lines.Where(l => l.OnMap && l.Back is not null && (walls.Contains(l.Front.Sector) || walls.Contains(l.Back.Sector)) && (rooms.Contains(l.Front.Sector) || rooms.Contains(l.Back.Sector))).ToList();
        Assert.Equal(4, wallFaces.Count);
        Assert.All(wallFaces, f => Assert.Equal((Doom.Wall, Doom.Wall), ((rooms.Contains(f.Front.Sector) ? f.Front : f.Back!).Top, (rooms.Contains(f.Front.Sector) ? f.Front : f.Back!).Bottom)));
    }

    [Fact]
    public void ADoorAsTallAsTheRoomOrWithNoRoomForFramesIsTheDoorAlone()
    {
        // A 128-wide opening takes the big door, 128 tall in a 128 room: no frame needed.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 512, 192));
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), Opening(map), Doom, null, fill: true, heightOf: Heights));
        Assert.Null(made.Why);
        var only = Assert.Single(Between(map, rooms));
        Assert.Same(made.Door, only);
        Assert.Equal((256.0, 272.0), Across(map, only));

        // A corridor too short for the frames: the door alone, 16 deep, rather than no door.
        var (shortMap, shortHistory, shortRooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 280, 160));
        shortHistory.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(shortMap.Frame), Opening(shortMap), Doom, null, fill: true, heightOf: Heights));
        Assert.Null(made.Why);
        var alone = Assert.Single(Between(shortMap, shortRooms));
        Assert.Equal(alone.FloorHeight, alone.CeilingHeight);
        Assert.Equal((256.0, 272.0), Across(shortMap, alone));
    }
}
