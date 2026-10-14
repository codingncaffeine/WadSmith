using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;

namespace WadSmith.Tests;

/// <summary>
/// A project's maps as the user changes them (6c): the map's slot button opens the list, whose New map, Copy this map, Move and
/// Delete this map are clicked where the list shows them; the editor then shows the map the change leads to, and the list,
/// closed by each choice as a menu is, opens again at the next click.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class MapSlotsInTheWindowTests
{
    private static void ClickIn(TopLevel root, Visual target)
    {
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? throw new InvalidOperationException("Not on screen.");
        root.MouseMove(middle, RawInputModifiers.None);
        root.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        root.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    /// <summary>Opens the maps list from the slot button, as the user does, and gives the list's control named <paramref name="name"/>.</summary>
    private static Control Item(MainWindow window, string name)
    {
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Maps");
        ClickIn(window, button);
        var list = (Control)((Flyout)FlyoutBase.GetAttachedFlyout(button)!).Content!;
        Assert.True(Ui.Until(() => list.IsEffectivelyVisible && TopLevel.GetTopLevel(list) is not null, 10), "The maps list did not open.");
        return list.GetVisualDescendants().OfType<Control>().First(c => AutomationProperties.GetName(c) == name);
    }

    private static void Press(MainWindow window, string name)
    {
        var item = Item(window, name);
        ClickIn(TopLevel.GetTopLevel(item)!, item);
    }

    [Fact]
    public async Task AMapIsAddedCopiedMovedAndDeletedFromTheMapsList()
    {
        var wad = GameFiles.Require("freedoom1.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var project = Ui.NewMap(vm).Session.Project;
                EditorViewModel Now() => (EditorViewModel)vm.Page!;
                string Seen() => $"on {Now().MapChooserLabel}, maps {string.Join(",", project.Maps().Select(m => m.Slot))}";
                var lines = new List<string> { $"start: {Seen()}" };

                Press(window, "New map");
                Ui.Until(() => Now().Session.Slot == "E1M2");
                lines.Add($"new: {Seen()}; said '{Now().Status}'");

                Press(window, "Copy this map");
                Ui.Until(() => Now().Session.Slot == "E1M3");
                lines.Add($"copy: {Seen()}");

                ((ComboBox)Item(window, "Slot to move to")).SelectedItem = "E1M5";
                Ui.Pump(0.2);
                var move = Item(window, "Move");
                ClickIn(TopLevel.GetTopLevel(move)!, move);
                Ui.Until(() => Now().Session.Slot == "E1M5");
                lines.Add($"move: {Seen()}; E1M3 file {File.Exists(Path.Combine(project.Folder, "maps", "E1M3.wad"))}");

                Press(window, "Delete this map");
                Ui.Until(() => Now().Session.Slot != "E1M5");
                lines.Add($"delete: {Seen()}; kept {Directory.GetFiles(Path.Combine(project.Folder, ".wadsmith", "deleted")).Length}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(
            "start: on E1M1 ▾, maps E1M1\n"
            + "new: on E1M2 ▾, maps E1M1,E1M2; said 'E1M2 is a new map of this project, one room to start from; the E1M2 ▾ button lists the project's maps.'\n"
            + "copy: on E1M3 ▾, maps E1M1,E1M2,E1M3\n"
            + "move: on E1M5 ▾, maps E1M1,E1M2,E1M5; E1M3 file False\n"
            + "delete: on E1M1 ▾, maps E1M1,E1M2; kept 2",
            said);
    }
}
