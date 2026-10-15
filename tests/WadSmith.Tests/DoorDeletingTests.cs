using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Deleting a door leaves the wall it stood in, like the wall beside it (the user's report, 2026-10-15), and the Door tool takes
/// a new door through that wall: whichever part of the door is selected, with or without wall beside it.
/// </summary>
public sealed class DoorDeletingTests
{
    private static readonly Look Look = Look.For("doom");

    private static string Udmf(EditorMap map) => UdmfText.WriteString(MapConversion.ToUdmf(map.ToModel()));

    private static (Sector? Door, string? Why) MakeDoor(EditorMap map, History history, Line wall, (double, double)? span)
    {
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, MapGeometry.Build(map.Frame), wall, Look, span, fill: span is not null, heightOf: _ => 72));
        DrawingTests.AssertSound(map);
        return made;
    }

    /// <summary>Deletes the door one of whose parts is <paramref name="part"/>, checks the map is sound and undo gives it back exactly.</summary>
    private static void DeleteDoor(EditorMap map, History history, Element part)
    {
        var geometry = MapGeometry.Build(map.Frame);
        var doorway = Doors.DoorwayOf(geometry, part, "doom") ?? throw new InvalidOperationException("Not a part of a door.");
        var before = Udmf(map);
        history.Record("Delete the door", e => Doors.WallUp(e, geometry, doorway, Doors.FramesBeside(geometry, doorway), Look));
        DrawingTests.AssertSound(map);
        var after = Udmf(map);
        history.Undo();
        Assert.Equal(before, Udmf(map));
        history.Redo();
        Assert.Equal(after, Udmf(map));
    }

    private static List<Sector> Between(EditorMap map, IReadOnlyList<Sector> rooms) => [.. map.Sectors.Where(s => s.OnMap && !rooms.Contains(s))];

    private static List<Line> FacesToward(EditorMap map, Sector room) =>
        [.. map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back.Sector, room)))];

    [Fact]
    public void ADoorInAWallDeletedByAnyOfItsPartsLeavesOneWallLikeTheWallBeside()
    {
        foreach (var which in new[] { "area", "frame", "face", "side", "frame's wall" })
        {
            var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
            var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
            var door = MakeDoor(map, history, opening, Doors.Centred(opening, Doors.StandardWidth)).Door!;
            var geometry = MapGeometry.Build(map.Frame);
            var found = Doors.Recognise(geometry, door)!;
            var frame = Doors.FramesBeside(geometry, door)[0];
            var beside = FacesToward(map, rooms[0]).First(l => (ReferenceEquals(l.Front.Sector, rooms[0]) ? l.Back! : l.Front).Sector is { } s && s.FloorHeight >= s.CeilingHeight);
            var besideLook = (ReferenceEquals(beside.Front.Sector, rooms[0]) ? beside.Front : beside.Back!).Top;
            Element part = which switch
            {
                "area" => door,
                "frame" => frame,
                "face" => found.Own.First(),
                "side" => map.Lines.First(l => l.OnMap && !found.Own.Contains(l) && (ReferenceEquals(l.Front.Sector, door) || ReferenceEquals(l.Back?.Sector, door))),
                _ => map.Lines.First(l => l.OnMap && (ReferenceEquals(l.Front.Sector, frame) || ReferenceEquals(l.Back?.Sector, frame)) && rooms.Contains(ReferenceEquals(l.Front.Sector, frame) ? l.Back!.Sector : l.Front.Sector)),
            };
            DeleteDoor(map, history, part);

            // One piece of wall between the rooms, closed; each room sees one wall, as the wall beside the door looked; no door.
            var rest = Between(map, rooms);
            Assert.True(rest.Count == 1, $"{which}: {rest.Count} areas between the rooms");
            Assert.True(rest[0].FloorHeight >= rest[0].CeilingHeight, which);
            geometry = MapGeometry.Build(map.Frame);
            Assert.DoesNotContain(map.Sectors, s => s.OnMap && Doors.Recognise(geometry, s) is not null);
            foreach (var room in rooms)
            {
                var faces = FacesToward(map, room);
                Assert.True(faces.Count == 1, $"{which}: {faces.Count} walls toward a room");
                Assert.Equal(256, faces[0].Length);
                Assert.Equal(0, faces[0].Special);
            }

            Assert.Equal(besideLook, FacesToward(map, rooms[0]).Select(l => (ReferenceEquals(l.Front.Sector, rooms[0]) ? l.Front : l.Back!).Top).Single());
            Assert.All(rooms, r => Assert.True(r.CeilingHeight > r.FloorHeight));

            // The Door tool's click on that wall: a door through it, framed into both rooms, on their floor.
            var wall = FacesToward(map, rooms[1]).Single();
            var again = MakeDoor(map, history, wall, Doors.Centred(wall, Doors.StandardWidth));
            Assert.True(again.Door is not null, $"{which}: {again.Why}");
            geometry = MapGeometry.Build(map.Frame);
            var made = Doors.Recognise(geometry, again.Door!)!;
            Assert.Equal(rooms.OrderBy(r => r.Slot), Doors.FramesOf(geometry, made).Select(f => f.Room).OrderBy(r => r.Slot));
            Assert.Equal(rooms[0].FloorHeight, made.Area.FloorHeight);
        }
    }

    [Fact]
    public void ADoorFillingAnOpeningBecomesAPieceOfWallOfItsOwnThatANewDoorGoesThrough()
    {
        // A corridor 64 wide off a room: the door fills its end, framed, with no wall beside it.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        var door = MakeDoor(map, history, opening, null).Door!;
        Assert.Equal(3, Between(map, rooms).Count);
        DeleteDoor(map, history, door);

        var rest = Assert.Single(Between(map, rooms));
        Assert.Equal(rest.FloorHeight, rest.CeilingHeight);
        Assert.Equal(rooms.Max(r => r.CeilingHeight), rest.CeilingHeight);
        Assert.All(map.Lines.Where(l => l.OnMap), l => Assert.Equal(0, l.Special));
        foreach (var room in rooms)
        {
            var face = Assert.Single(FacesToward(map, room));
            var side = ReferenceEquals(face.Front.Sector, room) ? face.Front : face.Back!;
            Assert.NotEqual("-", side.Bottom);
            Assert.Equal("-", side.Middle);
        }

        var again = MakeDoor(map, history, FacesToward(map, rooms[0]).Single(), null);
        Assert.True(again.Door is not null, again.Why);
        var geometry = MapGeometry.Build(map.Frame);
        Assert.Equal(2, Doors.FramesOf(geometry, Doors.Recognise(geometry, again.Door!)!).Count);
    }

    [Fact]
    public void APartOfADoorIsKnownAsTheDoorsAndTheRoomsAndTheirWallsAreNot()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
        var door = MakeDoor(map, history, opening, Doors.Centred(opening, Doors.StandardWidth)).Door!;
        var geometry = MapGeometry.Build(map.Frame);
        var pieces = map.Sectors.Where(s => s.OnMap && !rooms.Contains(s) && s.FloorHeight >= s.CeilingHeight && !ReferenceEquals(s, door)).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.All(rooms, r => Assert.Null(Doors.DoorwayOf(geometry, r, "doom")));
        Assert.All(pieces, p => Assert.Null(Doors.DoorwayOf(geometry, p, "doom")));
        Assert.All(map.Lines.Where(l => l.OnMap && (l.Back is null || pieces.Contains(l.Front.Sector) && rooms.Contains(l.Back.Sector) || rooms.Contains(l.Front.Sector) && pieces.Contains(l.Back.Sector))), l => Assert.Null(Doors.DoorwayOf(geometry, l, "doom")));
        Assert.All(Doors.FramesBeside(geometry, door), f => Assert.Same(door, Doors.DoorwayOf(geometry, f, "doom")));
        Assert.All(Doors.FramesBeside(geometry, door).Concat([door]).SelectMany(a => map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, a) || ReferenceEquals(l.Back?.Sector, a)))), l => Assert.Same(door, Doors.DoorwayOf(geometry, l.Front, "doom")));
    }
}
