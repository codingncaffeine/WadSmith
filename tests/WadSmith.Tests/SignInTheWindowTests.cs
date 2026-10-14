using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>A sign written on a wall (Phase 7), as the user goes: the wall selected, "Write a sign here…", its words changed in the Inspector, undo.</summary>
[Collection(TimingCollection.Name)]
public sealed class SignInTheWindowTests
{
    private static void Click(MainWindow window, Visual target)
    {
        if (target is Control control) control.BringIntoView();
        Ui.Pump(0.2);
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");
        window.MouseMove(middle, RawInputModifiers.None);
        window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    [Fact]
    public async Task ASignIsWrittenOnTheWallAndItsWordsChangeInTheInspector()
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
                var was = wall.Front.Middle;
                session.Selection.Set(wall);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                var write = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Write a sign here…" });
                Click(window, write);
                for (var i = 0; i < 200 && !session.Map.Sides.Any(s => s.OnMap && s.Middle == "SIGN0001"); i++) Ui.Pump(0.05);
                var face = session.Map.Sides.First(s => s.OnMap && s.Middle == "SIGN0001");
                var lines = new List<string> { $"wall was {was}; a face shows {face.Middle}; status '{editor.Status}'" };

                session.Selection.Set(face);
                Ui.Pump(0.5);
                var row = editor.Inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Words");
                var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && ReferenceEquals(t.DataContext, row));
                lines.Add($"words '{box.Text}'");
                box.Focus();
                box.Text = "Vault ahead";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                for (var i = 0; i < 200 && face.Middle == "SIGN0001"; i++) Ui.Pump(0.05);
                lines.Add($"face shows {face.Middle} saying '{SignPictures.Of(session.Project.CopiedNow(), face.Middle)?.Settings.Text}'");
                session.History.Undo();
                Ui.Pump(0.3);
                lines.Add($"undone: {face.Middle}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("wall was STARTAN3; a face shows SIGN0001; status 'The sign is on the wall: its Inspector rows change its words, letters, colour and background.'\n"
            + "words 'Your words here'\nface shows SIGN0002 saying 'Vault ahead'\nundone: SIGN0001", said);
    }
}
