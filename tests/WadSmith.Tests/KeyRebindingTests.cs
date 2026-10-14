using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ViewModels;

namespace WadSmith.Tests;

/// <summary>Keys of one's own (6c): a key press written as the palette writes shortcuts, read back the same.</summary>
public sealed class KeyChordTests
{
    [Theory]
    [InlineData(Key.M, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+M")]
    [InlineData(Key.F9, KeyModifiers.None, "F9")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control, "Ctrl+[")]
    [InlineData(Key.D, KeyModifiers.Alt, "Alt+D")]
    [InlineData(Key.D3, KeyModifiers.Shift, "Shift+D3")]
    public void AKeyPressIsWrittenAsAShortcutAndReadBack(Key key, KeyModifiers modifiers, string shortcut)
    {
        Assert.Equal(shortcut, KeyChords.Format(key, modifiers));
        Assert.True(KeyChords.Matches(shortcut, key, modifiers));
        Assert.Null(KeyChords.Format(Key.LeftShift, KeyModifiers.Shift));
    }
}

/// <summary>
/// A key of one's own, as the user gives it: Ctrl+K, a few letters, Alt+Enter, then the key. The plan answers to the new key and
/// no longer to the old one; a key another command had moves over, and that command is told of; Preferences puts the preset back.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class KeyRebindingInTheWindowTests
{
    [Fact]
    public async Task ACommandTakesTheKeyPressedForItInThePalette()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                void Press(Key key, RawInputModifiers modifiers)
                {
                    window.KeyPress(key, modifiers, PhysicalKey.None, null);
                    Ui.Pump(0.2);
                }

                void Palette(string letters)
                {
                    canvas.Focus();
                    Ui.Pump(0.2);
                    Press(Key.K, RawInputModifiers.Control);
                    Ui.Until(() => editor.Palette is not null);
                    Ui.Pump(0.3);
                    window.KeyTextInput(letters);
                    Ui.Pump(0.3);
                }

                var lines = new List<string>();
                Palette("measure");
                lines.Add($"found {editor.Palette!.Results[0].Name} ({editor.Palette.Results[0].Shortcut})");
                Press(Key.Enter, RawInputModifiers.Alt);
                Press(Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);
                lines.Add($"said '{editor.Palette!.Said}'; search still '{editor.Palette.Query}'; shown {editor.Palette.Results[0].Shortcut}");
                Press(Key.Escape, RawInputModifiers.None);
                Ui.Until(() => editor.Palette is null);

                canvas.Focus();
                Ui.Pump(0.2);
                var before = editor.Tool;
                Press(Key.M, RawInputModifiers.None);
                lines.Add($"M leaves the tool {(editor.Tool == before ? "as it was" : editor.Tool.ToString())}");
                Press(Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);
                lines.Add($"Ctrl+Shift+M: {editor.Tool}");

                Palette("measure");
                Press(Key.Enter, RawInputModifiers.Alt);
                Press(Key.D, RawInputModifiers.None);
                lines.Add($"said '{editor.Palette!.Said}'");
                Press(Key.Escape, RawInputModifiers.None);
                Ui.Until(() => editor.Palette is null);

                vm.OpenPreferencesCommand.Execute(null);
                Ui.Until(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "Keys back to the preset")));
                var preset = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "Keys back to the preset"));
                preset.BringIntoView();
                Ui.Pump(0.2);
                var at = preset.TranslatePoint(new Point(preset.Bounds.Width / 2, preset.Bounds.Height / 2), window)!.Value;
                window.MouseMove(at, RawInputModifiers.None);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                lines.Add($"preset: Measure {editor.KeyFor("Measure tool")}, Door {editor.KeyFor("Door tool")}, kept {App.App.Services.Settings.Current.Keys.Count}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(
            "found Measure tool (M)\n"
            + "said 'Measure tool is Ctrl+Shift+M now.'; search still 'measure'; shown Ctrl+Shift+M\n"
            + "M leaves the tool as it was\n"
            + "Ctrl+Shift+M: Measure\n"
            + "said 'Measure tool is D now, and Door tool has no key.'\n"
            + "preset: Measure M, Door D, kept 0",
            said);
    }
}
