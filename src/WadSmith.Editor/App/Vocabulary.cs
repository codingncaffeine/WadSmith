using System.Text.Json;

namespace WadSmith.Editor.App;

/// <summary>
/// The two names of the things on screen (D3): WadSmith's friendly label and the classic name of 25 years of Doom
/// tutorials (sector, linedef, thing), from <c>data/ui/vocabulary.json</c>. With <see cref="Classic"/> on a label shows
/// its classic name; either way the tooltip names the other one, so neither audience is stranded.
/// </summary>
public sealed class Vocabulary
{
    private readonly Dictionary<string, string> _classic;
    private readonly Dictionary<string, string> _friendly;

    private Vocabulary(Dictionary<string, string> classic)
    {
        _classic = classic;
        _friendly = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, c) in classic) _friendly.TryAdd(c, f);
    }

    /// <summary>The table from the built-in data file.</summary>
    public static Vocabulary Load()
    {
        using var doc = JsonDocument.Parse(Core.DataFiles.ReadText("ui/vocabulary.json"));
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.RootElement.GetProperty("labels").EnumerateArray()) table[e.GetProperty("friendly").GetString()!] = e.GetProperty("classic").GetString()!;
        return new(table);
    }

    /// <summary>Whether labels show the classic names (Preferences, "Show classic names").</summary>
    public bool Classic { get; set; }

    /// <summary>The label to show for a friendly label: itself, or its classic name when those are shown.</summary>
    public string Label(string friendly) => Classic && _classic.TryGetValue(friendly, out var classic) ? Cased(friendly, classic) : friendly;

    /// <summary>The name a label does not show (the classic one, or with classic names on the friendly one); null when both are the same.</summary>
    public string? Other(string friendly)
    {
        if (!_classic.TryGetValue(friendly, out var classic) || string.Equals(classic, friendly, StringComparison.OrdinalIgnoreCase)) return null;
        return Cased(friendly, Classic ? friendly : classic);
    }

    /// <summary>A tooltip with the other name added: "Classic name: Light level." or "WadSmith calls it Brightness.".</summary>
    public string Tip(string friendly, string tooltip)
    {
        if (Other(friendly) is not { } other) return tooltip;
        var plain = IsCapitals(other) ? other[..1] + other[1..].ToLowerInvariant() : other;
        var note = Classic ? $"WadSmith calls it “{plain}”." : $"Classic name: {plain}.";
        return tooltip.Length == 0 ? note : $"{tooltip} {note}";
    }

    /// <summary>Whether the table knows this friendly label (for tests and the coverage check).</summary>
    public bool Knows(string friendly) => _classic.ContainsKey(friendly);

    /// <summary>The friendly label a classic name stands for, or null.</summary>
    public string? FriendlyFor(string classic) => _friendly.GetValueOrDefault(classic);

    private static string Cased(string like, string text) => IsCapitals(like) ? text.ToUpperInvariant() : text;

    private static bool IsCapitals(string s) => s.Any(char.IsLetter) && s.Where(char.IsLetter).All(char.IsUpper);
}
