using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// A box drawn on the plan with Ultimate Doom Builder's keys (D65): no key replaces the selection, Shift adds to it, Ctrl
/// takes away from it, Ctrl+Shift keeps only what is in both.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class BoxKeysInTheWindowTests
{
    [Fact]
    public async Task ShiftAddsCtrlTakesAwayAndBothKeepWhatIsInBoth()
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
                var rooms = new Dictionary<Sector, string>();
                foreach (var (x, name) in new[] { (512, "A"), (768, "B"), (1024, "C") })
                {
                    var had = session.Map.Sectors.Where(s => s.OnMap).ToHashSet();
                    session.DrawRoom([new(x, 0), new(x, 128), new(x + 128, 128), new(x + 128, 0)]);
                    Ui.Until(() => session.Map.Sectors.Count(s => s.OnMap) > had.Count, 10);
                    rooms[session.Map.Sectors.Single(s => s.OnMap && !had.Contains(s))] = name;
                }

                editor.Commands().Single(c => c.Name == "Select rooms only").Run();
                canvas.FitAround(new MapPoint(840, 64), 900);
                Ui.Pump(0.3);
                var changes = 0;
                session.Selection.Changed += () => changes++;
                Point At(double x, double y) => canvas.TranslatePoint(canvas.ToScreen(new MapPoint(x, y)), window)!.Value;
                string Box(double x0, double x1, RawInputModifiers keys)
                {
                    // The selection changes once when the box's rooms arrive, and once more first for a box with no key (it
                    // starts by clearing the selection); the arrival may come while the button is let go.
                    var before = changes;
                    var expected = keys == RawInputModifiers.None ? 2 : 1;
                    window.MouseMove(At(x0, -64), keys);
                    window.MouseDown(At(x0, -64), MouseButton.Left, keys);
                    window.MouseMove(At((x0 + x1) / 2, 64), keys | RawInputModifiers.LeftMouseButton);
                    window.MouseMove(At(x1, 192), keys | RawInputModifiers.LeftMouseButton);
                    window.MouseUp(At(x1, 192), MouseButton.Left, keys);
                    Ui.Until(() => changes >= before + expected, 10);
                    return string.Join(" ", session.Selection.Items.Select(e => e is Sector s && rooms.TryGetValue(s, out var n) ? n : e.GetType().Name).Order());
                }

                return new List<string>
                {
                    $"box over A and B: {Box(480, 912, RawInputModifiers.None)}",
                    $"Ctrl, box over B: {Box(736, 912, RawInputModifiers.Control)}",
                    $"Shift, box over B and C: {Box(736, 1168, RawInputModifiers.Shift)}",
                    $"Ctrl+Shift, box over B and C: {Box(736, 1168, RawInputModifiers.Control | RawInputModifiers.Shift)}",
                    $"box over C: {Box(992, 1168, RawInputModifiers.None)}",
                };
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "box over A and B: A B",
            "Ctrl, box over B: A",
            "Shift, box over B and C: A B C",
            "Ctrl+Shift, box over B and C: B C",
            "box over C: C",
        ], seen);
    }
}
