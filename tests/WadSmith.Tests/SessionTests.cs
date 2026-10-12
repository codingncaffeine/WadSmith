using System.Collections.Concurrent;
using WadSmith.Core.Checks;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

public sealed class SessionTests
{
    /// <summary>Stands in for the UI dispatcher: queued actions run when the test pumps.</summary>
    private sealed class FakeUi
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action a) => _queue.Enqueue(a);

        public void PumpUntil(Func<bool> done, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (!done())
            {
                while (_queue.TryDequeue(out var a)) a();
                if (DateTime.UtcNow > until) throw new TimeoutException();
                Thread.Sleep(10);
            }
        }
    }

    private static SetProperty<Sector, double> Floor(Sector s, double h) => new(s, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, h);

    [Theory]
    [InlineData("doom")]
    [InlineData("heretic")]
    [InlineData("hexen")]
    [InlineData("strife")]
    public void TheStarterRoomIsSoundAndHasAPlayerStart(string engine)
    {
        var m = StarterRoom.Create("MAP01", engine);
        Assert.Empty(MapValidator.Check(m));
        Assert.Contains(m.Things, t => t.Type == 1);
        var map = EditorMap.FromModel(m);
        var geometry = Editor.Geometry.MapGeometry.Build(map.Frame);
        // The player start stands inside the room, so the room's walls face inward.
        Assert.Same(map.Sectors[0], geometry.SectorAt(new Editor.Geometry.MapPoint(map.Things[0].X, map.Things[0].Y)));
    }

    [Fact]
    public void AChangeIsKeptWithoutAskingAndClosingFlushes()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        session.History.Run(Floor(session.Map.Sectors[0], 24));
        Assert.Equal(SaveState.Pending, session.State);

        // After the save delay, with nobody asking, the change is on disk.
        ui.PumpUntil(() => session.State == SaveState.Saved, TimeSpan.FromSeconds(10));
        Assert.Equal(24, Project.Open(project.Folder).LoadMap("MAP01").Sectors[0].FloorHeight);

        // A change still inside the delay reaches the disk when the session is flushed.
        session.History.Run(Floor(session.Map.Sectors[0], 48));
        var flush = session.FlushAsync();
        ui.PumpUntil(() => flush.IsCompleted, TimeSpan.FromSeconds(10));
        Assert.Equal(48, Project.Open(project.Folder).LoadMap("MAP01").Sectors[0].FloorHeight);
    }

    [Fact]
    public void OpeningAMapKeepsAVersionWithAPicture()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", _ => { });
        var folder = Path.Combine(project.Folder, ".wadsmith", "versions", "MAP01");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((!Directory.Exists(folder) || Directory.GetFiles(folder, "*.png").Length == 0) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.Single(Directory.GetFiles(folder, "*.wad"));
        Assert.Single(Directory.GetFiles(folder, "*.png"));
    }

    [Fact]
    public void TurningAndMirroringTheSelectionAreOneStepEachAndWaitForAChangeMadeMeanwhile()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        session.DrawRoom([new(512, 0), new(512, 128), new(768, 128), new(768, 0)]);
        var room = Assert.Single(session.Selection.Items.OfType<Sector>());
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        (double, double, double, double) Bounds()
        {
            var points = session.Map.Lines.Where(l => l.OnMap && (l.Front.Sector == room || l.Back?.Sector == room)).SelectMany(l => new[] { l.Start, l.End }).ToList();
            return (points.Min(v => v.X), points.Min(v => v.Y), points.Max(v => v.X), points.Max(v => v.Y));
        }

        // A change made while what turns is gathered: the turn is gathered again and lands once, after it.
        session.TurnSelection(90);
        session.History.Run(Floor(room, 24));
        ui.PumpUntil(() => session.History.UndoLabel == "Turn", TimeSpan.FromSeconds(10));
        Assert.Equal((576.0, -64.0, 704.0, 192.0), Bounds());
        Assert.Equal(24, room.FloorHeight);
        DrawingTests.AssertSound(session.Map);
        Assert.Same(room, Assert.Single(session.Selection.Items));

        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        session.FlipSelection(leftToRight: true);
        ui.PumpUntil(() => session.History.UndoLabel == "Mirror", TimeSpan.FromSeconds(10));
        Assert.Equal((576.0, -64.0, 704.0, 192.0), Bounds());
        DrawingTests.AssertSound(session.Map);

        session.History.Undo();
        session.History.Undo();
        Assert.Equal((512.0, 0.0, 768.0, 128.0), Bounds());
        Assert.Equal(24, room.FloorHeight);
    }

    [Fact]
    public void TheInspectorTurnsARoomButNotAThingOnItsOwn()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var inspector = new WadSmith.App.ViewModels.InspectorViewModel(session);
        session.Selection.Set(session.Map.Things[0]);
        Assert.DoesNotContain(inspector.Rows, r => r.Label == "Turn left 90°");

        var start = session.Map.Things[0];
        var angle = start.Angle;
        session.Selection.Set(session.Map.Sectors[0]);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Turn left 90°").Command.Execute(null);
        ui.PumpUntil(() => session.History.UndoLabel == "Turn", TimeSpan.FromSeconds(10));
        Assert.Equal((angle + 90) % 360, start.Angle);

        // A typed angle turns it once; the field is ready for the next turn.
        var typed = inspector.Rows.OfType<WadSmith.App.ViewModels.NumberRow>().Single(r => r.Label == "Turn by (°)");
        typed.Value = 90;
        ui.PumpUntil(() => start.Angle == (angle + 180) % 360, TimeSpan.FromSeconds(10));
        Assert.Equal(0, typed.Value);
        DrawingTests.AssertSound(session.Map);
    }

    [Fact]
    public void AWindowMadeInTheSessionIsAWindowInTheInspector()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var inspector = new WadSmith.App.ViewModels.InspectorViewModel(session);
        session.DrawRoom([new(320, -128), new(320, 128), new(576, 128), new(576, -128)]);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        var east = session.Map.Lines.Single(l => l.OnMap && l.Start.X == 256 && l.End.X == 256);
        session.MakeWindow(east, 224, 288);
        var window = Assert.Single(session.Selection.Items.OfType<Sector>());
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        Assert.Equal("Window", inspector.Heading);
        Assert.Contains("64 tall", inspector.Summary);
        Assert.Equal(40, inspector.Rows.OfType<WadSmith.App.ViewModels.NumberRow>().First(r => r.Label == "Sill height").Value);

        var blocks = inspector.Rows.OfType<WadSmith.App.ViewModels.FlagRow>().Single(r => r.Label == "Nothing walks or flies through");
        blocks.Value = true;
        Assert.Equal("Block the window", session.History.UndoLabel);
        Assert.All(session.Map.Lines.Where(l => l.OnMap && l.Back is not null && (l.Front.Sector == window || l.Back.Sector == window)), l => Assert.True(l.Flag("blocking")));

        // Open again and lowered to a step, it is a way through: the Inspector says what it is now.
        blocks.Value = false;
        inspector.Rows.OfType<WadSmith.App.ViewModels.NumberRow>().First(r => r.Label == "Sill height").Value = 16;
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        Assert.Equal("Room area", inspector.Heading);
        DrawingTests.AssertSound(session.Map);
    }

    [Fact]
    public void TheInspectorMakesARoomASecretAndBack()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var inspector = new WadSmith.App.ViewModels.InspectorViewModel(session);
        session.DrawRoom([new(256, -64), new(256, 64), new(384, 64), new(384, -64)]);
        var room = Assert.Single(session.Selection.Items.OfType<Sector>());
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Make it a secret").Command.Execute(null);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        Assert.Equal("Secret area", inspector.Heading);
        Assert.Contains("solid wall on the in-game map", inspector.Summary);
        Assert.True(inspector.Rows.OfType<WadSmith.App.ViewModels.FlagRow>().Single(r => r.Label == "Hidden on the in-game map").Value);

        inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Not a secret").Command.Execute(null);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        Assert.Equal("Room area", inspector.Heading);
        Assert.Equal(0, room.Special);
        Assert.DoesNotContain(session.Map.Lines, l => l.OnMap && l.Flag("secret"));
    }

    [Fact]
    public void ALockedDoorOffersItsKeyAndTheKeyKnowsItsDoor()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var inspector = new WadSmith.App.ViewModels.InspectorViewModel(session);
        string? asked = null;
        inspector.KeyFor += colour => asked = colour;
        session.DrawRoom([new(256, -64), new(256, 64), new(384, 64), new(384, -64)]);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        session.MakeDoor(session.Map.Lines.Single(l => l.OnMap && l.Back is not null));
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version && inspector.Heading == "Door", TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(inspector.Rows, r => r.Label.StartsWith("Place the", StringComparison.Ordinal));

        // Locked with the red key: the Inspector offers to place it, and the key placed opens the door.
        inspector.Rows.OfType<WadSmith.App.ViewModels.ChoiceRow>().Single(r => r.Label == "Locked with").Value = 1;
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Place the red key…").Command.Execute(null);
        Assert.Equal("red", asked);
        session.PlaceKey("red", new Editor.Geometry.MapPoint(-128, 0));
        var key = Assert.Single(session.Selection.Items.OfType<Thing>());
        Assert.Equal(13, key.Type);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        Assert.Contains("Opens", inspector.Summary);
        inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Select the doors it opens").Command.Execute(null);
        Assert.All(session.Selection.Items, e => Assert.Equal(Editor.Smart.Doors.DoorLockedRaise, Assert.IsType<Line>(e).Special));
        Assert.Equal(2, session.Selection.Count);

        // A key outside every room is refused.
        session.PlaceKey("red", new Editor.Geometry.MapPoint(2000, 2000));
        Assert.Single(session.Map.Things, t => t.OnMap && t.Type == 13);
    }

    [Fact]
    public void NewRoomsTakeTheChosenLookOrARoomsOwn()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var hell = Look.Named("doom").Single(l => l.Name == "Hell").Look;
        session.ChooseLook(hell);
        session.DrawRoom([new(512, 0), new(512, 128), new(768, 128), new(768, 0)]);
        var room = Assert.Single(session.Selection.Items.OfType<Sector>());
        Assert.Equal(("FLOOR6_1", "FLAT5_6", 160), (room.FloorTexture, room.CeilingTexture, room.Light));
        Assert.All(session.Map.Lines.Where(l => l.OnMap && l.Front.Sector == room), l => Assert.Equal("SP_HOT1", l.Front.Middle));

        // A room's own look: its pictures, heights, light, and what most of its walls show.
        var starter = session.Map.Sectors[0];
        var like = session.LookOf(starter);
        Assert.Equal(("FLOOR4_8", "CEIL3_5", "STARTAN3", 0.0, 128.0, 192), (like.Floor, like.Ceiling, like.Wall, like.FloorHeight, like.CeilingHeight, like.Light));
        Assert.Equal(hell.Door, like.Door);
    }

    [Fact]
    public void APrefabSavedFromTheSessionIsPlacedAsOneStep()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var saved = false;
        session.PrefabsChanged += () => saved = true;
        session.DrawRoom([new(512, 0), new(512, 128), new(768, 128), new(768, 0)]);
        ui.PumpUntil(() => session.Geometry.Version == session.Map.Version, TimeSpan.FromSeconds(10));
        session.SavePrefab();
        ui.PumpUntil(() => saved, TimeSpan.FromSeconds(10));
        var prefab = Assert.Single(Editor.Editing.Prefabs.In(project.Folder));

        var before = session.Map.Sectors.Count(s => s.OnMap);
        session.PlacePrefab(prefab, new Editor.Geometry.MapPoint(640, 1024));
        ui.PumpUntil(() => session.Map.Sectors.Count(s => s.OnMap) == before + 1, TimeSpan.FromSeconds(10));
        Assert.Equal("Paste", session.History.UndoLabel);
        DrawingTests.AssertSound(session.Map);
        session.History.Undo();
        Assert.Equal(before, session.Map.Sectors.Count(s => s.OnMap));
    }

    [Fact]
    public void MapSettingsAreKeptInTheRecordAndWrittenAsMapInfo()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var changes = 0;
        session.MapSettingsChanged += () => changes++;
        session.ChangeMapSettings(m => m with { Title = "Gateway", Next = ProjectMapInfo.EndToTitle });
        Assert.Equal("Gateway", session.MapEntry.Title);
        Assert.Equal(1, changes);
        var flush = session.FlushAsync();
        ui.PumpUntil(() => flush.IsCompleted, TimeSpan.FromSeconds(20));
        Assert.True(flush.IsCompletedSuccessfully);
        Assert.Equal("Gateway", Project.Open(project.Folder).Info.Maps.Single().Title);
        var path = Path.Combine(project.Folder, ProjectMapInfo.FileName);
        var text = File.ReadAllText(path);
        Assert.Contains("map MAP01 \"Gateway\"", text, StringComparison.Ordinal);
        Assert.Contains("next = \"EndTitle\"", text, StringComparison.Ordinal);

        // The same again changes nothing (the game's defaults arriving counted once meanwhile); back to the game's own values, the file goes.
        var before = changes;
        session.ChangeMapSettings(m => m with { Title = "Gateway" });
        Assert.Equal(before, changes);
        session.ChangeMapSettings(m => m with { Title = "", Next = null });
        var again = session.FlushAsync();
        ui.PumpUntil(() => again.IsCompleted, TimeSpan.FromSeconds(20));
        Assert.True(again.IsCompletedSuccessfully);
        Assert.False(File.Exists(path));
        Assert.Equal("", Project.Open(project.Folder).Info.Maps.Single().Title);
    }

    /// <summary>A texture dropped on one part of a face in 3D changes that part alone, one undo step, and selects the face.</summary>
    [Fact]
    public void PaintingASurfaceChangesThatPartAlone()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(SceneTests.TwoRooms(floor2: 32, ceiling2: 96));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var shared = session.Map.Lines.Single(l => l.OnMap && l.Back is not null);
        var face = shared.Front.Sector.FloorHeight == 0 ? shared.Front : shared.Back!;
        Assert.Null(session.PaintSurface(new Editor.Scene.SurfaceTarget(face, Editor.Scene.WallPart.Bottom, false), "BRICK1"));
        Assert.Equal(("UPPER", "-", "BRICK1"), (face.Top, face.Middle, face.Bottom));
        Assert.Equal("Put a texture on a wall", session.History.UndoLabel);
        Assert.Same(face, session.Selection.Items.Single());

        var room = face.Sector;
        Assert.Null(session.PaintSurface(new Editor.Scene.SurfaceTarget(room, Editor.Scene.WallPart.Middle, true), "F_SKY1"));
        Assert.Equal(("FLOOR", "F_SKY1"), (room.FloorTexture, room.CeilingTexture));
        session.History.Undo();
        session.History.Undo();
        Assert.Equal(("LOWER", "CEIL"), (face.Bottom, room.CeilingTexture));
        Assert.Contains("wall, a floor or a ceiling", session.PaintSurface(new Editor.Scene.SurfaceTarget(session.Map.Vertices[0], Editor.Scene.WallPart.Middle, false), "BRICK1"), StringComparison.Ordinal);
    }

    /// <summary>A floor or ceiling moved from the 3D view: the selected rooms go together, one undo step per gesture.</summary>
    [Fact]
    public void RaisingASurfaceMovesTheSelectedRoomsTogetherOneStepPerGesture()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(SceneTests.TwoRooms(floor2: 32, ceiling2: 96));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        var (one, two) = (session.Map.Sectors[0], session.Map.Sectors[1]);
        session.Selection.Set([one, two]);
        var gesture = History.NewGesture();
        var floor = new Editor.Scene.SurfaceTarget(one, Editor.Scene.WallPart.Middle, false);
        Assert.Equal(8, session.RaiseSurface(floor, 8, gesture));
        Assert.Equal(16, session.RaiseSurface(floor, 8, gesture));
        Assert.Equal((16.0, 48.0), (one.FloorHeight, two.FloorHeight));
        Assert.Equal("Change floor height", session.History.UndoLabel);
        Assert.True(session.History.Undo());
        Assert.Equal((0.0, 32.0), (one.FloorHeight, two.FloorHeight));
        Assert.Equal(16, session.RaiseSurface(floor, 16, History.NewGesture()));

        // A room outside the selection moves alone; a ceiling is its own step; nothing else is a surface.
        session.Selection.Clear();
        Assert.Equal(120, session.RaiseSurface(new Editor.Scene.SurfaceTarget(one, Editor.Scene.WallPart.Middle, true), -8, History.NewGesture()));
        Assert.Equal((120.0, 96.0), (one.CeilingHeight, two.CeilingHeight));
        Assert.Equal("Change ceiling height", session.History.UndoLabel);
        Assert.Null(session.RaiseSurface(new Editor.Scene.SurfaceTarget(session.Map.Lines[0], Editor.Scene.WallPart.Middle, false), 8, 0));
        Assert.Null(session.RaiseSurface(floor, 0, 0));
    }
}
