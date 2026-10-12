using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace WadSmith.Tests;

/// <summary>The Guide's card in the real window: every button stays inside the window, and what the editor refuses or hints shows on the card.</summary>
[Collection(TimingCollection.Name)]
public sealed class GuideCardInTheWindowTests
{
    [Fact]
    public async Task TheStopButtonStaysInsideTheWindowAndTheCardSaysWhatWasRefused()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var tile = vm.Welcome.Games.First();
                Ui.Wait(tile.GuidedMap!.ExecuteAsync(tile));
                Ui.Pump(1.0);
                var editor = (App.ViewModels.EditorViewModel)vm.Page!;
                var guide = editor.Guide!;
                guide.SkipCommand.Execute(null);
                guide.SkipCommand.Execute(null);
                Ui.Pump(0.6);
                var said = new List<string> { $"step {guide.Index + 1}: {guide.Said.Split(':')[0]}" };

                // Back reads the step before again; Skip goes forward once more.
                guide.BackCommand.Execute(null);
                Ui.Pump(0.2);
                said.Add($"back to step {guide.Index + 1} ({guide.Title}), back {(guide.CanGoBack ? "offered" : "not offered")}");
                guide.SkipCommand.Execute(null);
                Ui.Pump(0.6);
                said.Add($"forward to step {guide.Index + 1}: {guide.Said.Split(':')[0]}");

                // The row of buttons, with the long "See one in <game>" title, must wrap: every button inside the card's own
                // border (a row that overflows is clipped by the map area, still inside the window, and unreadable).
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string c && (c.StartsWith("See one in", StringComparison.Ordinal) || c is "Stop the guide" or "Skip this step")).ToList();
                foreach (var b in buttons.OrderBy(b => b.Content as string, StringComparer.Ordinal))
                {
                    var card = b.FindAncestorOfType<Border>()!;
                    var topLeft = b.TranslatePoint(new Point(0, 0), card)!.Value;
                    var inside = topLeft.X >= 0 && topLeft.Y >= 0 && topLeft.X + b.Bounds.Width <= card.Bounds.Width + 0.5 && topLeft.Y + b.Bounds.Height <= card.Bounds.Height + 0.5;
                    said.Add($"{(b.Content as string)!.Split(' ')[0]} {(inside ? "inside" : "OUTSIDE")}");
                }

                // The Door tool on nothing: the card says to click a wall.
                editor.Commands().Single(c => c.Name == "Door tool").Run();
                Ui.Pump(0.2);
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                // The plan's top right: empty grid, away from the tool column that overlays its left edge.
                var empty = canvas.TranslatePoint(new Point(canvas.Bounds.Width - 20, 20), window)!.Value;
                window.MouseMove(empty, RawInputModifiers.None);
                window.MouseDown(empty, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(empty, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                said.Add($"after a miss: {guide.Said.Split(':')[0]}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["step 3: Your rooms share no wall yet", "back to step 2 (See it in 3D), back offered", "forward to step 3: Your rooms share no wall yet", "See inside", "Skip inside", "Stop inside", "after a miss: Click on a wall"], seen);
    }
}
