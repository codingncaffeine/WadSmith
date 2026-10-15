using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ThreeD;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// The user's report (2026-10-15): a door clicked with the Select tool did not offer how it opens, and deleting it broke the
/// doorway — its face went, and the door ran into its frame. A door is one thing to select, in the plan and in the 3D view,
/// whichever part of it is clicked; deleting it leaves the wall it stood in, as the wall beside it, and the Door tool takes a
/// new door into that wall.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class DoorPartsInTheWindowTests
{
    [Fact]
    public async Task ADoorClickedOnThePlanIsTheDoorAndDeletingItLeavesTheWallANewDoorGoesInto()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                editor.Layout = MapLayout.Split;
                Ui.Pump(0.5);
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var (west, east, wall) = TwoRooms(session, _ => 0);
                var said = new List<string>();

                // The Door tool's click on the long wall between them: a door 64 wide in its middle, wall either side.
                editor.Tool = EditorTool.Door;
                canvas.FitAround(Middle(wall), 768);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, Middle(wall));
                var door = Ui.Until(() => DoorOn(session) is not null) ? DoorOn(session)! : throw new InvalidOperationException("The Door tool made no door: " + editor.Status);
                var found = Doors.Recognise(session.Geometry, door)!;
                var frames = Doors.FramesOf(session.Geometry, found);
                said.Add($"made: a door with {frames.Count} frames, {Slabs(session).Count} pieces of wall beside it");

                // The pieces of wall beside it, as each room sees them: one texture each side, kept to compare with afterwards.
                var westFace = FacesToward(session, west).First();
                var eastFace = FacesToward(session, east).First();
                var (westLook, eastLook) = (Looks(westFace), Looks(eastFace));

                // With the Select tool: the door's middle seen from far off (the whole map in view, the door a few pixels), its
                // frame and its side seen close up. Each is the door, with how it opens.
                editor.Tool = EditorTool.Select;
                var face = found.Own.First();
                var frame = frames[0].Frame;
                var side = session.Map.Lines.First(l => l.OnMap && !found.Own.Contains(l) && (ReferenceEquals(l.Front.Sector, door) || ReferenceEquals(l.Back?.Sector, door)));
                var doorMiddle = AreaMiddle(session, door);
                canvas.FitAround(doorMiddle, 12000);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, doorMiddle);
                said.Add($"door from far off: {Shown(editor)}");
                canvas.FitAround(doorMiddle, 160);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, AreaMiddle(session, frame));
                said.Add($"its frame close up: {Shown(editor)}");
                Shot(window, "door-frame-clicked.png");
                ClickPlan(window, canvas, Middle(side));
                said.Add($"its side close up: {Shown(editor)}");

                // Its face clicked (the user's click), then Delete: the door goes and the wall it stood in closes up.
                ClickPlan(window, canvas, Middle(face));
                said.Add($"its face close up: {Shown(editor)}");
                said.Add($"the Inspector's last row: {editor.Inspector.Rows.Last().Label}");
                canvas.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Ui.Until(() => DoorOn(session) is null && session.Geometry.Version == session.Map.Version, 10);
                Ui.Pump(0.2);
                said.Add($"deleted: {(DoorOn(session) is null ? "no door" : "a door")}, undo says {session.History.UndoLabel}");
                said.Add($"between the rooms: {Between(session, west, east)}");
                var westNow = FacesToward(session, west);
                var eastNow = FacesToward(session, east);
                said.Add($"the wall each side: {westNow.Count} toward the west room, {eastNow.Count} toward the east, {(westNow.All(f => Looks(f) == westLook) && eastNow.All(f => Looks(f) == eastLook) ? "looking as the wall beside the door did" : $"looking {string.Join("/", westNow.Select(Looks))} and {string.Join("/", eastNow.Select(Looks))}")}");
                said.Add($"the rooms: {Rooms(west, east)}");

                // The Door tool's click on that wall: a new door, through the wall from one room to the other.
                editor.Tool = EditorTool.Door;
                canvas.FitAround(Middle(wall), 768);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, Middle(westNow[0].Line!));
                var again = Ui.Until(() => DoorOn(session) is not null) ? DoorOn(session) : null;
                said.Add(again is null ? $"a new door: none ({editor.Status})" : $"a new door: {NewDoor(session, again, west, east)}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, seen));
        Assert.Equal(
        [
            "made: a door with 2 frames, 2 pieces of wall beside it",
            "door from far off: Door, with Opens",
            "its frame close up: Door, with Opens",
            "its side close up: Door, with Opens",
            "its face close up: Door, with Opens",
            "the Inspector's last row: Delete this door",
            "deleted: no door, undo says Delete the door",
            "between the rooms: one piece of wall, closed",
            "the wall each side: 1 toward the west room, 1 toward the east, looking as the wall beside the door did",
            "the rooms: both open, as tall as before",
            "a new door: framed into both rooms, its floor on theirs",
        ], seen);
    }

    [Fact]
    public async Task ADoorClickedInThe3DViewIsTheDoorAndDeletingItLeavesTheWall()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                editor.Layout = MapLayout.Split;
                editor.CameraMode = CameraMode.Orbit;
                Ui.Pump(1.5);
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var view = window.GetVisualDescendants().OfType<View3D>().First(v => v.IsEffectivelyVisible);
                var start = session.Map.Sectors.Single(s => s.OnMap);
                view.LookAt(AreaMiddle(session, start), start.FloorHeight, 640);
                Ui.Pump(0.5);
                var camera = view.CurrentCamera;
                var (west, east, wall) = TwoRooms(session, l => Facing(l, camera.X, camera.Y));
                editor.Tool = EditorTool.Door;
                canvas.FitAround(Middle(wall), 768);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, Middle(wall));
                var door = Ui.Until(() => DoorOn(session) is not null) ? DoorOn(session)! : throw new InvalidOperationException("The Door tool made no door: " + editor.Status);
                var found = Doors.Recognise(session.Geometry, door)!;
                var said = new List<string>();

                // The door's face toward the room the camera looks from, seen through its frame, clicked with the Select tool.
                editor.Tool = EditorTool.Select;
                var face = found.Own.OrderBy(l => Distance(Middle(l), camera.X, camera.Y)).First();
                view.LookAt(Middle(face), west.FloorHeight + 24, 384);
                Ui.Until(() => view.SceneVersion >= session.Map.Version, 10);
                Ui.Pump(0.5);
                var aim = (Middle(face).X, Middle(face).Y, west.FloorHeight + 24);
                Hover(window, view, aim);
                said.Add($"under the pointer: {(view.Hovered?.Element is Side s && ReferenceEquals(s.Line, face) ? "the door's face" : view.Hovered?.Element?.GetType().Name ?? "nothing")}");
                Click3D(window, view, aim);
                Ui.Until(() => session.Selection.Count == 1, 10);
                said.Add($"clicked: {session.Selection.Items.FirstOrDefault()?.GetType().Name}, {Shown(editor)}");
                Shot(window, "door-clicked-3d.png");

                // Delete with the pointer still over the 3D view: the door goes, the wall it stood in closes up.
                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Ui.Until(() => DoorOn(session) is null && session.Geometry.Version == session.Map.Version, 10);
                Ui.Pump(0.2);
                said.Add($"deleted: {(DoorOn(session) is null ? "no door" : "a door")}; between the rooms: {Between(session, west, east)}; the rooms: {Rooms(west, east)}");
                Shot(window, "door-deleted-3d.png");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, seen));
        Assert.Equal(
        [
            "under the pointer: the door's face",
            "clicked: Side, Door, with Opens",
            "deleted: no door; between the rooms: one piece of wall, closed; the rooms: both open, as tall as before",
        ], seen);
    }

    // The starter room and a room drawn beyond one of its walls (the one <paramref name="score"/> rates highest, the first on a
    // tie), 512 deep, sharing the whole wall: the user's map, where the Door tool's click on the long wall makes a door 64 wide in
    // its middle, wall either side.
    internal static (Sector First, Sector Second, Line Wall) TwoRooms(EditorSession session, Func<Line, double> score)
    {
        var first = session.Map.Sectors.Single(s => s.OnMap);
        var wall = session.Map.Lines.Where(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, first)).OrderByDescending(score).First();
        var (nx, ny) = Outward(wall);
        var (a, b) = (new MapPoint(wall.Start.X, wall.Start.Y), new MapPoint(wall.End.X, wall.End.Y));
        session.DrawRoom([a, b, new(b.X + nx * 512, b.Y + ny * 512), new(a.X + nx * 512, a.Y + ny * 512)]);
        if (!Ui.Until(() => session.Map.Sectors.Count(s => s.OnMap) == 2 && session.Geometry.Version == session.Map.Version)) throw new InvalidOperationException("The second room was not drawn.");
        return (first, session.Map.Sectors.Single(s => s.OnMap && !ReferenceEquals(s, first)), session.Map.Lines.Single(l => l.OnMap && l.Back is not null));
    }

    /// <summary>The unit vector across a wall away from its front (out of the room it faces).</summary>
    internal static (double X, double Y) Outward(Line l)
    {
        var (dx, dy) = (l.End.X - l.Start.X, l.End.Y - l.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return (-dy / length, dx / length);
    }

    /// <summary>How squarely the room's side of a one-sided wall looks toward a point (the camera), 1 for straight at it: the far wall, seen from inside.</summary>
    internal static double Facing(Line l, double x, double y)
    {
        var (nx, ny) = Outward(l);
        var (dx, dy) = (x - Middle(l).X, y - Middle(l).Y);
        return (dx * -nx + dy * -ny) / Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
    }

    internal static MapPoint Middle(Line l) => new((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);

    internal static double Distance(MapPoint p, double x, double y) => Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

    internal static MapPoint AreaMiddle(EditorSession session, Sector area)
    {
        var b = session.Geometry.WallBoundsAt(area.Slot)!.Value;
        return new((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
    }

    internal static Sector? DoorOn(EditorSession session) => session.Map.Sectors.FirstOrDefault(s => s.OnMap && Doors.Recognise(session.Geometry, s) is not null);

    /// <summary>The closed areas (floor up on the ceiling) that are no door: the pieces of wall a door is set into.</summary>
    internal static List<Sector> Slabs(EditorSession session) => [.. session.Map.Sectors.Where(s => s.OnMap && s.FloorHeight >= s.CeilingHeight && Doors.Recognise(session.Geometry, s) is null)];

    /// <summary>The faces toward <paramref name="room"/> of the walls between it and a closed area, along the wall's line order.</summary>
    internal static List<Side> FacesToward(EditorSession session, Sector room) =>
        [.. session.Map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back.Sector, room))
                && (ReferenceEquals(l.Front.Sector, room) ? l.Back.Sector : l.Front.Sector) is { } other && other.FloorHeight >= other.CeilingHeight)
            .Select(l => ReferenceEquals(l.Front.Sector, room) ? l.Front : l.Back!)];

    internal static string Looks(Side s) => $"{s.Top}/{s.Middle}/{s.Bottom}";

    /// <summary>What lies between the two rooms besides themselves.</summary>
    internal static string Between(EditorSession session, Sector a, Sector b)
    {
        var rest = session.Map.Sectors.Where(s => s.OnMap && !ReferenceEquals(s, a) && !ReferenceEquals(s, b)).ToList();
        return rest.Count == 1 ? $"one piece of wall, {(rest[0].FloorHeight >= rest[0].CeilingHeight ? "closed" : "open")}" : $"{rest.Count} areas, {rest.Count(s => s.FloorHeight < s.CeilingHeight)} of them open";
    }

    internal static string Rooms(Sector a, Sector b) => a.OnMap && b.OnMap && a.FloorHeight == 0 && b.FloorHeight == 0 && a.CeilingHeight == b.CeilingHeight && a.CeilingHeight > 64
        ? "both open, as tall as before"
        : $"{(a.OnMap ? $"{a.FloorHeight}/{a.CeilingHeight}" : "gone")} and {(b.OnMap ? $"{b.FloorHeight}/{b.CeilingHeight}" : "gone")}";

    /// <summary>A new door's doorway: framed into both rooms, its floor on theirs.</summary>
    internal static string NewDoor(EditorSession session, Sector door, Sector a, Sector b)
    {
        var found = Doors.Recognise(session.Geometry, door)!;
        var rooms = Doors.FramesOf(session.Geometry, found).Select(f => f.Room).ToList();
        var framed = rooms.Count == 2 && rooms.Contains(a) && rooms.Contains(b) ? "framed into both rooms" : $"{rooms.Count} frames, into {string.Join("/", rooms.Select(r => ReferenceEquals(r, a) ? "the first" : ReferenceEquals(r, b) ? "the second" : "another area"))}";
        return $"{framed}, {(door.FloorHeight == a.FloorHeight ? "its floor on theirs" : $"its floor at {door.FloorHeight}")}";
    }

    /// <summary>With WADSMITH_DOOR_SHOT set to a folder, keeps the window as it looks now there, under <paramref name="name"/>.</summary>
    internal static void Shot(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("WADSMITH_DOOR_SHOT") is not { Length: > 0 } folder) return;
        Ui.Pump(1.0);
        using var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
        frame.Save(Path.Combine(folder, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    /// <summary>What the Inspector shows: its heading, and whether it offers how a door opens.</summary>
    internal static string Shown(EditorViewModel editor)
    {
        Ui.Pump(0.2);
        return $"{editor.Inspector.Heading}{(editor.Inspector.Rows.Any(r => r.Label == "Opens") ? ", with Opens" : ", no Opens")}";
    }

    internal static void ClickPlan(MainWindow window, MapCanvas canvas, MapPoint at, RawInputModifiers mods = RawInputModifiers.None)
    {
        var p = canvas.TranslatePoint(canvas.ToScreen(at), window)!.Value;
        window.MouseMove(p, mods);
        Ui.Pump(0.05);
        window.MouseDown(p, MouseButton.Left, mods);
        window.MouseUp(p, MouseButton.Left, mods);
        Ui.Pump(0.3);
    }

    internal static Point Screen(MainWindow window, View3D view, (double X, double Y, double Z) at) =>
        view.TranslatePoint(view.ScreenPoint(at.X, at.Y, at.Z) ?? throw new InvalidOperationException("Behind the camera."), window)!.Value;

    internal static void Hover(MainWindow window, View3D view, (double X, double Y, double Z) at)
    {
        window.MouseMove(Screen(window, view, at), RawInputModifiers.None);
        Ui.Until(() => view.Hovered is { } h && Math.Abs(h.X - at.X) < 6 && Math.Abs(h.Y - at.Y) < 6, 5);
    }

    internal static void Click3D(MainWindow window, View3D view, (double X, double Y, double Z) at, RawInputModifiers mods = RawInputModifiers.None)
    {
        var p = Screen(window, view, at);
        window.MouseMove(p, mods);
        Ui.Pump(0.05);
        window.MouseDown(p, MouseButton.Left, mods);
        window.MouseUp(p, MouseButton.Left, mods);
        Ui.Pump(0.3);
    }
}
