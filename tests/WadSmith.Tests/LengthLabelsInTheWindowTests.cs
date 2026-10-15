using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// Lengths on the plan (D65, Ultimate Doom Builder's line labels): each side of a room drawn corner by corner, the side to
/// the pointer with its angle; and each wall a dragged corner stretches, gone when the drag is let go.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class LengthLabelsInTheWindowTests
{
    [Fact]
    public async Task SidesBeingDrawnAndWallsBeingStretchedShowTheirLengths()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First();
                canvas.FitAround(new MapPoint(512, 0), 1600);
                Ui.Pump(0.3);
                Point At(double x, double y) => canvas.TranslatePoint(canvas.ToScreen(new MapPoint(x, y)), window)!.Value;
                void Click(double x, double y)
                {
                    var p = At(x, y);
                    window.MouseMove(p, RawInputModifiers.None);
                    window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
                    Ui.Pump(0.05);
                }

                string Labels() => canvas.LengthLabels() is { Count: > 0 } labels ? string.Join(", ", labels.Select(l => l.Text).Order()) : "none";
                var said = new List<string>();

                // Corner by corner with the Room tool: the side placed, and the side to the pointer with its angle.
                editor.Tool = EditorTool.Room;
                Ui.Pump(0.1);
                Click(1024, 0);
                Click(1280, 0);
                window.MouseMove(At(1280, 192), RawInputModifiers.None);
                Ui.Pump(0.05);
                said.Add($"drawing: {Labels()}");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
                Ui.Pump(0.05);

                // The starter room's north-east corner dragged 64 east: its two walls with their lengths as they are now. Zoomed in
                // as a user would be to take a corner: further out the plan picks the wall.
                editor.Tool = EditorTool.Select;
                canvas.FitAround(new MapPoint(256, 256), 600);
                Ui.Pump(0.3);
                window.MouseMove(At(256, 256), RawInputModifiers.None);
                window.MouseDown(At(256, 256), MouseButton.Left, RawInputModifiers.None);
                window.MouseMove(At(288, 256), RawInputModifiers.LeftMouseButton);
                Ui.Pump(0.05);
                window.MouseMove(At(320, 256), RawInputModifiers.LeftMouseButton);
                Ui.Until(() => canvas.LengthLabels().Count == 2 && canvas.LengthLabels().Any(l => l.Text == "576"), 10);
                said.Add($"dragging a corner: {Labels()}");
                window.MouseUp(At(320, 256), MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.1);
                said.Add($"let go: {Labels()}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["drawing: 192 · 90°, 256", "dragging a corner: 516, 576", "let go: none"], seen);
    }
}
