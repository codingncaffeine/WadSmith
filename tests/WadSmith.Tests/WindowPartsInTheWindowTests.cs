using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ThreeD;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;
using static WadSmith.Tests.DoorPartsInTheWindowTests;

namespace WadSmith.Tests;

/// <summary>
/// A window is one thing, as a door is: clicked in the 3D view (a face) it is the window in the Inspector, and Delete takes it
/// away whole, the wall it was in closing up like the wall beside it, the room as it was. Deleting its face once made the room
/// the window's.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class WindowPartsInTheWindowTests
{
    [Fact]
    public async Task AWindowClickedInThe3DViewIsTheWindowAndDeletingItLeavesTheWall()
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
                var heights = (west.FloorHeight, west.CeilingHeight);

                // The Window tool's click on the long wall between them: a window in its middle, wall either side.
                editor.Tool = EditorTool.Window;
                canvas.FitAround(Middle(wall), 768);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, Middle(wall));
                Sector? WindowOn() => session.Map.Sectors.FirstOrDefault(s => s.OnMap && WallWindows.Recognise(session.Geometry, s) is not null);
                var made = Ui.Until(() => WindowOn() is not null) ? WindowOn()! : throw new InvalidOperationException("The Window tool made no window: " + editor.Status);
                var said = new List<string>();

                // The window's face toward the room the camera looks from, clicked with the Select tool.
                editor.Tool = EditorTool.Select;
                var face = session.Map.Lines.First(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, made) || ReferenceEquals(l.Back.Sector, made)) && ReferenceEquals(ReferenceEquals(l.Front.Sector, made) ? l.Back.Sector : l.Front.Sector, west));
                view.LookAt(Middle(face), made.FloorHeight, 384);
                Ui.Until(() => view.SceneVersion >= session.Map.Version, 10);
                Ui.Pump(0.5);
                Click3D(window, view, (Middle(face).X, Middle(face).Y, made.FloorHeight - 8));
                Ui.Until(() => session.Selection.Count == 1, 10);
                Ui.Pump(0.2);
                said.Add($"clicked: {session.Selection.Items.FirstOrDefault()?.GetType().Name}, {editor.Inspector.Heading}, last row {editor.Inspector.Rows.Last().Label}");

                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Ui.Until(() => WindowOn() is null && session.Geometry.Version == session.Map.Version, 10);
                Ui.Pump(0.2);
                said.Add($"deleted: {(WindowOn() is null ? "no window" : "a window")}, undo says {session.History.UndoLabel}; between the rooms: {Between(session, west, east)}; the room {(west.OnMap && (west.FloorHeight, west.CeilingHeight) == heights ? "as it was" : west.OnMap ? $"now {west.FloorHeight}/{west.CeilingHeight}" : "gone")}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(["clicked: Side, Window, last row Delete this window", "deleted: no window, undo says Delete the window; between the rooms: one piece of wall, closed; the room as it was"], seen);
    }
}
