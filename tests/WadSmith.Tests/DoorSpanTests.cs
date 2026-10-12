using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Doors of a chosen width: dragged along a wall, or a standard one in the middle of a long wall.</summary>
public sealed class DoorSpanTests
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    /// <summary>Two rooms 512 tall side by side: the wall between them is 512 long, longer than a door can be.</summary>
    private static (EditorMap Map, History History, Line Wall) LongWall()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 512), (256, 0, 512, 512));
        return (map, history, map.Lines.Single(l => l.OnMap && l.Back is not null));
    }

    private static (Sector? Door, string? Why) Make(EditorMap map, History history, Line wall, (double, double)? span)
    {
        (Sector? Door, string? Why) result = (null, null);
        history.Record("Make a door", e => result = Doors.Make(e, MapGeometry.Build(map.Frame), wall, Look, span));
        return result;
    }

    [Fact]
    public void AStretchDraggedAlongAWallBecomesADoorThatWide()
    {
        var (map, history, wall) = LongWall();
        var (door, why) = Make(map, history, wall, (100, 196));
        Assert.Null(why);
        var g = MapGeometry.Build(map.Frame);
        Assert.NotNull(Doors.Recognise(g, door!));
        var box = g.WallBoundsAt(door!.Slot)!.Value;
        Assert.Equal(96, Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));
        Assert.Equal(Doors.Depth, Math.Min(box.MaxX - box.MinX, box.MaxY - box.MinY));
        Assert.Contains(map.Lines, l => l.OnMap && l.Front.Top == Look.BigDoor);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ALongWallClickedGetsAStandardDoorInItsMiddle()
    {
        var (map, history, wall) = LongWall();
        Assert.Equal((224.0, 288.0), Doors.Centred(wall));
        var (door, _) = Make(map, history, wall, Doors.Centred(wall));
        var box = MapGeometry.Build(map.Frame).WallBoundsAt(door!.Slot)!.Value;
        Assert.Equal((224.0, 288.0), (box.MinY, box.MaxY));
        Assert.Contains(map.Lines, l => l.OnMap && l.Front.Top == Look.Door);

        // The script's "door" does the same on a long wall, and takes a width.
        var (scripted, _, _) = MovingTests.Drawn((0, 0, 256, 512), (256, 0, 512, 512));
        Assert.All(EditScript.Run(scripted, "door 256 256 128", Look, "doom"), r => Assert.True(r.Done, r.Said));
        var made = scripted.Sectors.Single(s => s.OnMap && s.CeilingHeight == s.FloorHeight);
        var scriptedBox = MapGeometry.Build(scripted.Frame).WallBoundsAt(made.Slot)!.Value;
        Assert.Equal((192.0, 320.0), (scriptedBox.MinY, scriptedBox.MaxY));
    }

    [Fact]
    public void TooNarrowOrTooWideIsRefused()
    {
        var (map, history, wall) = LongWall();
        Assert.Contains("at least 8", Make(map, history, wall, (100, 104)).Why);
        Assert.Contains("up to 256", Make(map, history, wall, (0, 400)).Why);
        Assert.Contains("corridor", Make(map, history, wall, null).Why);
    }
}
