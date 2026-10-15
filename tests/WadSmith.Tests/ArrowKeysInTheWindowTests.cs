using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// The arrow keys on the plan (D65, from Ultimate Doom Builder): they move what is selected a grid step at a time (Shift: one
/// unit), a run of presses being one undo step; with nothing selected, or with Doom Builder's keys, they scroll the plan as
/// UDB's and Eureka's do, and Ctrl with them moves the selection.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ArrowKeysInTheWindowTests
{
    [Fact]
    public async Task TheArrowsMoveTheSelectionOrScrollThePlan()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First();
                var start = session.Map.Sectors.Single(s => s.OnMap);
                session.DrawRoom([new(512, 0), new(512, 128), new(768, 128), new(768, 0)]);
                var drawn = session.Map.Sectors.Single(s => s.OnMap && s != start);
                var corner = session.Map.Vertices.Single(v => v.OnMap && v.X == 512 && v.Y == 0);
                Ui.Pump(0.3);
                canvas.Focus();
                void Press(Key key, RawInputModifiers mods = RawInputModifiers.None)
                {
                    window.KeyPress(key, mods, PhysicalKey.None, null);
                    Ui.Pump(0.02);
                }

                // A run is over when the plan has let it go and the shapes have caught up with the map.
                void Settle() => Ui.Until(() => !canvas.IsGesturing && session.Geometry.Version == session.Map.Version, 10);
                string Corner() => $"{corner.X:0},{corner.Y:0}";
                var said = new List<string>();
                var step = canvas.GridStepNow;

                // Three steps east and one unit north in one run: one undo step, which takes it all back.
                session.Selection.Set(drawn);
                Ui.Pump(0.2);
                var steps = session.History.Steps.Count;
                Press(Key.Right);
                Press(Key.Right);
                Press(Key.Right);
                Press(Key.Up, RawInputModifiers.Shift);
                Settle();
                said.Add($"a run: {(corner.X - 512) / step} grid steps east and {corner.Y:0} north, {session.History.Steps.Count - steps} undo step \"{session.History.UndoLabel}\"");
                Press(Key.Z, RawInputModifiers.Control);
                Settle();
                said.Add($"Ctrl+Z: {Corner()}");

                // Another key lets the run go at once: Ctrl+Z as soon as the room is seen to move takes back that press.
                Press(Key.Left);
                Ui.Until(() => corner.X != 512, 10);
                Press(Key.Z, RawInputModifiers.Control);
                Settle();
                said.Add($"Left then Ctrl+Z at once: {Corner()}, {(session.History.CanRedo ? "redo offered" : "nothing to redo")}");

                // Nothing selected: the plan scrolls, the map stays.
                session.Selection.Clear();
                Ui.Pump(0.1);
                var version = session.Map.Version;
                var before = canvas.ToScreen(new MapPoint(0, 0));
                Press(Key.Right);
                Press(Key.Up);
                var after = canvas.ToScreen(new MapPoint(0, 0));
                said.Add($"nothing selected: the map's middle went {after.X - before.X:0} across and {after.Y - before.Y:0} down the screen, map {(session.Map.Version == version ? "unchanged" : "changed")}");

                // Doom Builder's keys: the arrows scroll even with a selection; Ctrl moves it.
                App.App.Services!.Settings.Current.DoomBuilderKeys = true;
                editor.VeteranChanged();
                Ui.Pump(0.1);
                session.Selection.Set(drawn);
                Ui.Pump(0.2);
                Press(Key.Right);
                Settle();
                said.Add($"Doom Builder keys, Right: {Corner()}");
                Press(Key.Right, RawInputModifiers.Control);
                Settle();
                said.Add($"Doom Builder keys, Ctrl+Right: {(corner.X - 512) / step} grid step east");
                return said;
            }
            finally
            {
                App.App.Services!.Settings.Current.DoomBuilderKeys = false;
                window.Close();
            }
        });
        Assert.Equal([
            "a run: 3 grid steps east and 1 north, 1 undo step \"Move\"",
            "Ctrl+Z: 512,0",
            "Left then Ctrl+Z at once: 512,0, redo offered",
            "nothing selected: the map's middle went -120 across and 120 down the screen, map unchanged",
            "Doom Builder keys, Right: 512,0",
            "Doom Builder keys, Ctrl+Right: 1 grid step east",
        ], seen);
    }
}
