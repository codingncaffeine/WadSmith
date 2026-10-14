using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>A door's look: the game's own door pictures to choose from, and a doorway that follows the chosen picture's width and height.</summary>
public sealed class DoorLooksTests
{
    private static readonly ResourceContainer Nowhere = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, []));

    private static TextureInfo Wall(string name, int width, int height) => new(name, TextureKind.Composite, width, height, "test", Nowhere);

    [Fact]
    public void TheLooksAreTheFamilysOwnDoorsNotTracksOrTrims()
    {
        TextureInfo[] doom = [Wall("DOOR1", 64, 72), Wall("DOOR3", 64, 72), Wall("BIGDOOR2", 128, 128), Wall("EXITDOOR", 128, 72), Wall("SPCDOOR1", 64, 128), Wall("DOORTRAK", 8, 128), Wall("DOORBLU", 8, 128), Wall("DOORSTOP", 8, 128), Wall("STARTAN3", 128, 128)];
        Assert.Equal(["BIGDOOR2", "DOOR1", "DOOR3", "EXITDOOR", "SPCDOOR1"], Doors.Looks("doom", doom).Select(t => t.Name));
        Assert.Equal(["BIGDOOR2", "DOOR1", "DOOR3", "EXITDOOR", "SPCDOOR1"], Doors.Looks("chex", doom).Select(t => t.Name));
        TextureInfo[] heretic = [Wall("DOORWOOD", 64, 128), Wall("DOORSTON", 64, 128), Wall("GRSTNPB", 64, 128)];
        Assert.Equal(["DOORSTON", "DOORWOOD"], Doors.Looks("heretic", heretic).Select(t => t.Name));
        TextureInfo[] hexen = [Wall("D_WD01", 64, 128), Wall("D_BRASS1", 64, 128), Wall("FOREST01", 64, 128)];
        Assert.Equal(["D_BRASS1", "D_WD01"], Doors.Looks("hexen", hexen).Select(t => t.Name));
        TextureInfo[] strife = [Wall("DORMS01", 64, 128), Wall("DORTRK01", 8, 128), Wall("BRKGRY01", 64, 128)];
        Assert.Equal(["DORMS01"], Doors.Looks("strife", strife).Select(t => t.Name));
    }

    [Fact]
    public void AFramelessDoorsLookLeavesTheRoomsBesideItAlone()
    {
        // A door made with no frames (no picture sizes known), straight between two rooms 128 tall: the rooms are not frames,
        // and a short picture must not bring their ceilings down to its height (the user's north door, 2026-10-12).
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Look.For("doom"), Doors.Centred(opening, Doors.StandardWidth), fill: true));
        var door = Doors.Recognise(MapGeometry.Build(map.Frame), made.Door!)!;
        Assert.Empty(Doors.FramesOf(MapGeometry.Build(map.Frame), door));

        history.Record("Change the door's look", e => Doors.Dress(e, MapGeometry.Build(map.Frame), door, "DOOR1", (64, 72)));
        Assert.All(door.Own, l => Assert.Equal("DOOR1", l.Front.Top));
        Assert.All(rooms, r => Assert.Equal(128, r.CeilingHeight - r.FloorHeight));
    }

    [Fact]
    public void DressingADoorChangesItsPictureAndItsDoorwayFollowsThePicturesHeight()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        var look = Look.For("doom");
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, look, null, fill: true, heightOf: t => t == look.Door ? 72 : null));
        var door = made.Door!;
        var frames = map.Sectors.Where(s => s.OnMap && !rooms.Contains(s) && !ReferenceEquals(s, door)).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(72, f.CeilingHeight - f.FloorHeight));
        var found = Doors.Recognise(MapGeometry.Build(map.Frame), door)!;
        Assert.Equal(2, Doors.FramesOf(MapGeometry.Build(map.Frame), found).Count);

        // A big door, as tall as the room: the frames open up to the room's ceiling and the faces show it.
        history.Record("Change the door's look", e => Doors.Dress(e, MapGeometry.Build(map.Frame), found, "BIGDOOR2", (128, 128)));
        Assert.All(found.Own, l => Assert.Equal(("BIGDOOR2", 0.0), (l.Front.Top, l.Front.OffsetY)));
        Assert.All(frames, f => Assert.Equal(128, f.CeilingHeight - f.FloorHeight));
        Assert.Equal(124, Doors.Recognise(MapGeometry.Build(map.Frame), door)!.OpensTo);

        // Back to a short door: the frames come down to its height again, and one undo each way.
        history.Record("Change the door's look", e => Doors.Dress(e, MapGeometry.Build(map.Frame), found, "DOOR1", (64, 72)));
        Assert.All(found.Own, l => Assert.Equal("DOOR1", l.Front.Top));
        Assert.All(frames, f => Assert.Equal(72, f.CeilingHeight - f.FloorHeight));
        history.Undo();
        Assert.All(frames, f => Assert.Equal(128, f.CeilingHeight - f.FloorHeight));
        Assert.All(found.Own, l => Assert.Equal("BIGDOOR2", l.Front.Top));
    }

    /// <summary>The faces' length (the door's width) and the middle of the first along the shared wall.</summary>
    private static (double Width, double Middle) Faces(Door door) =>
        (door.Own.First().Length, (door.Own.First().Start.Y + door.Own.First().End.Y) / 2);

    /// <summary>
    /// The user's report (2026-10-14): a door clicked onto a wall is 64 wide, and a big door's picture (128 wide) chosen for it
    /// showed half of itself. The doorway now follows the picture's width as well, about its middle, the wall either side giving
    /// way; a narrower picture gives the wall back, and each is one undo step. The map stays whole and the door stays a door.
    /// </summary>
    [Fact]
    public void ABigDoorsPictureMakesTheDoorwayAsWideAsItself()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        var look = Look.For("doom");
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, look, Doors.Centred(opening, Doors.StandardWidth), fill: true, heightOf: t => t == look.Door ? 72 : 128));
        Door Now() => Doors.Recognise(MapGeometry.Build(map.Frame), made.Door!)!;
        Assert.Equal((64.0, 128.0), Faces(Now()));
        Assert.Equal(2, Doors.FramesOf(MapGeometry.Build(map.Frame), Now()).Count);

        string? why = "";
        history.Record("Change the door's look", e => why = Doors.Dress(e, MapGeometry.Build(map.Frame), Now(), "BIGDOOR2", (128, 128)));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
        var big = Now();
        Assert.Equal((128.0, 128.0), Faces(big));
        Assert.All(big.Own, l => Assert.Equal("BIGDOOR2", l.Front.Top));
        Assert.Equal(2, big.Own.Count());
        var frames = Doors.FramesOf(MapGeometry.Build(map.Frame), big);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal((128.0, 128.0), (f.Frame.CeilingHeight - f.Frame.FloorHeight, MapGeometry.Build(map.Frame).WallBoundsAt(f.Frame.Slot)!.Value is var b ? b.MaxY - b.MinY : 0)));
        Assert.Equal(124, big.OpensTo);
        Assert.All(rooms, r => Assert.Equal(128, r.CeilingHeight - r.FloorHeight));

        // Back to Doom's small door: 64 wide again, the wall either side grown back; undo makes it big once more.
        history.Record("Change the door's look", e => why = Doors.Dress(e, MapGeometry.Build(map.Frame), Now(), "DOOR1", (64, 72)));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
        Assert.Equal((64.0, 128.0), Faces(Now()));
        history.Undo();
        Assert.Equal((128.0, 128.0), Faces(Now()));
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void APictureWiderThanTheWallHasRoomForGetsTheWidestDoorThatFitsAndSaysSo()
    {
        // A 128 wall between the rooms: the door's 64 leaves 32 of wall either side, of which 8 stay: 112 at most.
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 512, 192));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Look.For("doom"), Doors.Centred(opening, Doors.StandardWidth), fill: true));
        Door Now() => Doors.Recognise(MapGeometry.Build(map.Frame), made.Door!)!;
        Assert.Equal(64, Faces(Now()).Width);

        string? why = null;
        history.Record("Change the door's look", e => why = Doors.Dress(e, MapGeometry.Build(map.Frame), Now(), "BIGDOOR2", (128, 128)));
        Assert.Equal("The picture is 128 wide; this wall has room for a door 112 wide, which it is now. Choose a narrower picture, or make the wall longer.", why);
        Assert.Equal((112.0, 128.0), Faces(Now()));
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ADoorFillingItsOpeningKeepsItsWidthAndSaysWhy()
    {
        // A corridor 64 wide: the door is its whole end, its sides the corridor's own walls.
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Look.For("doom"), null, fill: true));
        var door = Doors.Recognise(MapGeometry.Build(map.Frame), made.Door!)!;
        string? why = null;
        history.Record("Change the door's look", e => why = Doors.Dress(e, MapGeometry.Build(map.Frame), door, "BIGDOOR2", (128, 128)));
        Assert.Equal("The picture is 128 wide and the door 64: the door's sides are rooms' walls, which it does not move. Make its opening 128 wide, or choose a picture 64 wide.", why);
        Assert.Equal(64, Faces(Doors.Recognise(MapGeometry.Build(map.Frame), made.Door!)!).Width);
        Assert.All(door.Own, l => Assert.Equal("BIGDOOR2", l.Front.Top));
        DrawingTests.AssertSound(map);
    }
}
