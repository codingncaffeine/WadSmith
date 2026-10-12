using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ThreeD;
using WadSmith.App.ViewModels;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>
/// The shortcut the command palette shows beside a command is the key that does it, in WadSmith's keys and in Doom
/// Builder's: pressed on the plan, it leaves the editor where running the command from the palette leaves it.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ShortcutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachShortcutThePaletteShowsDoesWhatTheCommandDoes(bool doomBuilderKeys)
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var (wrong, checkedNames, controls) = await Ui.Run(() => Check(temp.Path, Path.GetDirectoryName(wad)!, doomBuilderKeys));
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        // The keys every preset must be seen to work, so an empty list is not a walk that saw nothing.
        HashSet<string> must = doomBuilderKeys
            ? ["Select corners only", "Select walls only", "Select rooms only", "Select objects only", "Room tool", "Door tool", "Show the 3D view alone"]
            : ["Select tool", "Room tool", "Door tool", "Window tool", "Teleport tool", "Light tool", "Stairs tool", "Measure tool", "Show the 3D view alone", "Show the player's size"];
        Assert.Superset(must, checkedNames);
        Assert.Equal(["a letter typed in a field ran nothing", "the 3D view's key went from Walk"], controls);
    }

    private static (List<string> Wrong, HashSet<string> Checked, List<string> Controls) Check(string profile, string games, bool doomBuilderKeys)
    {
        var (window, vm) = Ui.Start(profile, games);
        var wrong = new List<string>();
        var seen = new HashSet<string>();
        var controls = new List<string>();
        try
        {
            var editor = Ui.NewMap(vm);
            App.App.Services!.Settings.Current.DoomBuilderKeys = doomBuilderKeys;
            editor.VeteranChanged();
            var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First();
            var view3d = window.GetVisualDescendants().OfType<View3D>().First();
            object State() => (editor.Tool, editor.Picks, editor.Layout, editor.ShowFit, editor.ShowHeights, editor.ViewMode, editor.MirrorChoice, editor.CameraMode, editor.Palette is not null);
            void Press((Key Key, KeyModifiers Modifiers) chord)
            {
                var raw = RawInputModifiers.None;
                if ((chord.Modifiers & KeyModifiers.Control) != 0) raw |= RawInputModifiers.Control;
                if ((chord.Modifiers & KeyModifiers.Shift) != 0) raw |= RawInputModifiers.Shift;
                if ((chord.Modifiers & KeyModifiers.Alt) != 0) raw |= RawInputModifiers.Alt;
                window.KeyPress(chord.Key, raw, PhysicalKey.None, null);
            }

            foreach (var command in editor.Commands())
            {
                // Play would start the engine, the start screen would close the map, Preferences would cover it.
                if (command.Group is "Play" || command.Name is "Preferences" || command.Name.StartsWith("Back to", StringComparison.Ordinal) || !command.Available) continue;
                if (KeyChords.Parse(command.Shortcut) is not { } chord) continue;
                foreach (var tool in new[] { EditorTool.Select, EditorTool.Measure })
                {
                    void Reset()
                    {
                        editor.Palette = null;
                        editor.Layout = MapLayout.Split;
                        editor.Tool = tool;
                        editor.Picks = PickOnly.Anything;
                        editor.ShowFit = false;
                        Ui.Pump(0.02);
                        canvas.Focus();
                        Ui.Pump(0.02);
                    }

                    Reset();
                    var before = State();
                    Press(chord);
                    Ui.Pump(0.03);
                    var pressed = State();
                    Reset();
                    command.Run();
                    Ui.Pump(0.03);
                    var ran = State();
                    if (Equals(pressed, before) && Equals(ran, before)) continue;
                    seen.Add(command.Name);
                    if (!Equals(pressed, ran)) wrong.Add($"{command.Name} ({command.Shortcut}): the key leaves {pressed}, the command {ran}");
                }
            }

            // Controls: a tool's letter typed into a text field types, and the 3D view in Walk passes the 3D key on.
            editor.Palette = null;
            editor.Layout = MapLayout.Split;
            editor.Tool = EditorTool.Select;
            Ui.Pump(0.05);
            var field = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
            field.Focus();
            Ui.Pump(0.05);
            var letter = KeyChords.Parse(editor.KeyFor("Door tool"))!.Value;
            Press(letter);
            Ui.Pump(0.05);
            if (editor.Tool == EditorTool.Select) controls.Add("a letter typed in a field ran nothing");
            editor.CameraMode = CameraMode.Walk;
            view3d.Focus();
            Ui.Pump(0.05);
            Press(KeyChords.Parse(editor.KeyFor("Show the 3D view alone"))!.Value);
            Ui.Pump(0.05);
            if (editor.Layout == MapLayout.ThreeD) controls.Add("the 3D view's key went from Walk");
            editor.CameraMode = CameraMode.Orbit;
            editor.Palette = null;
        }
        finally
        {
            App.App.Services!.Settings.Current.DoomBuilderKeys = false;
            window.Close();
        }

        return (wrong, seen, controls);
    }
}
