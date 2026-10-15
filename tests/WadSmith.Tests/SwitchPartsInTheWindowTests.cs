using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ThreeD;
using WadSmith.App.ViewModels;
using WadSmith.Editor.App;
using WadSmith.Editor.Smart;
using static WadSmith.Tests.DoorPartsInTheWindowTests;

namespace WadSmith.Tests;

/// <summary>
/// A switch is one thing, as a door is: clicked in the 3D view (its face) it is the switch in the Inspector, not a wall face,
/// and Delete takes it away, the wall it was set into whole again.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class SwitchPartsInTheWindowTests
{
    [Fact]
    public async Task AnExitSwitchClickedInThe3DViewIsTheSwitchAndDeletingItGivesTheWallBack()
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
                var room = session.Map.Sectors.Single(s => s.OnMap);
                view.LookAt(AreaMiddle(session, room), room.FloorHeight, 640);
                Ui.Pump(0.5);
                var camera = view.CurrentCamera;
                var wall = session.Map.Lines.Where(l => l.OnMap && l.Back is null).OrderByDescending(l => Facing(l, camera.X, camera.Y)).First();
                var walls = session.Map.Lines.Count(l => l.OnMap);

                // The wall clicked on the plan, then "Make it the exit switch" in its Inspector.
                editor.Tool = EditorTool.Select;
                canvas.FitAround(Middle(wall), 768);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, Middle(wall));
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make it the exit switch").Command.Execute(null);
                Ui.Until(() => session.Map.Lines.Any(l => l.OnMap && Exits.IsExit(l)), 10);
                var exit = session.Map.Lines.Single(l => l.OnMap && Exits.IsExit(l));
                var said = new List<string>();

                // The switch's picture clicked in the 3D view.
                var aim = (Middle(exit).X, Middle(exit).Y, room.FloorHeight + 32);
                view.LookAt(Middle(exit), room.FloorHeight + 32, 320);
                Ui.Until(() => view.SceneVersion >= session.Map.Version, 10);
                Ui.Pump(0.5);
                Click3D(window, view, aim);
                Ui.Until(() => session.Selection.Count == 1, 10);
                Ui.Pump(0.2);
                said.Add($"clicked: {session.Selection.Items.FirstOrDefault()?.GetType().Name}, {editor.Inspector.Heading}, last row {editor.Inspector.Rows.Last().Label}");

                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Ui.Until(() => !session.Map.Lines.Any(l => l.OnMap && Exits.IsExit(l)) && session.Geometry.Version == session.Map.Version, 10);
                Ui.Pump(0.2);
                said.Add($"deleted: {(session.Map.Lines.Any(l => l.OnMap && Exits.IsExit(l)) ? "an exit" : "no exit")}, undo says {session.History.UndoLabel}, {session.Map.Sectors.Count(s => s.OnMap)} area, {session.Map.Lines.Count(l => l.OnMap) - walls} walls more than at first");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(["clicked: Side, Exit switch, last row Delete this switch", "deleted: no exit, undo says Delete the switch, 1 area, 0 walls more than at first"], seen);
    }
}
