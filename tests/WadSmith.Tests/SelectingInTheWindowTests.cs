using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// The veteran's selection keys in the real editor (D58): Ctrl+A, Ctrl+I, Ctrl+L, Ctrl+Shift+C/V, Shift+Home, [ and ],
/// pressed on the plan; and the Inspector's select-alike rows.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class SelectingInTheWindowTests
{
    [Fact]
    public async Task TheSelectionKeysWorkOnThePlan()
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
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                var start = session.Map.Sectors.Single(s => s.OnMap);
                session.DrawRoom([new(512, 0), new(512, 128), new(768, 128), new(768, 0)]);
                var drawn = session.Map.Sectors.Single(s => s.OnMap && s != start);
                Ui.Pump(0.3);
                canvas.Focus();
                void Press(Key key, RawInputModifiers mods = RawInputModifiers.None)
                {
                    window.KeyPress(key, mods, PhysicalKey.None, null);
                    Ui.Pump(0.05);
                    for (var i = 0; i < 100 && session.Geometry.Version != session.Map.Version; i++) Ui.Pump(0.02);
                    Ui.Pump(0.1);
                }

                string Selected() => string.Join(" + ", session.Selection.Items.Select(e => e switch
                {
                    Sector s when s == start => "start room",
                    Sector => "drawn room",
                    Thing => "player start",
                    _ => e.GetType().Name,
                }).Order());

                var said = new List<string>();
                Press(Key.A, RawInputModifiers.Control);
                said.Add($"Ctrl+A: {Selected()}");

                session.Selection.Set(start);
                Press(Key.I, RawInputModifiers.Control);
                said.Add($"Ctrl+I from the start room: {Selected()}");

                Press(Key.Escape);
                said.Add($"Esc: {Selected()}");
                Press(Key.L, RawInputModifiers.Control);
                said.Add($"Ctrl+L: {Selected()}");

                // Copy the drawn room's properties, paste them onto the start room.
                session.History.Record("Dress", e => e.Set(drawn, s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NUKAGE1"));
                session.Selection.Set(drawn);
                Press(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
                session.Selection.Set(start);
                Press(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift);
                said.Add($"Ctrl+Shift+C then V: the start room's floor is {start.FloorTexture}, undo says {session.History.UndoLabel}");

                // The grid: ] coarser than the step it snaps to now, [ back.
                var before = canvas.GridStepNow;
                Press(Key.OemCloseBrackets);
                var coarser = canvas.GridStepNow;
                Press(Key.OemOpenBrackets);
                Press(Key.OemOpenBrackets);
                said.Add($"grid: ] {coarser / before}× coarser, then [ [ {before / canvas.GridStepNow}× finer than at first");

                // Shift+Home brings the selected drawn room to the middle of the plan.
                session.Selection.Set(drawn);
                canvas.FitAround(new Editor.Geometry.MapPoint(-2000, -2000), 200);
                Ui.Pump(0.2);
                Press(Key.Home, RawInputModifiers.Shift);
                var middle = canvas.ToScreen(new Editor.Geometry.MapPoint(640, 64));
                said.Add($"Shift+Home: the drawn room is {(middle.X > 0 && middle.X < canvas.Bounds.Width && middle.Y > 0 && middle.Y < canvas.Bounds.Height ? "in view" : "out of view")}");

                // The Inspector's select-alike row for the start room picks the rooms with its floor picture: now both.
                session.Selection.Set(start);
                Ui.Pump(0.2);
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Same floor picture").Command.Execute(null);
                for (var i = 0; i < 50 && session.Selection.Count < 2; i++) Ui.Pump(0.02);
                said.Add($"Inspector, Same floor picture: {Selected()}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "Ctrl+A: drawn room + player start + start room",
            "Ctrl+I from the start room: drawn room",
            "Esc: ",
            "Ctrl+L: drawn room",
            "Ctrl+Shift+C then V: the start room's floor is NUKAGE1, undo says Paste the room's properties",
            "grid: ] 2× coarser, then [ [ 2× finer than at first",
            "Shift+Home: the drawn room is in view",
            "Inspector, Same floor picture: drawn room + start room",
        ], seen);
    }

    [Fact]
    public async Task TheRoomsEffectTheLightViewAndTheNextMap()
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
                var room = session.Map.Sectors.Single(s => s.OnMap);
                var said = new List<string>();

                // The Inspector's Effect: chosen by name, the secret it already was kept.
                session.MakeSecret(room);
                session.Selection.Set(room);
                Ui.Pump(0.2);
                var effect = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Effect");
                effect.Value = effect.Options.ToList().IndexOf("Hurts a little (5% a second, nukage)");
                Ui.Pump(0.1);
                said.Add($"effect: special {room.Special}, shown as {effect.Options[effect.Value]}");

                // The toolbar's Brightness button paints the plan in greys by brightness.
                var light = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Content as string == "Brightness");
                light.IsChecked = true;
                Ui.Pump(0.1);
                said.Add($"Brightness: {editor.ViewMode}");

                // A second map in the project: Next map opens it, Previous map comes back.
                var first = session.Slot;
                var second = first == "MAP01" ? "MAP02" : "E1M2";
                session.Project.SaveMap(Editor.StarterRoom.Create(second, "doom"));
                editor.Commands().Single(c => c.Name == "Next map").Run();
                for (var i = 0; i < 100 && (vm.Page as EditorViewModel)?.Session.Slot != second; i++) Ui.Pump(0.05);
                said.Add($"Next map: {((vm.Page as EditorViewModel)?.Session.Slot == second ? "the second" : (vm.Page as EditorViewModel)?.Session.Slot)}");
                ((EditorViewModel)vm.Page!).Commands().Single(c => c.Name == "Previous map").Run();
                for (var i = 0; i < 100 && (vm.Page as EditorViewModel)?.Session.Slot != first; i++) Ui.Pump(0.05);
                said.Add($"Previous map: {((vm.Page as EditorViewModel)?.Session.Slot == first ? "the first" : (vm.Page as EditorViewModel)?.Session.Slot)}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["effect: special 1095, shown as Hurts a little (5% a second, nukage)", "Brightness: Brightness", "Next map: the second", "Previous map: the first"], seen);
    }
}
