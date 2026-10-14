namespace WadSmith.Core.Library;

/// <summary>What a free source's entry is to the editor: an object to place (a monster, a lamp) or a pack of textures.</summary>
public enum LibraryKind
{
    Object,
    TexturePack,
}

/// <summary>
/// One entry of a free source (D61): an object from Realm667 or a texture pack from idgames, with what its own page says
/// about it — the picture, who made it, its licence words — and where its file is. Source is "realm667" or "idgames"; Id is
/// safe in a file name ("r667-748", "idgames-19427"); Group is the shelf group it shows under (the Objects shelf's words, or
/// "Texture packs"); Style is the game it was made for in words ("Doom", "Heretic/Hexen", "Strife", "Other"); Family is the
/// engine family whose colours its pictures are in; Size is in bytes when the source says it; Licence is what the author
/// allows, in their own words, when the source says it.
/// </summary>
public sealed record LibraryItem(
    string Source,
    string Id,
    string Title,
    LibraryKind Kind,
    string Group,
    string Style,
    string Family,
    Uri? Preview,
    Uri Download,
    Uri Page,
    string Credits,
    string Description,
    string Info,
    long? Size = null,
    string? Licence = null)
{
    /// <summary>The class names its page offers to place ("Summon:" on Realm667), first the main one; empty when the page names none.</summary>
    public IReadOnlyList<string> Summons => [.. Field("Summon").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_')).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The value of a "Name: value" line of the entry's information, or "".</summary>
    public string Field(string name)
    {
        foreach (var line in Info.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return line[(colon + 1)..].Trim();
        }

        return "";
    }

    /// <summary>The size in words ("90 KB", "22 MB"), or "" when unknown.</summary>
    public string SizeWords => Size switch
    {
        null => "",
        < 1024 => $"{Size} bytes",
        < 1024 * 1024 => $"{Math.Max(1, Size.Value / 1024)} KB",
        _ => (Size.Value / 1048576.0).ToString(Size.Value < 10 * 1048576L ? "0.0" : "0", System.Globalization.CultureInfo.CurrentCulture) + " MB",
    };
}
