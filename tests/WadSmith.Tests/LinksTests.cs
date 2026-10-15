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

    private static int Action(string name) => Core.Games.ActionCatalog.Find(name)!.Number;

    private static Thing Add(Editor.Editing.History history, double x, double y, int type, int tid = 0, int special = 0, params int[] args)
    {
        var t = new Thing { X = x, Y = y, Type = type, Tid = tid, Special = special };
        args.CopyTo(t.Args, 0);
        history.Record("Add", e => e.Add(t));
        return t;
    }

    [Fact]
    public void AnObjectsOwnActionIsLinkedToTheAreaItNamesAndTheAreaBack()
    {
        // A monster that lowers the far room's floor when it dies (Hexen format: the action is the object's own).
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (512, 0, 768, 256));
        history.Record("Tag", e => e.Set(rooms[1], s => s.Tag, (s, v) => s.Tag = v, 7));
        var monster = Add(history, 128, 128, 3001, special: Action("Floor_LowerToLowest"), args: 7);
        var g = MapGeometry.Build(map.Frame);
        Assert.Equal([(128f, 128f, 640f, 128f)], SelectionOverlay.Take([monster], g, new MapLook(null)).Links);
        Assert.Equal([(128f, 128f, 640f, 128f)], SelectionOverlay.Take([rooms[1]], g, new MapLook(null)).Links);
    }

    [Fact]
    public void AnActionOnObjectsIsLinkedToThemByTheirIdAndBack()
    {
        // A wall that wakes the monsters with id 5 (Thing_Activate), and one that has none of its kind.
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        var wall = map.Lines.First(l => l.OnMap);
        history.Record("Act", e =>
        {
            e.Set(wall, l => l.Special, (l, v) => l.Special = v, Action("Thing_Activate"));
            e.Set(wall, l => l.Args[0], (l, v) => l.Args[0] = v, 5);
        });
        var imp = Add(history, 64, 64, 3001, tid: 5);
        var other = Add(history, 192, 192, 3001, tid: 6);
        var g = MapGeometry.Build(map.Frame);
        var (mx, my) = ((float)((wall.Start.X + wall.End.X) / 2), (float)((wall.Start.Y + wall.End.Y) / 2));
        Assert.Equal([(mx, my, 64f, 64f)], SelectionOverlay.Take([wall], g, new MapLook(null)).Links);
        Assert.Equal([(mx, my, 64f, 64f)], SelectionOverlay.Take([imp], g, new MapLook(null)).Links);
        Assert.Empty(SelectionOverlay.Take([other], g, new MapLook(null)).Links);
    }

    [Fact]
    public void APatrolRouteAndACamerasPathAreLinkedStopToStop()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));

        // A patrol round three points (1 → 2 → 3 → 1): each stop once, the last back to the first.
        var first = Add(history, 32, 32, 9024, tid: 1, args: 2);
        Add(history, 96, 32, 9024, tid: 2, args: 3);
        Add(history, 96, 96, 9024, tid: 3, args: 1);

        // A moving camera to point 10, then point 11, where the path ends (the next stop is in the fourth argument).
        var camera = Add(history, 200, 200, 9072, args: [10, 0]);
        Add(history, 150, 150, 9070, tid: 10, args: [0, 0, 0, 11, 0]);
        Add(history, 100, 150, 9070, tid: 11);
        var g = MapGeometry.Build(map.Frame);
        Assert.Equal([(32f, 32f, 96f, 32f), (96f, 32f, 96f, 96f), (96f, 96f, 32f, 32f)], SelectionOverlay.Take([first], g, new MapLook(null)).Links);
        Assert.Equal([(200f, 200f, 150f, 150f), (150f, 150f, 100f, 150f)], SelectionOverlay.Take([camera], g, new MapLook(null)).Links);
    }
}
