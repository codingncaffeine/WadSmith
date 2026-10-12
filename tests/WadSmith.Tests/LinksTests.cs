using WadSmith.App.Canvas;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>The dashed links the plan draws from a selection: switches to what they work on, teleporters to where they send, and back.</summary>
public sealed class LinksTests
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    [Fact]
    public void ATeleporterIsLinkedToItsArrivalAndTheArrivalToItsSenders()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 384, 192), (512, 0, 768, 256));
        Thing? arrival = null;
        history.Record("Teleport", e => arrival = Teleports.Make(e, MapGeometry.Build(map.Frame), rooms[1], new MapPoint(640, 128), 90, "doom").Arrival);
        var g = MapGeometry.Build(map.Frame);
        var look = new MapLook(null);
        var sender = map.Lines.First(l => l.OnMap && Teleports.IsTeleport(l));
        var link = Assert.Single(SelectionOverlay.Take([sender], g, look).Links);
        Assert.Equal((640f, 128f), (link.X2, link.Y2));

        var back = SelectionOverlay.Take([arrival!], g, look).Links;
        Assert.Equal(map.Lines.Count(l => l.OnMap && Teleports.IsTeleport(l)), back.Count);
        Assert.All(back, l => Assert.Equal((640f, 128f), (l.X2, l.Y2)));
    }

    [Fact]
    public void ASwitchIsLinkedToItsDoorAndTheDoorToItsSwitch()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 384, 192));
        Sector? door = null;
        history.Record("Door", e => door = Doors.Make(e, MapGeometry.Build(map.Frame), map.Lines.Single(l => l.OnMap && l.Back is not null), Look).Door);
        var west = map.Lines.Single(l => l.OnMap && l.Start.X == 0 && l.End.X == 0);
        Line? sw = null;
        history.Record("Switch", e => sw = Switches.Make(e, MapGeometry.Build(map.Frame), west, new MapPoint(0, 128), door!, Look).Switch);
        var g = MapGeometry.Build(map.Frame);
        Assert.Single(SelectionOverlay.Take([sw!], g, new MapLook(null)).Links);
        Assert.Single(SelectionOverlay.Take([door!], g, new MapLook(null)).Links);
        Assert.Empty(SelectionOverlay.Take([rooms[0]], g, new MapLook(null)).Links);
    }
}
