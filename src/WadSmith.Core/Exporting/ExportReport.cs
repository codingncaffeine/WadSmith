using System.Globalization;
using System.Text;

namespace WadSmith.Core.Exporting;

/// <summary>
/// One thing an export loses or changes, for one map (null for the whole project): what, in plain words; on which kind of
/// element ("wall", "side", "room", "object", "corner", "map", "file") and which ones; and whether the port stops with an
/// error over it (vanilla meeting an object or picture it does not know) rather than only playing without it.
/// </summary>
public sealed record ExportLoss(string? Map, string Key, string Title, string On, IReadOnlyList<int> Indexes, bool Breaks, string Detail = "")
{
    public int Count => Math.Max(1, Indexes.Count);
}

/// <summary>What an export lost, map by map, and the notes worth knowing (corners rounded to whole units, nodes built).</summary>
public sealed class ExportReport
{
    private readonly List<ExportLoss> _losses = [];

    public IReadOnlyList<ExportLoss> Losses => _losses;

    public List<string> Notes { get; } = [];

    /// <summary>Whether something in the export stops the port with an error.</summary>
    public bool Breaks => _losses.Any(l => l.Breaks);

    public void Add(ExportLoss loss) => _losses.Add(loss);

    /// <summary>Adds one loss per key from (key, index) pairs, keeping the first index order, with the words <paramref name="words"/> gives each key.</summary>
    internal void AddGrouped(string? map, string on, IEnumerable<(string Key, int Index)> found, Func<string, (string Title, bool Breaks, string Detail)> words)
    {
        foreach (var g in found.GroupBy(f => f.Key, StringComparer.Ordinal))
        {
            var (title, breaks, detail) = words(g.Key);
            _losses.Add(new ExportLoss(map, $"{on}:{g.Key}", title, on, [.. g.Select(f => f.Index).Distinct()], breaks, detail));
        }
    }

    /// <summary>The report as text, for the command line and the log.</summary>
    public string ToText(bool verbose = false)
    {
        var sb = new StringBuilder();
        if (_losses.Count == 0) sb.AppendLine("Nothing is lost.");
        foreach (var map in _losses.GroupBy(l => l.Map))
        {
            sb.AppendLine(map.Key is null ? "The project:" : $"{map.Key}:");
            foreach (var l in map)
            {
                sb.Append("  ").Append(l.Breaks ? "STOPS THE GAME: " : "").Append(l.Title);
                if (l.Indexes.Count > 0) sb.Append(CultureInfo.InvariantCulture, $" ({l.Indexes.Count} {l.On}{(l.Indexes.Count == 1 ? "" : "s")})");
                if (verbose && l.Indexes.Count > 0) sb.Append(" [").Append(string.Join(' ', l.Indexes.Take(12))).Append(l.Indexes.Count > 12 ? " …]" : "]");
                if (l.Detail.Length > 0) sb.Append(" — ").Append(l.Detail);
                sb.AppendLine();
            }
        }

        foreach (var n in Notes) sb.AppendLine("Note: " + n);
        return sb.ToString();
    }
}
