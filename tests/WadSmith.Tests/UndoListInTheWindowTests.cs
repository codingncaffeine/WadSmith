using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;

namespace WadSmith.Tests;

/// <summary>
/// The undo list (D65, Ultimate Doom Builder's): the Versions panel lists this session's steps; clicking one puts the map as it
/// was just after it, back or forward, and a new change drops the steps taken back.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class UndoListInTheWindowTests
{
    [Fact]
    public async Task ClickingAStepGoesBackOrForwardToIt()
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
                foreach (var x in new[] { 512, 768, 1024 })
                {
                    var had = session.Map.Sectors.Count(s => s.OnMap);
                    session.DrawRoom([new(x, 0), new(x, 128), new(x + 128, 128), new(x + 128, 0)]);
                    Ui.Until(() => session.Map.Sectors.Count(s => s.OnMap) > had, 10);
                }

                editor.ShowVersions = true;
                Ui.Pump(0.3);
                List<Button> Rows() => [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("step") && b.IsEffectivelyVisible)];
                string List() => string.Join(" | ", Rows().Select(b => b.DataContext is StepItem s ? s.Label + (s.IsNow ? " (now)" : s.IsUndone ? " (undone)" : "") : "?"));
                int Rooms() => session.Map.Sectors.Count(s => s.OnMap);
                void Click(int row)
                {
                    var b = Rows()[row];
                    var middle = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(middle, RawInputModifiers.None);
                    window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
                    Ui.Until(() => session.Geometry.Version == session.Map.Version, 10);
                    Ui.Pump(0.2);
                }

                var said = new List<string> { $"after three rooms: {Rooms()} rooms · {List()}" };
                Click(1);
                said.Add($"the first room's step: {Rooms()} rooms · {List()}");
                Click(3);
                said.Add($"the last step, taken back: {Rooms()} rooms · {List()}");
                Click(0);
                said.Add($"as opened: {Rooms()} rooms · {List()}");
                Click(2);
                session.DrawRoom([new(512, 512), new(512, 640), new(640, 640), new(640, 512)]);
                Ui.Until(() => Rooms() == 4, 10);
                Ui.Pump(0.2);
                said.Add($"a new room after the second: {Rooms()} rooms · {List()}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "after three rooms: 4 rooms · The map as it was opened | Draw a room | Draw a room | Draw a room (now)",
            "the first room's step: 2 rooms · The map as it was opened | Draw a room (now) | Draw a room (undone) | Draw a room (undone)",
            "the last step, taken back: 4 rooms · The map as it was opened | Draw a room | Draw a room | Draw a room (now)",
            "as opened: 1 rooms · The map as it was opened (now) | Draw a room (undone) | Draw a room (undone) | Draw a room (undone)",
            "a new room after the second: 4 rooms · The map as it was opened | Draw a room | Draw a room | Draw a room (now)",
        ], seen);
    }
}
