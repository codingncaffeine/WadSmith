using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>A lift in the real editor: a click on one of its walls shows the lift in the Inspector, as a click on a door's face shows the door.</summary>
[Collection(TimingCollection.Name)]
public sealed class LiftInTheWindowTests
{
    [Fact]
    public async Task TheInspectorShowsTheLiftForOneOfItsWalls()
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
                var x1 = session.Map.Vertices.Where(v => v.OnMap).Max(v => v.X);

                // A small room against the first, its floor raised, made a lift.
                session.DrawRoom([new(x1, -64), new(x1 + 128, -64), new(x1 + 128, 64), new(x1, 64)]);
                Ui.Pump(0.5);
                var ledge = session.Map.Sectors.Single(s => s.OnMap && s.Slot != 0);
                session.History.Record("Raise", e => e.Set(ledge, s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64.0));
                Ui.Pump(0.5);
                session.MakeLift(ledge);
                Ui.Pump(0.8);
                var lift = Lifts.Recognise(session.Geometry, ledge);
                var said = new List<string> { $"lift: {lift?.Triggers.Count} walls send it down, {editor.Inspector.Heading}" };

                // One of its walls selected, as a click on the plan selects it: still the lift.
                session.Selection.Set(lift!.Triggers.First());
                Ui.Pump(0.3);
                said.Add($"its wall selected: {editor.Inspector.Heading}; {string.Join(", ", editor.Inspector.Rows.Where(r => r.Label is "Select the lift's area" or "THIS WALL").Select(r => r.Label))}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["lift: 1 walls send it down, Lift", "its wall selected: Lift; Select the lift's area, THIS WALL"], seen);
    }

    [Fact]
    public async Task TheInspectorShowsTheWindowForOneOfItsWalls()
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
                var x1 = session.Map.Vertices.Where(v => v.OnMap).Max(v => v.X);

                // A room against the first, and a window in the opening between them.
                session.DrawRoom([new(x1, -128), new(x1 + 256, -128), new(x1 + 256, 128), new(x1, 128)]);
                Ui.Pump(0.5);
                var opening = session.Map.Lines.Single(l => l.OnMap && l.Back is not null);
                session.MakeWindow(opening, 32, 96);
                Ui.Pump(0.8);
                var pane = session.Map.Sectors.Single(s => s.OnMap && WallWindows.Recognise(session.Geometry, s) is not null);
                var found = WallWindows.Recognise(session.Geometry, pane)!;
                var said = new List<string> { $"window: {found.Faces.Count} faces, {editor.Inspector.Heading}" };

                session.Selection.Set(found.Faces.First());
                Ui.Pump(0.3);
                said.Add($"its wall selected: {editor.Inspector.Heading}; {string.Join(", ", editor.Inspector.Rows.Where(r => r.Label is "Select the window's area" or "THIS WALL").Select(r => r.Label))}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["window: 2 faces, Window", "its wall selected: Window; Select the window's area, THIS WALL"], seen);
    }
}
