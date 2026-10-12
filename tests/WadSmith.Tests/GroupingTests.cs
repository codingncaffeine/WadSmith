using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Groups and locks: names and locks kept in the map, read from frames, holding what is locked.</summary>
public sealed class GroupingTests
{
    [Fact]
    public void AGroupIsANameOnItsMembersThatGoesWithTheMapAndUndoes()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256), (512, 0, 768, 256));
        Thing? imp = null;
        history.Record("Imp", e => imp = e.Add(new Thing { X = 128, Y = 128, Type = 3001 }));
        history.Record("Group", e => Grouping.Name(e, [rooms[0], rooms[1], imp!], " Courtyard "));
        var group = Assert.Single(Grouping.In(map.Frame));
        Assert.Equal(("Courtyard", 2, 1, false), (group.Name, group.Rooms.Count, group.Things.Count, group.Locked));
        Assert.Null(Grouping.GroupOf(rooms[2]));

        // Written as UDMF and read back, the group is still there.
        var model = map.ToModel();
        var back = EditorMap.FromModel(MapConversion.FromUdmf("MAP01", UdmfText.Parse(UdmfText.WriteString(MapConversion.ToUdmf(model)))));
        Assert.Equal(["Courtyard"], Grouping.In(back.Frame).Select(g => g.Name));

        history.Undo();
        Assert.Empty(Grouping.In(map.Frame));
    }

    [Fact]
    public void ALockedRoomHoldsItsWallsItsCornersAndNotItsNeighbours()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        history.Record("Group", e => Grouping.Name(e, [rooms[0]], "Hall"));
        history.Record("Lock", e => Grouping.Lock(e, [rooms[0]], true));
        var g = MapGeometry.Build(map.Frame);
        Assert.True(Grouping.In(map.Frame)[0].Locked);
        Assert.True(Grouping.Holds(g, rooms[0]));
        Assert.False(Grouping.Holds(g, rooms[1]));

        // Its own walls are held; the wall it shares with the free room is not (the free room still owns it), but the shared
        // corners are: moving them would change the locked room.
        var own = map.Lines.First(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, rooms[0]));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        Assert.True(Grouping.Holds(g, own));
        Assert.False(Grouping.Holds(g, shared));
        Assert.True(Grouping.Holds(g, shared.Start));
        Assert.False(Grouping.Holds(g, map.Vertices.Single(v => v.OnMap && v.X == 512 && v.Y == 0)));

        // A drag's worker asks the frame whether moving corners would change a locked room, and which.
        Assert.Equal("Hall", Grouping.HeldBy(g, [shared.Start]));
        Assert.Null(Grouping.HeldBy(g, [map.Vertices.Single(v => v.OnMap && v.X == 512 && v.Y == 0)]));

        // Ungrouping unlocks too.
        history.Record("Ungroup", e => Grouping.Name(e, [rooms[0]], null));
        Assert.False(Grouping.IsLocked(rooms[0]));
        Assert.Empty(Grouping.In(map.Frame));
    }

    [Fact]
    public void AHiddenGroupHidesItsRoomsWallsAndThingsButNotWhatItShares()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        Thing? imp = null;
        history.Record("Imp", e => imp = e.Add(new Thing { X = 128, Y = 128, Type = 3001 }));
        history.Record("Group", e => Grouping.Name(e, [rooms[0], imp!], "Hall"));
        var g = MapGeometry.Build(map.Frame);
        var hidden = new HashSet<string> { "Hall" };
        Assert.True(Grouping.Hides(g, rooms[0], hidden));
        Assert.True(Grouping.Hides(g, imp!, hidden));
        Assert.False(Grouping.Hides(g, rooms[1], hidden));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        Assert.False(Grouping.Hides(g, shared, hidden));
        Assert.False(Grouping.Hides(g, shared.Start, hidden));
        Assert.True(Grouping.Hides(g, map.Lines.First(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, rooms[0])), hidden));
        Assert.True(Grouping.Hides(g, map.Vertices.Single(v => v.OnMap && v.X == 0 && v.Y == 0), hidden));
        Assert.False(Grouping.Hides(g, rooms[0], new HashSet<string>()));

        // The 3D scene leaves the hidden room, the faces seen from it and its imp out.
        var all = Editor.Scene.MapScene.Build(g, _ => (64, 64));
        var shown = Editor.Scene.MapScene.Build(g, _ => (64, 64), null, hidden);
        Assert.Contains(all.Batches.SelectMany(b => b.Owners), o => ReferenceEquals(o, rooms[0]));
        Assert.DoesNotContain(shown.Batches.SelectMany(b => b.Owners), o => ReferenceEquals(o, rooms[0]));
        Assert.Contains(shown.Batches.SelectMany(b => b.Owners), o => ReferenceEquals(o, rooms[1]));
        Assert.DoesNotContain(shown.Things, t => ReferenceEquals(t.Element, imp));
    }

    [Fact]
    public void LockingOrHidingLetsGoOfTheSelectionAndALockedGroupCannotBeSelected()
    {
        using var temp = new TempFolder();
        var project = WadSmith.Core.Projects.Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(Editor.StarterRoom.Create("MAP01", "doom"));
        using var session = Editor.EditorSession.Open(project, "MAP01", a => a());
        var room = session.Map.Sectors[0];
        var told = new List<string>();
        session.Notice += told.Add;
        session.Selection.Set(room);
        session.GroupSelection("Start");
        var group = Assert.Single(Grouping.In(session.Map.Frame));
        session.Hide("Start", true);
        Assert.True(session.Selection.IsEmpty);
        session.Hide("Start", false);
        session.SelectGroup(group);
        Assert.Equal(1, session.Selection.Count);
        session.LockGroup(group, true);
        Assert.True(session.Selection.IsEmpty);
        session.SelectGroup(Assert.Single(Grouping.In(session.Map.Frame)));
        Assert.True(session.Selection.IsEmpty);
        Assert.Contains(told, t => t.Contains("is locked", StringComparison.Ordinal));
        Assert.Equal("Lock a group", session.History.UndoLabel);
    }
}
