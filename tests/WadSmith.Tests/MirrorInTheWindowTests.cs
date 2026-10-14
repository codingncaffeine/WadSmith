using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;

namespace WadSmith.Tests;

/// <summary>A mirror (Phase 7), as the user goes: a solid wall selected, its Inspector's "A mirror" ticked; the saved map has GZDoom's Line_Mirror on it; undo takes it off.</summary>
[Collection(TimingCollection.Name)]
public sealed class MirrorInTheWindowTests
{
    [Fact]
    public async Task ATickedWallBecomesAMirrorInTheSavedMap()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                var wall = session.Map.Lines.First(l => l.OnMap && l.Back is null);
                session.Selection.Set(wall);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                var row = editor.Inspector.Rows.OfType<FlagRow>().Single(r => r.Label == "A mirror");
                var box = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, row));
                box.BringIntoView();
                Ui.Pump(0.2);
                var middle = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;
                window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                var special = wall.Special;
                Ui.Wait(session.FlushAsync());
                var saved = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(session.Project.Folder, session.Project.Maps().First().File)));
                session.History.Undo();
                Ui.Pump(0.3);
                return $"special {special}; saved has special = 182: {saved.Contains("special = 182;", StringComparison.Ordinal)}; undone {wall.Special}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("special 182; saved has special = 182: True; undone 0", said);
    }
}
