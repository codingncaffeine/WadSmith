using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;

namespace WadSmith.Tests;

/// <summary>Through the real window: Preferences show the engine Play starts, and another is chosen there (D64).</summary>
[Collection(TimingCollection.Name)]
public sealed class EngineInTheWindowTests
{
    private static void Click(Window window, Control c)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at, RawInputModifiers.None);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.3);
    }

    private static Button Shown(Window window, string content) => window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == content);

    [Fact]
    public async Task PreferencesShowTheEngineChosenAndGoBackToTheOneFoundFirst()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var mine = Path.Combine(temp.Path, "engines", "uzdoom-mine");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "");
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            var settings = App.App.Services!.Settings.Current;
            var said = new List<string>();
            try
            {
                settings.EnginePath = mine;
                Click(window, Shown(window, "Preferences"));
                var prefs = vm.Preferences!;
                Ui.Until(() => prefs.EnginesFound.IsCompleted);
                Ui.Pump(0.3);
                var box = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(c) == "Engine");
                box.BringIntoView();
                Ui.Pump(0.5);
                if (Environment.GetEnvironmentVariable("WADSMITH_ENGINE_SHOT") is { Length: > 0 } shots)
                {
                    using var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
                    frame.Save(Path.Combine(shots, "preferences-engine.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                var chosen = box.SelectedItem as EngineRow;
                said.Add($"chosen: {chosen?.Name}, {(chosen?.Where == mine ? "the file chosen" : chosen?.Where)}");
                said.Add($"box says: {box.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible)?.Text}");
                said.Add($"first row: {(box.Items[0] as EngineRow)?.Name}");

                box.SelectedIndex = 0;
                Ui.Until(() => prefs.EnginesFound.IsCompleted);
                Ui.Pump(0.3);
                said.Add($"kept after the first row: {settings.EnginePath ?? "nothing"}");
                Click(window, Shown(window, "Done"));
                said.Add($"closed: {vm.Preferences is null}");
            }
            finally
            {
                settings.EnginePath = null;
                window.Close();
            }

            return said;
        });
        Assert.Equal(["chosen: UZDoom, the file chosen", "box says: UZDoom", "first row: The one found first", "kept after the first row: nothing", "closed: True"], seen);
    }
}
