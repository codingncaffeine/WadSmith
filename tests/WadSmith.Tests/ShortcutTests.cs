using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>
/// The shortcut the command palette shows beside a command is the key that does it: pressed on the plan, it leaves the
/// editor where running the command from the palette leaves it.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ShortcutTests
{
    [Fact]
    public async Task EachShortcutThePaletteShowsDoesWhatTheCommandDoes()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var (wrong, checkedNames) = await Ui.Run(() => Check(temp.Path, Path.GetDirectoryName(wad)!));
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        // Every tool's letter, Tab and G must have been seen to change something, so an empty list is not a walk that saw nothing.
        Assert.Superset(new HashSet<string> { "Select tool", "Room tool", "Door tool", "Window tool", "Teleport tool", "Light tool", "Stairs tool", "Measure tool", "Show the 3D view alone", "Show the player's size" }, checkedNames);
    }

    private static (List<string> Wrong, HashSet<string> Checked) Check(string profile, string games)
    {
        var (window, vm) = Ui.Start(profile, games);
        var wrong = new List<string>();
        var seen = new HashSet<string>();
        try
        {
            var editor = Ui.NewMap(vm);
            var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First();
            object State() => (editor.Tool, editor.Layout, editor.ShowFit, editor.ShowHeights, editor.ViewMode, editor.MirrorChoice, editor.CameraMode, editor.Palette is not null);
            foreach (var command in editor.Commands())
            {
                // Play would start the engine; the start screen would close the map.
                if (command.Group is "Play" || command.Name.StartsWith("Back to", StringComparison.Ordinal) || !command.Available) continue;
                if (Gesture(command.Shortcut) is not { } gesture) continue;
                foreach (var tool in new[] { EditorTool.Select, EditorTool.Measure })
                {
                    void Reset()
                    {
                        editor.Palette = null;
                        editor.Layout = MapLayout.Split;
                        editor.Tool = tool;
                        editor.ShowFit = false;
                        Ui.Pump(0.02);
                        canvas.Focus();
                        Ui.Pump(0.02);
                    }

                    Reset();
                    var before = State();
                    window.KeyPress(gesture.Key, gesture.Modifiers, PhysicalKey.None, null);
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

            editor.Palette = null;
        }
        finally
        {
            window.Close();
        }

        return (wrong, seen);
    }

    // The palette's way of writing a shortcut, as a key and its modifiers; null for what is not one key ("G in 3D").
    private static (Key Key, RawInputModifiers Modifiers)? Gesture(string text)
    {
        if (text.Length == 0 || text.Contains(' ', StringComparison.Ordinal)) return null;
        var parts = text.Split('+');
        var mods = RawInputModifiers.None;
        foreach (var m in parts[..^1])
        {
            mods |= m switch
            {
                "Ctrl" => RawInputModifiers.Control,
                "Shift" => RawInputModifiers.Shift,
                "Alt" => RawInputModifiers.Alt,
                _ => throw new InvalidOperationException($"Unknown modifier in {text}"),
            };
        }

        var key = parts[^1] switch
        {
            "[" => Key.OemOpenBrackets,
            "]" => Key.OemCloseBrackets,
            "," => Key.OemComma,
            "Esc" => Key.Escape,
            var k when k.Length == 1 && char.IsLetter(k[0]) => Enum.Parse<Key>(k),
            var k => Enum.Parse<Key>(k),
        };
        return (key, mods);
    }
}
