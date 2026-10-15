using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A switch is one thing, as a door is (the user's report on doors, 2026-10-15): deleting it, by its wall, its face or its
/// alcove, gives back the wall it was set into, as the wall beside it. Delete on a switch's wall once only said the wall stays.
/// </summary>
public sealed class SwitchDeletingTests
{
    private static readonly Look Look = Look.For("doom");

    private static string Udmf(EditorMap map) => UdmfText.WriteString(MapConversion.ToUdmf(map.ToModel()));

    /// <summary>An exit switch on the room's east wall: in an alcove when its picture is shorter than the room, else on a flat piece.</summary>
    private static (EditorMap Map, History History, Sector Room, Line Switch) ExitSwitch(bool alcove)
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256));
        var room = rooms[0];
        var wall = map.Lines.Single(l => l.OnMap && Math.Abs(l.Start.X - 256) < 1 && Math.Abs(l.End.X - 256) < 1);
        Line? made = null;
        history.Record("Make it the exit switch", e => made = Exits.Make(e, MapGeometry.Build(map.Frame), wall, false, Look, new MapPoint(256, 128), alcove ? _ => (32, 64) : null));
        DrawingTests.AssertSound(map);
        return (map, history, room, made!);
    }

    private static void Delete(EditorMap map, History history, Element part)
    {
        var geometry = MapGeometry.Build(map.Frame);
        var wall = Switches.SwitchOf(geometry, part) ?? throw new InvalidOperationException("Not a part of a switch.");
        var before = Udmf(map);
        history.Record("Delete the switch", e => Switches.Remove(e, geometry, wall, Look));
        DrawingTests.AssertSound(map);
        var after = Udmf(map);
        history.Undo();
        Assert.Equal(before, Udmf(map));
        history.Redo();
        Assert.Equal(after, Udmf(map));
    }

    [Fact]
    public void AnExitSwitchInAnAlcoveGoesByAnyOfItsPartsAndTheWallIsWholeAgain()
    {
        foreach (var which in new[] { "switch wall", "switch face", "alcove", "alcove's mouth", "alcove's side" })
        {
            var (map, history, room, sw) = ExitSwitch(alcove: true);
            var alcove = sw.Front.Sector;
            Assert.NotSame(room, alcove);
            var texture = map.Lines.First(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, room) && Math.Abs(l.Start.X - 256) < 1 && Math.Abs(l.End.X - 256) < 1).Front.Middle;
            Element part = which switch
            {
                "switch wall" => sw,
                "switch face" => sw.Front,
                "alcove" => alcove,
                "alcove's mouth" => map.Lines.Single(l => l.OnMap && l.Back is not null),
                _ => map.Lines.First(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, alcove) && !ReferenceEquals(l, sw)),
            };
            Delete(map, history, part);

            Assert.Single(map.Sectors, s => s.OnMap);
            Assert.All(map.Lines.Where(l => l.OnMap), l => Assert.Equal(0, l.Special));
            var east = Assert.Single(map.Lines, l => l.OnMap && Math.Abs(l.Start.X - 256) < 1 && Math.Abs(l.End.X - 256) < 1);
            Assert.True(east.Back is null && east.Length == 256, which);
            Assert.Equal(texture, east.Front.Middle);
            Assert.Equal(4, map.Lines.Count(l => l.OnMap));
        }
    }

    [Fact]
    public void ASwitchOnAFlatPieceOfWallGoesAndTheWallIsOneWallAgain()
    {
        var (map, history, room, sw) = ExitSwitch(alcove: false);
        Assert.Same(room, sw.Front.Sector);
        Delete(map, history, sw.Front);
        Assert.All(map.Lines.Where(l => l.OnMap), l => Assert.Equal(0, l.Special));
        Assert.Equal(4, map.Lines.Count(l => l.OnMap));
        Assert.DoesNotContain(map.Lines, l => l.OnMap && l.Front.Middle.StartsWith("SW", StringComparison.OrdinalIgnoreCase));
    }
}
