using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A window is one thing, as a door is (the user's report on doors, 2026-10-15): deleting it, by its area or a wall or face of
/// it, leaves the wall it was in like the wall beside it, and the rooms as they were. Deleting a window's face once made the
/// room the window's (its floor up on the sill, its ceiling down to the top).
/// </summary>
public sealed class WindowDeletingTests
{
    private static readonly Look Look = Look.For("doom");

    private static string Udmf(EditorMap map) => UdmfText.WriteString(MapConversion.ToUdmf(map.ToModel()));

    /// <summary>Deletes as the editor does (Delete on the selection), checks the map is sound and undo gives it back exactly.</summary>
    private static void Delete(EditorMap map, History history, Element part)
    {
        var geometry = MapGeometry.Build(map.Frame);
        var window = WallWindows.WindowOf(geometry, part) ?? throw new InvalidOperationException("Not a part of a window.");
        var before = Udmf(map);
        history.Record("Delete the window", e => Doors.WallUp(e, geometry, window, [], Look));
        DrawingTests.AssertSound(map);
        var after = Udmf(map);
        history.Undo();
        Assert.Equal(before, Udmf(map));
        history.Redo();
        Assert.Equal(after, Udmf(map));
    }

    [Fact]
    public void AWindowInAWallTwoRoomsShareGoesWholeByAnyOfItsPartsAndTheWallClosesUp()
    {
        foreach (var which in new[] { "area", "face toward the first room", "face toward the second room", "face as the 3D view picks it" })
        {
            var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
            var opening = Assert.Single(map.Lines, l => l.OnMap && l.Back is not null);
            (Sector? Window, string? Why) made = (null, null);
            history.Record("Make a window", e => made = WallWindows.Make(e, MapGeometry.Build(map.Frame), opening, 96, 160, Look));
            var window = made.Window!;
            var heights = rooms.Select(r => (r.FloorHeight, r.CeilingHeight)).ToList();
            Line FaceToward(Sector room) => map.Lines.First(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, window) || ReferenceEquals(l.Back.Sector, window)) && ReferenceEquals(ReferenceEquals(l.Front.Sector, window) ? l.Back.Sector : l.Front.Sector, room));
            Element part = which switch
            {
                "area" => window,
                "face toward the first room" => FaceToward(rooms[0]),
                "face toward the second room" => FaceToward(rooms[1]),
                _ => ReferenceEquals(FaceToward(rooms[0]).Front.Sector, rooms[0]) ? FaceToward(rooms[0]).Front : FaceToward(rooms[0]).Back!,
            };
            Delete(map, history, part);

            var geometry = MapGeometry.Build(map.Frame);
            var rest = map.Sectors.Where(s => s.OnMap && !rooms.Contains(s)).ToList();
            Assert.True(rest.Count == 1 && rest[0].FloorHeight >= rest[0].CeilingHeight, $"{which}: {rest.Count} areas between the rooms");
            Assert.DoesNotContain(map.Sectors, s => s.OnMap && WallWindows.Recognise(geometry, s) is not null);
            Assert.Equal(heights, rooms.Select(r => (r.FloorHeight, r.CeilingHeight)).ToList());
            Assert.All(rooms, r => Assert.True(r.OnMap, which));
            foreach (var room in rooms) Assert.Single(map.Lines, l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back.Sector, room)));
        }
    }

    [Fact]
    public void AWindowThroughASolidWallBecomesWallAgain()
    {
        // Two rooms 64 apart, a window cut through the solid wall between them: deleted, a closed piece of wall stands there.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (320, 0, 576, 256));
        var wall = map.Lines.Single(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, rooms[0]) && Math.Abs(l.Start.X - 256) < 1 && Math.Abs(l.End.X - 256) < 1);
        (Sector? Window, string? Why) made = (null, null);
        history.Record("Make a window", e => made = WallWindows.Make(e, MapGeometry.Build(map.Frame), wall, 96, 160, Look));
        Assert.True(made.Window is not null, made.Why);
        var heights = rooms.Select(r => (r.FloorHeight, r.CeilingHeight)).ToList();
        Delete(map, history, made.Window!);

        var geometry = MapGeometry.Build(map.Frame);
        var rest = Assert.Single(map.Sectors, s => s.OnMap && !rooms.Contains(s));
        Assert.True(rest.FloorHeight >= rest.CeilingHeight);
        Assert.DoesNotContain(map.Sectors, s => s.OnMap && WallWindows.Recognise(geometry, s) is not null);
        Assert.Equal(heights, rooms.Select(r => (r.FloorHeight, r.CeilingHeight)).ToList());
    }
}
