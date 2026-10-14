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
            "'" => Key.OemQuotes,
            "Esc" => Key.Escape,
            var k => Enum.Parse<Key>(k),
        };
        return (key, mods);
    }

    /// <summary>A key press written as a shortcut, as <see cref="Parse"/> reads it back; null for a modifier pressed alone.</summary>
    public static string? Format(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return null;
        var name = key switch
        {
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemComma => ",",
            Key.OemQuotes => "'",
            Key.Escape => "Esc",
            _ => key.ToString(),
        };
        return ((modifiers & KeyModifiers.Control) != 0 ? "Ctrl+" : "") + ((modifiers & KeyModifiers.Shift) != 0 ? "Shift+" : "") + ((modifiers & KeyModifiers.Alt) != 0 ? "Alt+" : "") + name;
    }

    /// <summary>Whether a key press is this shortcut (exactly these modifiers).</summary>
    public static bool Matches(string shortcut, Key key, KeyModifiers modifiers) => Parse(shortcut) is { } c && c.Key == key && c.Modifiers == (modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt));
}
