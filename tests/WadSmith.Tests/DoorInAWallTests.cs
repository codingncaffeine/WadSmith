using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A door between two rooms is a door in a wall: on an opening wider than a door, the door goes in the middle and the
/// rest of the opening is closed with wall either side (a first map, 2026-10-12: "no wall, just a stand alone door").
/// </summary>
public sealed class DoorInAWallTests
{
    private static Line Opening(EditorMap map) => Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);

    private static List<Sector> SlabsBetween(EditorMap map, IEnumerable<Sector> rooms) => map.Sectors.Where(s => s.OnMap && !rooms.Contains(s)).ToList();

    private static IEnumerable<Line> FacesOf(EditorMap map, Sector slab, IEnumerable<Sector> rooms) =>
        map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, slab) || ReferenceEquals(l.Back.Sector, slab)) && rooms.Contains(ReferenceEquals(l.Front.Sector, slab) ? l.Back.Sector : l.Front.Sector));

    [Fact]
    public void ADoorOnAWideOpeningIsADoorInAWall()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var opening = Opening(map);
        var look = Look.For("doom");
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, look, Doors.Centred(opening, Doors.StandardWidth), fill: true));
        DrawingTests.AssertSound(map);
        Assert.Null(made.Why);
        var door = made.Door!;
        Assert.NotNull(Doors.Recognise(MapGeometry.Build(map.Frame), door));

        // Three slabs between the rooms, every one closed (ceiling on floor): wall 96, door 64, wall 96.
        var slabs = SlabsBetween(map, rooms);
        Assert.Equal(3, slabs.Count);
        Assert.All(slabs, s => Assert.Equal(s.FloorHeight, s.CeilingHeight));
        var walls = slabs.Where(s => !ReferenceEquals(s, door)).ToList();
        Assert.Equal(2, walls.Count);
        foreach (var piece in walls)
        {
            var faces = FacesOf(map, piece, rooms).ToList();
            Assert.Equal(2, faces.Count);
            Assert.All(faces, f => Assert.Equal(96, f.Length));
            Assert.All(faces, f => Assert.Equal(look.Wall, (rooms.Contains(f.Front.Sector) ? f.Front : f.Back!).Top));
            Assert.All(faces, f => Assert.Equal(0, f.Special));
        }

        var doorFaces = FacesOf(map, door, rooms).ToList();
        Assert.Equal(2, doorFaces.Count);
        Assert.All(doorFaces, f => Assert.Equal(64, f.Length));
        Assert.All(doorFaces, f => Assert.Equal(Doors.DoorRaise, f.Special));

        // One undo takes the door and its walls back.
        history.Undo();
        Assert.Equal(2, map.Sectors.Count(s => s.OnMap));
        Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
    }

    [Fact]
    public void AnOpeningNoWiderThanADoorIsFilledByTheDoorAlone()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160));
        var opening = Opening(map);
        Assert.Equal(64, opening.Length);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, Look.For("doom"), null, fill: true));
        Assert.Null(made.Why);
        Assert.Single(SlabsBetween(map, rooms));
    }

    [Fact]
    public void WallItOffClosesTheWholeOpeningWithNoDoor()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var opening = Opening(map);
        var look = Look.For("doom");
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Wall it off", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), opening, look, null, fill: false, door: false));
        DrawingTests.AssertSound(map);
        Assert.Equal((null, null), made);
        var slab = Assert.Single(SlabsBetween(map, rooms));
        Assert.Equal(slab.FloorHeight, slab.CeilingHeight);
        var faces = FacesOf(map, slab, rooms).ToList();
        Assert.Equal(2, faces.Count);
        Assert.All(faces, f => Assert.Equal(256, f.Length));
        Assert.All(faces, f => Assert.Equal(look.Wall, (rooms.Contains(f.Front.Sector) ? f.Front : f.Back!).Top));
        Assert.DoesNotContain(map.Lines, l => l.OnMap && Doors.Decode(l.Special, l.Args) is not null);
        Assert.Null(Doors.Recognise(MapGeometry.Build(map.Frame), slab));
    }
}
