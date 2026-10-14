using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ThreeD;
using WadSmith.Editor.Model;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>
/// Every tool works in the 3D view (D49, Phase 5b), as the user goes: with the 3D view alone, the tool chosen in the column,
/// each gesture made with the pointer over what it works on, and each lands its edit through the same code as the plan.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ToolsIn3DTests
{
    [Fact]
    public async Task EveryToolMakesItsObjectInThe3DView()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                editor.Layout = MapLayout.ThreeD;
                Ui.Pump(1.5);
                var view = window.GetVisualDescendants().OfType<View3D>().First(v => v.IsEffectivelyVisible);
                var room = session.Map.Sectors.First(s => s.OnMap);
                var corners = session.Geometry.LoopsAt(room.Slot).SelectMany(l => l).ToArray();
                var (x0, y0, x1, y1) = (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
                var floor = room.FloorHeight;
                var lines = new List<string>();

                Point Screen(double x, double y, double z) => view.TranslatePoint(view.ScreenPoint(x, y, z) ?? throw new InvalidOperationException("Behind the camera."), window)!.Value;
                void Hover(double x, double y, double z)
                {
                    window.MouseMove(Screen(x, y, z), RawInputModifiers.None);
                    for (var i = 0; i < 100 && !(view.Hovered is { } h && Math.Abs(h.X - x) < 6 && Math.Abs(h.Y - y) < 6); i++) Ui.Pump(0.02);
                }

                void Gesture(string name, (double X, double Y, double Z) from, (double X, double Y, double Z)? to = null)
                {
                    var before = session.Map.Version;
                    Hover(from.X, from.Y, from.Z);
                    var at = Screen(from.X, from.Y, from.Z);
                    window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                    if (to is { } end)
                    {
                        for (var i = 1; i <= 5; i++) window.MouseMove(Screen(from.X + (end.X - from.X) * i / 5, from.Y + (end.Y - from.Y) * i / 5, from.Z + (end.Z - from.Z) * i / 5), RawInputModifiers.LeftMouseButton);
                        at = Screen(end.X, end.Y, end.Z);
                    }

                    window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                    for (var i = 0; i < 100 && session.Map.Version == before; i++) Ui.Pump(0.02);
                    Ui.Pump(0.1);
                    lines.Add($"{name}: {(session.Map.Version == before ? "nothing" : session.History.UndoLabel)}");
                }

                // A solid wall the camera sees from inside the room: its face looks toward the camera.
                var camera = view.CurrentCamera;
                Line Facing(params Line[] except) => session.Map.Lines.Where(l => l.OnMap && l.Back is null && !except.Contains(l) && l.Front.Sector == room)
                    .OrderByDescending(l => -(l.End.Y - l.Start.Y) * (camera.X - (l.Start.X + l.End.X) / 2) + (l.End.X - l.Start.X) * (camera.Y - (l.Start.Y + l.End.Y) / 2) < 0 ? 1 : 0).First();
                (double X, double Y) Out(Line l, double by)
                {
                    var (dx, dy) = (l.End.X - l.Start.X, l.End.Y - l.Start.Y);
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    return ((l.Start.X + l.End.X) / 2 - dy / length * by, (l.Start.Y + l.End.Y) / 2 + dx / length * by);
                }

                var (cx, cy) = ((x0 + x1) / 2, (y0 + y1) / 2);
                editor.Tool = EditorTool.Light;
                Gesture("Light", (cx + 32, cy + 32, floor));
                editor.Tool = EditorTool.Room;
                Gesture("Room on the floor", (cx - 48, cy - 48, floor), (cx + 16, cy + 16, floor));

                // The same room the plan draws from those corners: a box from (cx − 48, cy − 48) to (cx + 16, cy + 16).
                var drawn = session.Map.Sectors.Last(s => s.OnMap);
                var dc = session.Geometry.LoopsAt(drawn.Slot).SelectMany(l => l).ToArray();
                lines.Add($"its corners {dc.Min(p => p.X) - cx:0},{dc.Min(p => p.Y) - cy:0} to {dc.Max(p => p.X) - cx:0},{dc.Max(p => p.Y) - cy:0} from the middle, {dc.Length} of them");
                var wall = Facing();
                var (mx, my) = Out(wall, 0);
                Gesture("Room out of a wall", (mx, my, floor + 32), (Out(wall, 128).X, Out(wall, 128).Y, floor + 32));
                var opening = session.Map.Lines.First(l => l.OnMap && l.Back is not null && Math.Abs((l.Start.X + l.End.X) / 2 - mx) < 1 && Math.Abs((l.Start.Y + l.End.Y) / 2 - my) < 1);
                var (ix, iy) = Out(opening, -12);
                editor.Tool = EditorTool.Window;
                Gesture("Window", (ix, iy, floor));
                editor.Tool = EditorTool.Room;
                var second = Facing(wall);
                var (sx, sy) = Out(second, 0);
                Gesture("Room out of another wall", (sx, sy, floor + 32), (Out(second, 128).X, Out(second, 128).Y, floor + 32));
                var doorway = session.Map.Lines.First(l => l.OnMap && l.Back is not null && Math.Abs((l.Start.X + l.End.X) / 2 - sx) < 1 && Math.Abs((l.Start.Y + l.End.Y) / 2 - sy) < 1);
                var (dx, dy) = Out(doorway, -12);
                editor.Tool = EditorTool.Door;
                Hover(dx, dy, floor);
                for (var i = 0; i < 50 && !editor.Status.StartsWith("Click: a door", StringComparison.Ordinal); i++) Ui.Pump(0.02);
                lines.Add($"over the opening: '{editor.Status}'");
                Gesture("Door", (dx, dy, floor));
                editor.Tool = EditorTool.Stairs;
                Gesture("Stairs", (x0 + 24, y1 - 80, floor), (x0 + 88, y1 - 24, floor));
                editor.Tool = EditorTool.Teleport;
                Gesture("Teleport pad (the small room)", (cx - 16, cy - 16, floor));
                Gesture("Teleport arrival", (cx + 48, cy - 48, floor));
                // The door itself, as the 3D view shows it (it stands beyond its frame), then a solid wall of the first room pulled out.
                var door = session.Map.Sectors.Single(s => s.OnMap && WadSmith.Editor.Smart.Doors.Recognise(session.Geometry, s) is not null);
                var face = session.Map.Lines.Where(l => l.OnMap && (l.Front.Sector == door || l.Back?.Sector == door)).OrderBy(l => Math.Abs((l.Start.X + l.End.X) / 2 - dx) + Math.Abs((l.Start.Y + l.End.Y) / 2 - dy)).First();
                editor.Tool = EditorTool.Switch;
                Gesture("Switch: the door", ((face.Start.X + face.End.X) / 2, (face.Start.Y + face.End.Y) / 2, floor + 32));
                var pulled = opening.Back!.Sector == room ? opening.Front.Sector : opening.Back.Sector;
                var third = session.Map.Lines.Where(l => l.OnMap && l.Back is null && l.Front.Sector == pulled)
                    .OrderByDescending(l => -(l.End.Y - l.Start.Y) * (camera.X - (l.Start.X + l.End.X) / 2) + (l.End.X - l.Start.X) * (camera.Y - (l.Start.Y + l.End.Y) / 2) < 0 ? 1 : 0).First();
                var (tx, ty) = Out(third, 0);
                Gesture("Switch: a solid wall", (tx, ty, floor + 32));
                editor.Tool = EditorTool.Measure;
                Gesture("Measure", (x0 + 16, cy, floor), (x1 - 16, cy, floor));
                for (var i = 0; i < 100 && editor.Profile is null; i++) Ui.Pump(0.02);
                lines.Add($"profile shown {editor.Profile is not null}; tool {editor.Tool}");

                // Side by side with the plan: the tool column is the same, and the 3D view still takes the tools.
                editor.Layout = MapLayout.Split;
                Ui.Pump(1.0);
                lines.Add($"tools shown in the split: {window.GetVisualDescendants().OfType<Avalonia.Controls.StackPanel>().Any(p => p.Name == "ToolColumn" && p.IsEffectivelyVisible)}");
                editor.Tool = EditorTool.Light;
                Gesture("Light in the split", (cx - 64, cy + 64, floor));
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(
            "Light: Place a light\nRoom on the floor: Draw a room\nits corners -48,-48 to 16,16 from the middle, 4 of them\nRoom out of a wall: Draw a room\nWindow: Make a window\nRoom out of another wall: Draw a room\n"
            + "over the opening: 'Click: a door goes in this opening (drag along it for its width).'\nDoor: Make a door\nStairs: Make stairs\nTeleport pad (the small room): nothing\nTeleport arrival: Make a teleporter\n"
            + "Switch: the door: nothing\nSwitch: a solid wall: Make a switch\nMeasure: nothing\nprofile shown True; tool Measure\n"
            + "tools shown in the split: True\nLight in the split: Place a light",
            said);
    }
}
