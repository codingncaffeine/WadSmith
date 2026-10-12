namespace WadSmith.Editor.App;

/// <summary>
/// How well typed letters match a name, as command palettes score it: every letter must appear in order; letters that
/// start a word, and runs of letters together, count most; a name that starts with what was typed counts most of all.
/// Case and spaces typed do not matter.
/// </summary>
public static class Fuzzy
{
    /// <summary>The score of <paramref name="typed"/> against <paramref name="text"/>, higher is better; null when the letters are not all there in order.</summary>
    public static int? Score(string typed, string text)
    {
        var query = typed.Trim();
        if (query.Length == 0) return 0;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 10_000 - text.Length;
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return (WordStart(text, at) ? 8_000 : 6_000) - at - text.Length;

        var letters = query.Replace(" ", "", StringComparison.Ordinal);
        if (!Fits(letters, 0, text, 0)) return null;
        var score = 0;
        var run = 0;
        var t = 0;
        for (var q = 0; q < letters.Length; q++)
        {
            // The next letter at a word start when the rest still fits after it; otherwise the nearest one.
            var nearest = -1;
            var chosen = -1;
            for (var i = t; i < text.Length; i++)
            {
                if (!Same(text[i], letters[q])) continue;
                if (nearest < 0) nearest = i;
                if (WordStart(text, i) && Fits(letters, q + 1, text, i + 1))
                {
                    chosen = i;
                    break;
                }
            }

            chosen = chosen >= 0 ? chosen : nearest;
            run = chosen == t && q > 0 ? run + 1 : 0;
            score += (WordStart(text, chosen) ? 60 : 10) + run * 15 - Math.Min(chosen - t, 20);
            t = chosen + 1;
        }

        return score - text.Length;
    }

    // Whether letters[from..] appear in order in text[start..].
    private static bool Fits(string letters, int from, string text, int start)
    {
        var t = start;
        for (var q = from; q < letters.Length; q++)
        {
            while (t < text.Length && !Same(text[t], letters[q])) t++;
            if (t == text.Length) return false;
            t++;
        }

        return true;
    }

    private static bool Same(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private static bool WordStart(string text, int i) => i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
