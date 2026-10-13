namespace WadSmith.Core.Audio;

/// <summary>
/// The engine's sound table (SNDINFO): logical sound names such as <c>imp/sight</c> to the lumps that hold them. Reads plain
/// <c>name lump</c> lines, <c>$random</c> groups (the first member stands for the group) and <c>$alias</c>; other commands
/// change how a sound plays, not which lump it is, and are skipped. Later files replace earlier ones, as in the engine.
/// </summary>
public sealed class SoundInfo
{
    private readonly Dictionary<string, string> _lumps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _randoms = new(StringComparer.OrdinalIgnoreCase);

    public static SoundInfo Empty { get; } = new();

    /// <summary>The table read from <paramref name="texts"/>, in load order.</summary>
    public static SoundInfo Parse(IEnumerable<string> texts)
    {
        var info = new SoundInfo();
        foreach (var text in texts) info.Read(text);
        return info;
    }

    /// <summary>The lump that plays for <paramref name="name"/>, following aliases and random groups; null when the table has none.</summary>
    public string? LumpFor(string name)
    {
        for (var hops = 0; hops < 8; hops++)
        {
            if (_lumps.TryGetValue(name, out var lump)) return lump;
            if (!_aliases.TryGetValue(name, out var next)) return null;
            name = next;
        }

        return null;
    }

    /// <summary>
    /// What <paramref name="name"/> needs to play from another table: the SNDINFO lines that define it (its own line, its
    /// <c>$alias</c> or <c>$random</c> and the lines of what they name) and the lumps those lines name. Empty when the table has none.
    /// </summary>
    public (IReadOnlyList<string> Lines, IReadOnlyList<string> Lumps) Needs(string name)
    {
        var lines = new List<string>();
        var lumps = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string n, int depth)
        {
            if (depth > 8 || !seen.Add(n)) return;
            if (_randoms.TryGetValue(n, out var members))
            {
                lines.Add($"$random {n} {{ {string.Join(' ', members)} }}");
                foreach (var m in members) Walk(m, depth + 1);
            }
            else if (_aliases.TryGetValue(n, out var to))
            {
                lines.Add($"$alias {n} {to}");
                Walk(to, depth + 1);
            }
            else if (_lumps.TryGetValue(n, out var lump))
            {
                lines.Add($"{n} {lump}");
                lumps.Add(lump);
            }
        }

        Walk(name, 0);
        return (lines, lumps);
    }

    private void Read(string text)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var inComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = Uncommented(lines[i], ref inComment);
            var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            if (words[0].Equals("$random", StringComparison.OrdinalIgnoreCase) && words.Length >= 2)
            {
                // "$random imp/sight { imp/sight1 imp/sight2 }", the list possibly over several lines.
                var members = new List<string>();
                var rest = string.Join(' ', words.Skip(2));
                while (!rest.Contains('}', StringComparison.Ordinal) && i + 1 < lines.Length) rest += " " + Uncommented(lines[++i], ref inComment);
                members.AddRange(rest.Split((char[])[' ', '\t', '{', '}'], StringSplitOptions.RemoveEmptyEntries));
                if (members.Count > 0) Alias(words[1], members[0]);
                _randoms[words[1]] = members;
                continue;
            }

            if (words[0].Equals("$alias", StringComparison.OrdinalIgnoreCase) && words.Length >= 3)
            {
                Alias(words[1], words[2]);
                continue;
            }

            if (words[0].StartsWith('$') || words.Length < 2) continue;
            _aliases.Remove(words[0]);
            _lumps[words[0]] = words[1];
        }
    }

    private void Alias(string name, string to)
    {
        _lumps.Remove(name);
        _aliases[name] = to;
    }

    private static string Uncommented(string line, ref bool inComment)
    {
        var kept = new System.Text.StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (inComment)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inComment = false;
                    i++;
                }

                continue;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                inComment = true;
                i++;
                continue;
            }

            kept.Append(line[i]);
        }

        return kept.ToString();
    }
}
