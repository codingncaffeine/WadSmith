using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>
/// Every control a person can press, type into or pick from carries a tooltip (§3.9): the real window is walked on
/// every page and panel, with each kind of thing selected, and any visible control without one fails the test by name.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class TooltipCoverageTests
{
    [Fact]
    public async Task EveryControlOnEveryPageHasATooltip()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var missing = await Ui.Run(() => Walk(temp.Path, Path.GetDirectoryName(wad)!));
        Assert.True(missing.Count == 0, "Controls without a tooltip:\n" + string.Join("\n", missing));
    }

    private static List<string> Walk(string profile, string games)
    {
        var (window, vm) = Ui.Start(profile, games);
        var missing = new List<string>();
        try
        {
            missing.AddRange(Untipped(window, "start screen"));

            var editor = Ui.NewMap(vm);
            missing.AddRange(Untipped(window, "editor"));
            foreach (var layout in new[] { MapLayout.Plan, MapLayout.ThreeD, MapLayout.Split })
            {
                editor.Layout = layout;
                Ui.Pump(0.3);
                missing.AddRange(Untipped(window, $"editor, {layout}"));
            }

            var map = editor.Session.Map;
            foreach (var (what, element) in new (string, WadSmith.Editor.Model.Element?)[]
                     {
                         ("a room", map.Sectors.FirstOrDefault()), ("a wall", map.Lines.FirstOrDefault()), ("a wall face", map.Lines.FirstOrDefault()?.Front),
                         ("a corner", map.Vertices.FirstOrDefault()), ("an object", map.Things.FirstOrDefault()),
                     })
            {
                if (element is null) continue;
                editor.Session.Selection.Set(element);
                Ui.Pump(0.4);
                missing.AddRange(Untipped(window, $"editor, {what} selected"));
            }

            editor.Session.Selection.Set();
            editor.ShowTextures = true;
            Ui.Pump(0.5);
            missing.AddRange(Untipped(window, "editor, Textures"));
            editor.ShowObjects = true;
            Ui.Pump(0.5);
            missing.AddRange(Untipped(window, "editor, Objects"));
            editor.ShowVersions = true;
            Ui.Pump(0.5);
            missing.AddRange(Untipped(window, "editor, Versions"));
            editor.ShowInspector = true;
            editor.ShowProblems = true;
            Ui.Pump(0.4);
            missing.AddRange(Untipped(window, "editor, Problems"));

            editor.OpenPaletteCommand.Execute(null);
            Ui.Pump(0.4);
            missing.AddRange(Untipped(window, "command palette"));
            editor.OpenPaletteCommand.Execute(null);
            Ui.Pump(0.2);

            vm.OpenPreferencesCommand.Execute(null);
            Ui.Pump(0.4);
            missing.AddRange(Untipped(window, "Preferences"));
            vm.Preferences!.CloseCommand.Execute(null);
            Ui.Pump(0.2);

            // The control: a bare button put on the page is found, so an empty list means the walk saw every control.
            var planted = new Button { Content = "Planted" };
            window.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "PlanPane").Children.Add(planted);
            Ui.Pump(0.3);
            var caught = Untipped(window, "planted");
            Assert.Equal(["planted: Button 'Planted'"], caught);
        }
        finally
        {
            window.Close();
        }

        return missing;
    }

    /// <summary>
    /// The visible controls a person uses that have no tooltip. A tooltip counts when it is on the control or on an
    /// element around it that holds no other such control, as an Inspector row holds its one field. A control's own
    /// insides (a box's arrow, a scroll bar) and menu entries, which say what they do, are not counted.
    /// </summary>
    private static List<string> Untipped(Window window, string page)
    {
        var found = new List<string>();
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(Uses))
        {
            if (!control.IsEffectivelyVisible || control.TemplatedParent is not null || control.FindAncestorOfType<MenuItem>() is not null) continue;
            if (Tipped(control)) continue;
            found.Add($"{page}: {control.GetType().Name} '{Label(control)}'");
        }

        return found.Distinct().ToList();
    }

    private static bool Uses(Control c) => c is Button or SplitButton or ComboBox or TextBox or Slider or NumericUpDown or ListBox;

    private static bool Tipped(Control control)
    {
        for (Visual? v = control; v is Control c; v = c.GetVisualParent())
        {
            if (ToolTip.GetTip(c) is { } tip && tip is not string { Length: 0 }) return true;
            if (!ReferenceEquals(c, control) && c.GetVisualDescendants().OfType<Control>().Count(d => Uses(d) && d.IsEffectivelyVisible && d.TemplatedParent is null) > 1) return false;
        }

        return false;
    }

    private static string Label(Control c) => c switch
    {
        ContentControl { Content: string s } => s,
        HeaderedContentControl { Header: string h } => h,
        TextBox t => t.PlaceholderText ?? t.Name ?? t.Text ?? "",
        _ => c.Name ?? (c.DataContext?.GetType().Name ?? ""),
    };

}
