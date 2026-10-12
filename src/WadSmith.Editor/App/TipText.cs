namespace WadSmith.Editor.App;

/// <summary>Tooltips at the length chosen in Preferences (D34): the full text, its first sentence, or none.</summary>
public static class TipText
{
    /// <summary>The first sentence of <paramref name="full"/>: up to the first full stop, question or exclamation mark that ends it (followed by a space and a capital, or by nothing).</summary>
    public static string Brief(string full)
    {
        for (var i = 0; i < full.Length; i++)
        {
            if (full[i] is not ('.' or '!' or '?')) continue;
            if (i == full.Length - 1) return full;
            if (full[i + 1] != ' ') continue;
            var next = full.AsSpan(i + 1).TrimStart();
            if (next.Length > 0 && (char.IsUpper(next[0]) || next[0] is '(' or '"' or '“')) return full[..(i + 1)];
        }

        return full;
    }

    /// <summary>What a tooltip shows at <paramref name="length"/>; null when it shows nothing.</summary>
    public static string? At(TooltipLength length, string full) => length switch
    {
        TooltipLength.Off => null,
        TooltipLength.Brief => Brief(full),
        _ => full,
    };
}
