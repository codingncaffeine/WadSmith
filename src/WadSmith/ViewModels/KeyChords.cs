using Avalonia.Input;

namespace WadSmith.App.ViewModels;

/// <summary>The way the palette writes a shortcut ("Ctrl+Shift+Z", "F9", "Ctrl+[", "Esc"), read back as a key and its modifiers.</summary>
internal static class KeyChords
{
    /// <summary>The key and modifiers a shortcut stands for; null for what is not one key ("G in 3D") or for none.</summary>
    public static (Key Key, KeyModifiers Modifiers)? Parse(string text)
    {
        if (text.Length == 0 || text.Contains(' ', StringComparison.Ordinal)) return null;
        var parts = text.Split('+');
        var mods = KeyModifiers.None;
        foreach (var m in parts[..^1])
        {
            mods |= m switch
            {
                "Ctrl" => KeyModifiers.Control,
                "Shift" => KeyModifiers.Shift,
                "Alt" => KeyModifiers.Alt,
                _ => throw new FormatException($"Unknown modifier in the shortcut {text}."),
            };
        }

        Key key = parts[^1] switch
        {
            "[" => Key.OemOpenBrackets,
            "]" => Key.OemCloseBrackets,
            "," => Key.OemComma,
            "Esc" => Key.Escape,
            var k => Enum.Parse<Key>(k),
        };
        return (key, mods);
    }

    /// <summary>Whether a key press is this shortcut (exactly these modifiers).</summary>
    public static bool Matches(string shortcut, Key key, KeyModifiers modifiers) => Parse(shortcut) is { } c && c.Key == key && c.Modifiers == (modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt));
}
