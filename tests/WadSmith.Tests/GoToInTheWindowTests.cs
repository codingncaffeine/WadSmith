using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Editor.App;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// Go to a point (D65, Ultimate Doom Builder's Go To Coordinates): the status strip shows where the pointer is on the plan, and
/// its button (or Ctrl+G) opens a box whose point, typed and entered, goes to the middle of the plan; words are refused;
/// with the 3D view alone the camera stands there.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class GoToInTheWindowTests
{
    private static void ClickIn(TopLevel root, Visual target)
    {
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? throw new InvalidOperationException("Not on screen.");
        root.MouseMove(middle, RawInputModifiers.None);
        root.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        root.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.1);
    }

    [Fact]
    public async Task ThePointersPlaceShowsAndItsBoxGoesToAPoint()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.Layout = MapLayout.Split;
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First();
                Ui.Pump(0.3);
                Point At(double x, double y) => canvas.TranslatePoint(canvas.ToScreen(new MapPoint(x, y)), window)!.Value;
                var button = window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Go to a point");
                var flyout = (Flyout)FlyoutBase.GetAttachedFlyout(button)!;
                var box = (Control)flyout.Content!;
                TextBox Field(string name) => box.GetVisualDescendants().OfType<TextBox>().First(t => AutomationProperties.GetName(t) == name);
                void Type(TextBox field, string text)
                {
                    var root = TopLevel.GetTopLevel(field)!;
                    ClickIn(root, field);
                    field.SelectAll();
                    root.KeyTextInput(text);
                    Ui.Pump(0.05);
                }

                void Enter()
                {
                    TopLevel.GetTopLevel(Field("Go to X"))!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                    Ui.Pump(0.2);
                }

                string Middle(MapPoint p)
                {
                    var s = canvas.ToScreen(p);
                    return $"{s.X - canvas.Bounds.Width / 2:0} right of the plan's middle and {s.Y - canvas.Bounds.Height / 2:0} below it";
                }

                var said = new List<string>();

                // The pointer over the plan: its place in the status strip.
                window.MouseMove(At(96, -32), RawInputModifiers.None);
                Ui.Pump(0.05);
                said.Add($"pointer: {button.Content}");

                // The button opens the box with that place; a place typed and Enter put it in the middle of the plan's open part.
                ClickIn(window, button);
                Assert.True(Ui.Until(() => flyout.IsOpen && TopLevel.GetTopLevel(box) is not null, 10), "The box did not open.");
                said.Add($"opened with {Field("Go to X").Text}, {Field("Go to Y").Text}");
                Type(Field("Go to X"), "2048");
                Type(Field("Go to Y"), "-1024");
                Enter();
                said.Add($"Enter: 2048, -1024 is {Middle(new MapPoint(2048, -1024))}; the box is {(flyout.IsOpen ? "open" : "closed")}");

                // Ctrl+G on the plan opens it again with where the pointer is; words are refused and the plan stays.
                window.MouseMove(At(2000, -1000), RawInputModifiers.None);
                canvas.Focus();
                window.KeyPress(Key.G, RawInputModifiers.Control, PhysicalKey.None, null);
                Assert.True(Ui.Until(() => flyout.IsOpen && TopLevel.GetTopLevel(box) is not null, 10), "Ctrl+G did not open the box.");
                said.Add($"Ctrl+G opened with {Field("Go to X").Text}, {Field("Go to Y").Text}");
                Type(Field("Go to X"), "east");
                Enter();
                said.Add($"words: \"{editor.Status}\", 2048, -1024 still {Middle(new MapPoint(2048, -1024))}");

                // The 3D view alone: the camera stands at the point.
                editor.Layout = MapLayout.ThreeD;
                Ui.Pump(0.3);
                window.KeyPress(Key.G, RawInputModifiers.Control, PhysicalKey.None, null);
                Assert.True(Ui.Until(() => flyout.IsOpen && TopLevel.GetTopLevel(box) is not null, 10), "Ctrl+G did not open the box with the 3D view alone.");
                Type(Field("Go to X"), "64");
                Type(Field("Go to Y"), "-96");
                Enter();
                var camera = editor.ViewCamera!();
                said.Add($"3D alone: the camera stands at {camera.X:0}, {camera.Y:0}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "pointer: 96, -32",
            "opened with 96, -32",
            "Enter: 2048, -1024 is 50 right of the plan's middle and 0 below it; the box is closed",
            "Ctrl+G opened with 2000, -1000",
            "words: \"Type the point as two numbers: how far east (x) and how far north (y), such as 512 and -64.\", 2048, -1024 still 50 right of the plan's middle and 0 below it",
            "3D alone: the camera stands at 64, -96",
        ], seen);
    }
}
