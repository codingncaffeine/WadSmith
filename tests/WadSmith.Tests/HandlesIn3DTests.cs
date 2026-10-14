using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ThreeD;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Handles in the 3D view (D49, Phase 5b), as the user goes: a room's corner dragged, a face's wall slid sideways, a window's sill raised; each one undo step.</summary>
[Collection(TimingCollection.Name)]
public sealed class HandlesIn3DTests
{
    [Fact]
    public async Task CornersWallsAndWindowSillsAreDraggedInThe3DView()
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
                // The handles and tools are aimed from the orbit camera's view of the whole room (the view opens flying at the start).
                editor.CameraMode = CameraMode.Orbit;
                Ui.Pump(1.5);
                var view = window.GetVisualDescendants().OfType<View3D>().First(v => v.IsEffectivelyVisible);
                var room = session.Map.Sectors.First(s => s.OnMap);
                var floor = room.FloorHeight;
                var camera = view.CurrentCamera;
                var lines = new List<string>();

                Point Screen(double x, double y, double z) => view.TranslatePoint(view.ScreenPoint(x, y, z)!.Value, window)!.Value;
                void Drag(Point from, Point to)
                {
                    window.MouseMove(from, RawInputModifiers.None);
                    Ui.Pump(0.05);
                    window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
                    for (var i = 1; i <= 6; i++) window.MouseMove(new Point(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6), RawInputModifiers.LeftMouseButton);
                    window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
                    Ui.Pump(0.3);
                }

                bool Faces(Line l) => -(l.End.Y - l.Start.Y) * (camera.X - (l.Start.X + l.End.X) / 2) + (l.End.X - l.Start.X) * (camera.Y - (l.Start.Y + l.End.Y) / 2) < 0;
                (double X, double Y) Normal(Line l)
                {
                    var (dx, dy) = (l.End.X - l.Start.X, l.End.Y - l.Start.Y);
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    return (-dy / length, dx / length);
                }

                // A corner of the selected room, the one nearest the camera's view of the far walls, 64 further out.
                session.Selection.Set(room);
                Ui.Pump(0.3);
                var far = session.Map.Lines.First(l => l.OnMap && l.Back is null && Faces(l));
                var corner = far.End;
                var (cx, cy) = (corner.X, corner.Y);
                var (nx, ny) = Normal(far);
                Drag(Screen(cx, cy, floor), Screen(cx + nx * 64, cy + ny * 64, floor));
                lines.Add($"corner from {cx:0},{cy:0} to {corner.X:0},{corner.Y:0}; undo says {session.History.UndoLabel}");
                session.History.Undo();
                Ui.Pump(0.3);
                lines.Add($"undone: {corner.X:0},{corner.Y:0}");

                // The face of that wall, its middle handle slid 32 out.
                session.Selection.Set(far.Front);
                Ui.Pump(0.3);
                var (mx, my) = ((far.Start.X + far.End.X) / 2, (far.Start.Y + far.End.Y) / 2);
                var middleZ = (room.FloorHeight + room.CeilingHeight) / 2;
                var (sx, sy) = (far.Start.X, far.Start.Y);
                Drag(Screen(mx, my, middleZ), Screen(mx + nx * 32, my + ny * 32, middleZ));
                lines.Add($"wall moved by {far.Start.X - sx:0},{far.Start.Y - sy:0}; undo says {session.History.UndoLabel}");
                session.History.Undo();
                Ui.Pump(0.3);

                // A window: a room beyond that wall, a window in the opening, its sill raised by dragging up.
                var (ax, ay, bx, by) = (far.Start.X, far.Start.Y, far.End.X, far.End.Y);
                session.DrawRoom([new(ax, ay), new(ax + nx * 128, ay + ny * 128), new(bx + nx * 128, by + ny * 128), new(bx, by)]);
                Ui.Pump(0.5);
                var opening = session.Map.Lines.First(l => l.OnMap && l.Back is not null);
                session.MakeWindow(opening, opening.Length / 2, opening.Length / 2);
                Ui.Pump(0.5);
                var windowArea = session.Map.Sectors.First(s => s.OnMap && WallWindows.Recognise(session.Geometry, s) is not null);
                var sill = windowArea.FloorHeight;
                session.Selection.Set(windowArea);
                Ui.Pump(0.3);
                var faces = WallWindows.Recognise(session.Geometry, windowArea)!.Faces;
                var (wx, wy) = (faces.Average(l => (l.Start.X + l.End.X) / 2), faces.Average(l => (l.Start.Y + l.End.Y) / 2));
                var at = Screen(wx, wy, sill);
                Drag(at, new Point(at.X, at.Y - 60));
                lines.Add($"sill raised {(windowArea.FloorHeight > sill ? "yes" : "no")}, under the head {(windowArea.FloorHeight <= windowArea.CeilingHeight ? "yes" : "no")}; undo says {session.History.UndoLabel}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("corner from 256,256 to 256,320; undo says Move a corner\nundone: 256,256\nwall moved by 0,32; undo says Move\nsill raised yes, under the head yes; undo says Change floor height", said);
    }
}
