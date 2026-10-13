using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace WadSmith.Tests;

/// <summary>Find and replace in the real editor (D58): Ctrl+F opens the panel, typing searches, Replace all is one undo step.</summary>
[Collection(TimingCollection.Name)]
public sealed class FindInTheWindowTests
{
    [Fact]
    public async Task CtrlFFindsTheFloorPictureAndReplaceAllChangesItInOneStep()
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
                var floor = room.FloorTexture;
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                canvas.Focus();
                Ui.Pump(0.1);
                window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
                Ui.Pump(0.2);
                var said = new List<string> { $"Ctrl+F: {(editor.ShowFind ? "the Find panel" : "nothing")}" };

                // The kind and the text, set through the panel's own controls.
                var panel = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemsSource == editor.Find.Kinds);
                panel.SelectedIndex = 1;
                var what = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.DataContext == editor.Find && t.Text == "");
                what.Text = floor;
                for (var i = 0; i < 50 && !editor.Find.AnyFound; i++) Ui.Pump(0.02);
                said.Add($"{floor}: {editor.Find.Summary} {string.Join("; ", editor.Find.Results.Select(r => $"{r.Label}, {r.Where}"))}");

                editor.Find.With = "NUKAGE1";
                var replace = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "Replace all");
                replace.Command!.Execute(null);
                Ui.Pump(0.2);
                said.Add($"Replace all: the floor is {room.FloorTexture}, undo says {session.History.UndoLabel}");
                for (var i = 0; i < 50 && editor.Find.AnyFound; i++) Ui.Pump(0.02);
                said.Add($"then: {editor.Find.Summary}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(4, seen.Count);
        Assert.Equal("Ctrl+F: the Find panel", seen[0]);
        Assert.Matches(": 1 found\\. Room, (floor|floor and ceiling)$", seen[1]);
        Assert.Matches("^Replace all: the floor is NUKAGE1, undo says Replace .+ with NUKAGE1$", seen[2]);
        Assert.Equal("then: Nothing found.", seen[3]);
    }
}
