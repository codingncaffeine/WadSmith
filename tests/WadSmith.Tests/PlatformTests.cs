using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Platforms and water (GZDoom's 3D floors): the control room, the index, the 3D view, the walker and the Inspector.</summary>
public sealed class PlatformTests
{
    private static readonly Look Doom = Look.For("doom");

    /// <summary>The starter room (512 square, floor 0, ceiling 128) with a platform from <paramref name="bottom"/> to <paramref name="top"/>.</summary>
    private static (EditorMap Map, History History, Sector Room) WithPlatform(double bottom, double top, PlatformKind kind = PlatformKind.Solid)
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var room = map.Sectors[0];
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Add a platform", e => Platforms.Add(e, geometry, room, bottom, top, kind, Doom));
        return (map, history, room);
    }

    private static void Arg(StructureEdit e, Line line, int k, int value) => e.Set(line, l => l.Args[k], (l, v) => l.Args[k] = v, value);

    [Fact]
    public void APlatformIsAControlRoomOutsideTheMapThatTheIndexFindsAndUndoTakesAway()
    {
        var (map, history, room) = WithPlatform(56, 72);
        var geometry = MapGeometry.Build(map.Frame);
        var platform = Assert.Single(Platforms.Of(geometry, room));
        Assert.Equal((56.0, 72.0, PlatformKind.Solid, 255), (platform.Bottom, platform.Top, platform.Kind, platform.Alpha));
        Assert.NotEqual(0, room.Tag);

        // The control room: north-west of the room, clear of it; its wall carries the special naming the room's tag.
        var control = platform.Control;
        var bounds = geometry.WallBoundsAt(control.Slot)!.Value;
        Assert.True(bounds.MaxX < -256 && bounds.MinY > 256, $"control room at {bounds}");
        Assert.Equal((64.0, 64.0), (bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
        Assert.Equal(Platforms.Special, platform.ControlLine.Special);
        Assert.Equal([room.Tag & 255, 1, 0, 255, room.Tag >> 8], platform.ControlLine.Args);
        Assert.Same(control, platform.ControlLine.Front.Sector);
        Assert.Equal((room.FloorTexture, room.FloorTexture), (control.FloorTexture, control.CeilingTexture));
        Assert.Same(room, Platforms.ControlOf(geometry, control)!.Room);
        Assert.Null(Platforms.ControlOf(geometry, room));
        Assert.Empty(Platforms.Of(geometry, control));

        Assert.True(history.Undo());
        geometry = MapGeometry.Build(map.Frame);
        Assert.Empty(geometry.Platforms);
        Assert.Equal(0, room.Tag);
        Assert.Single(map.Sectors, s => s.OnMap);
    }

    [Fact]
    public void AControlRoomRaisesNoProblems()
    {
        var plain = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var before = Problems.Check(MapGeometry.Build(plain.Frame), null, Doom, "doom").Select(p => p.Rule).Order().ToList();
        foreach (var kind in (PlatformKind[])[PlatformKind.Solid, PlatformKind.Water])
        {
            var (map, _, _) = WithPlatform(kind == PlatformKind.Water ? -8 : 56, kind == PlatformKind.Water ? 32 : 72, kind);
            Assert.Equal(before, Problems.Check(MapGeometry.Build(map.Frame), null, Doom, "doom").Select(p => p.Rule).Order().ToList());
        }
    }

    [Fact]
    public void TheIndexReadsTheTagAsGZDoomDoes()
    {
        // Without the line-id flag the fifth argument is the tag's high byte (260 = 4 + 256); with it (type 8) it is not.
        var (map, history, room) = WithPlatform(56, 72);
        var line = Platforms.Of(MapGeometry.Build(map.Frame), room).Single().ControlLine;
        history.Record("t", e =>
        {
            e.Set(room, s => s.Tag, (s, v) => s.Tag = v, 260);
            Arg(e, line, 0, 4);
            Arg(e, line, 4, 1);
        });
        Assert.Single(MapGeometry.Build(map.Frame).Platforms);
        history.Record("t", e => Arg(e, line, 1, 1 | 8));
        Assert.Empty(MapGeometry.Build(map.Frame).Platforms);
        history.Record("t", e => e.Set(room, s => s.Tag, (s, v) => s.Tag = v, 4));
        Assert.Single(MapGeometry.Build(map.Frame).Platforms);

        // The Vavoom kind (type 0) is not drawn.
        history.Record("t", e => Arg(e, line, 1, 0));
        Assert.Empty(MapGeometry.Build(map.Frame).Platforms);
    }

    [Fact]
    public void TheViewShowsTheTopWithTheControlCeilingAndTheUndersideWithItsFloorAndPicksThem()
    {
        var (map, history, room) = WithPlatform(56, 72);
        var geometry = MapGeometry.Build(map.Frame);
        var control = Platforms.Of(geometry, room).Single().Control;
        history.Record("t", e =>
        {
            e.Set(control, s => s.CeilingTexture, (s, v) => s.CeilingTexture = v, "TOPFLAT");
            e.Set(control, s => s.FloorTexture, (s, v) => s.FloorTexture = v, "UNDERFLAT");
        });
        geometry = MapGeometry.Build(map.Frame);
        var scene = MapScene.Build(geometry, _ => (64, 128));

        var top = scene.Batches.Single(b => b.Texture == "TOPFLAT" && b.Kind == SurfaceKind.Floor);
        Assert.All(top.Owners, o => Assert.Same(control, o));
        Assert.All(top.Parts, p => Assert.Equal(2, p));
        Assert.All(Enumerable.Range(0, top.VertexCount), i => Assert.Equal(72f, top.Vertices[i * SceneBatch.Stride + 2]));
        var under = scene.Batches.Single(b => b.Texture == "UNDERFLAT" && b.Kind == SurfaceKind.Ceiling);
        Assert.All(under.Parts, p => Assert.Equal(3, p));
        Assert.All(Enumerable.Range(0, under.VertexCount), i => Assert.Equal(56f, under.Vertices[i * SceneBatch.Stride + 2]));

        // From above, a ray down meets the top: a texture there goes on the control room's ceiling; from below, the underside.
        var down = scene.Hit((100, 100, 110), (0, 0, -1), 0)!.Value;
        Assert.Equal((HitKind.Floor, true, 72.0), (down.Kind, down.Platform, Math.Round(down.Z, 6)));
        Assert.Same(control, down.Element);
        Assert.True(down.Target.Ceiling);
        Assert.Equal("the top of this platform", down.Words);
        var up = scene.Hit((100, 100, 20), (0, 0, 1), 0)!.Value;
        Assert.Equal((HitKind.Ceiling, true, 56.0), (up.Kind, up.Platform, Math.Round(up.Z, 6)));
        Assert.False(up.Target.Ceiling);
        Assert.Equal(top.Owners.Count, scene.Surface(down).Surfaces.Length / (SceneBatch.Stride * 3));

        // The room's own floor is still a floor (not a platform).
        var floor = scene.Hit((100, 100, 40), (0, 0, -1), 0)!.Value;
        Assert.Equal((HitKind.Floor, false), (floor.Kind, floor.Platform));
        Assert.Same(room, floor.Element);
    }

    [Fact]
    public void WaterIsSeeThroughDrawnLastAndShowsFromInside()
    {
        var (map, _, room) = WithPlatform(-8, 32, PlatformKind.Water);
        var geometry = MapGeometry.Build(map.Frame);
        var water = Platforms.Of(geometry, room).Single();
        Assert.Equal((PlatformKind.Water, 128, Doom.Water, Doom.Water), (water.Kind, water.Alpha, water.Control.FloorTexture, water.Control.CeilingTexture));
        var scene = MapScene.Build(geometry, _ => (64, 128));
        var surface = scene.Batches.Single(b => b.Kind == SurfaceKind.Floor && b.Opacity < 1);
        Assert.Equal(128 / 255f, surface.Opacity);
        Assert.Same(scene.Batches[^1].Opacity < 1 ? scene.Batches[^1] : null, scene.Batches.Last(b => b.Opacity < 1));
        Assert.True(scene.Batches.SkipWhile(b => b.Opacity >= 1).All(b => b.Opacity < 1), "see-through batches come last");

        // Drawn from inside too: from under the surface, a ray up meets it.
        var up = scene.Hit((100, 100, 10), (0, 0, 1), 0)!.Value;
        Assert.Equal((HitKind.Floor, true, 32.0), (up.Kind, up.Platform, Math.Round(up.Z, 6)));

        // A solid one is not drawn from inside.
        var (solidMap, _, _) = WithPlatform(56, 72);
        var solid = MapScene.Build(MapGeometry.Build(solidMap.Frame), _ => (64, 128));
        Assert.Equal(HitKind.Ceiling, solid.Hit((100, 100, 10), (0, 0, 1), 0)!.Value.Kind);
        Assert.Equal(56, solid.Hit((100, 100, 10), (0, 0, 1), 0)!.Value.Z, 6);
    }

    [Fact]
    public void TheSidesShowFromTheRoomBesideWithTheControlWallsPicture()
    {
        var map = EditorMap.FromModel(SceneTests.TwoRooms(0, 128));
        var history = new History(map);
        var west = map.Sectors[0];
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Add a platform", e => Platforms.Add(e, geometry, west, 56, 72, PlatformKind.Solid, Doom));
        geometry = MapGeometry.Build(map.Frame);
        var platform = Platforms.Of(geometry, west).Single();
        Assert.Equal("WALL", platform.Sides);
        history.Record("t", e => e.Set(platform.ControlLine.Front, s => s.Middle, (s, v) => s.Middle = v, "SIDES"));
        var scene = MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 128));
        // The SIDES batch also holds the control room's own walls; the platform's side is the two triangles on the shared wall.
        var batch = scene.Batches.Single(b => b.Texture == "SIDES" && b.Kind == SurfaceKind.Wall);
        var onShared = Enumerable.Range(0, batch.Owners.Count).Where(t => batch.Walls[t].Back is not null).ToList();
        Assert.Equal(2, onShared.Count);
        Assert.All(onShared, t => Assert.Same(platform.ControlLine.Front, batch.Owners[t]));
        Assert.All(onShared.SelectMany(t => Enumerable.Range(t * 3, 3)), i =>
        {
            Assert.Equal(128f, batch.Vertices[i * SceneBatch.Stride]);
            Assert.InRange(batch.Vertices[i * SceneBatch.Stride + 2], 56f, 72f);
        });

        // Seen from the east room: a ray west at height 64 meets the side; at 40 it passes under into the west room's far wall.
        var side = scene.Hit((200, 64, 64), (-1, 0, 0), 0)!.Value;
        Assert.Equal((HitKind.Wall, 128.0), (side.Kind, Math.Round(side.X, 6)));
        Assert.Same(platform.ControlLine.Front, side.Element);
        Assert.Equal(0, scene.Hit((200, 64, 40), (-1, 0, 0), 0)!.Value.X, 6);
    }

    [Fact]
    public void TheWalkerStandsOnASolidPlatformWalksUnderItAndSwimsThroughWater()
    {
        var (map, _, _) = WithPlatform(56, 72);
        var geometry = MapGeometry.Build(map.Frame);
        var (a, b) = (new MapPoint(0, 0), new MapPoint(16, 0));
        Assert.Equal(Walker.EyeHeight, Walker.Step(geometry, a, 0, b)!.Value, 6);
        Assert.Equal(72 + Walker.EyeHeight, Walker.Step(geometry, a, 72, b)!.Value, 6);

        // A low slab is a step up; one too high to step onto that starts below the player's head stops them.
        var (low, _, _) = WithPlatform(0, 24);
        Assert.Equal(24 + Walker.EyeHeight, Walker.Step(MapGeometry.Build(low.Frame), a, 0, b)!.Value, 6);
        var (wall, _, _) = WithPlatform(8, 40);
        Assert.Null(Walker.Step(MapGeometry.Build(wall.Frame), a, 0, b));
        var (water, _, _) = WithPlatform(-8, 100, PlatformKind.Water);
        Assert.Equal(Walker.EyeHeight, Walker.Step(MapGeometry.Build(water.Frame), a, 0, b)!.Value, 6);
    }

    private sealed class Open : IDisposable
    {
        private readonly TempFolder _temp = new();
        private readonly ConcurrentQueue<Action> _ui = new();

        public Open(MapModel model)
        {
            var project = Project.Create(Path.Combine(_temp.Path, "p"), "p", "doom2");
            project.SaveMap(model);
            Session = EditorSession.Open(project, "MAP01", _ui.Enqueue);
            Session.Notice += Told.Add;
        }

        public EditorSession Session { get; }

        public List<string> Told { get; } = [];

        public void Settle()
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            do
            {
                while (_ui.TryDequeue(out var a)) a();
                Thread.Sleep(5);
            }
            while ((Session.Geometry.Version != Session.Map.Version || !_ui.IsEmpty) && DateTime.UtcNow < until);
        }

        public void Dispose()
        {
            Session.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void TheInspectorAddsAPlatformSetsItAndRemovesItEachOneUndoStep()
    {
        using var o = new Open(StarterRoom.Create("MAP01", "doom"));
        var session = o.Session;
        o.Settle();
        var room = session.Map.Sectors[0];
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(room);
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Add a floating platform").Command.Execute(null);
        o.Settle();
        Assert.Equal("Add a platform", session.History.UndoLabel);
        Assert.Contains(o.Told, t => t.StartsWith("A platform spans the room from 56 to 72", StringComparison.Ordinal));
        var platform = Platforms.Of(session.Geometry, room).Single();

        // The room's rows now show the platform; its top moves with the row, one undo step.
        inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Top").Value = 80;
        Assert.Equal(80, platform.Control.CeilingHeight);
        Assert.Equal("Change the platform's top", session.History.UndoLabel);
        inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "It is").Value = 1;
        Assert.Equal((PlatformKind.Water, 128), (platform.Kind, platform.Alpha));
        inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "See-through").Value = 0;
        Assert.Equal(255, platform.Alpha);

        // An object dropped on its top in the 3D view stands there, as a height above the floor.
        var imp = session.PlaceThing(3001, new MapPoint(64, 64), standAt: 80);
        Assert.Equal(80, imp.Height);

        // Selecting the control room shows the same platform, with the way back to the room.
        session.Selection.Set(platform.Control);
        Assert.Equal("Water's control room", inspector.Heading);
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Select the room it stands in").Command.Execute(null);
        Assert.Same(room, session.Selection.Items.Single());

        o.Settle();
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Drain the water").Command.Execute(null);
        o.Settle();
        Assert.Empty(session.Geometry.Platforms);
        Assert.False(platform.Control.OnMap);
        session.History.Undo();
        o.Settle();
        Assert.Single(session.Geometry.Platforms);
    }

    [Fact]
    public void AFloatingPlatformIsRefusedInWordsInARoomTooLowForIt()
    {
        using var o = new Open(SceneTests.TwoRooms(0, 96));
        o.Settle();
        var east = o.Session.Map.Sectors[1];
        o.Session.AddPlatform(east, PlatformKind.Solid);
        o.Settle();
        Assert.Empty(o.Session.Geometry.Platforms);
        Assert.Contains(o.Told, t => t.Contains("needs a room at least 128 tall", StringComparison.Ordinal) && t.Contains("This room is 96 tall", StringComparison.Ordinal));
        o.Session.AddPlatform(east, PlatformKind.Water);
        o.Settle();
        Assert.Equal(32, Assert.Single(o.Session.Geometry.Platforms).Top);
    }
}
