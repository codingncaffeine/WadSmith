using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;
using WadSmith.Core.Text;

namespace WadSmith.Core.Games;

/// <summary>
/// The behaviour switches GZDoom turns on for an original map, which a remix must ask for in its MAPINFO (D40): an
/// edited map is in UDMF with a new checksum, so the engine no longer knows it. Measured in GZDoom 4.14 with a probe
/// reading <c>level.compatflags</c> (2026-10-12): every Doom II map played from the IWAD runs with <c>shorttex</c> and
/// <c>light</c> (the engine forces them for Doom-format maps of an IWAD whose <c>iwadinfo.txt</c> entry says "Shorttex";
/// "Stairs" adds <c>stairs</c>, for TNT), MAP14 also with <c>pointonline</c> from <c>compatibility.txt</c>; the same maps
/// remixed run with none.
/// </summary>
public static partial class RemixCompat
{
    /// <summary>
    /// The engine's switch names this knows as MAPINFO keys (<c>compat_NAME = 1</c>), each checked in GZDoom by the flag it sets.
    /// Others in <c>compatibility.txt</c> concern maps WadSmith does not remix (Hexen's polyobjects) or are not behaviour
    /// (<c>rebuildnodes</c>: GZDoom builds nodes for every map WadSmith writes).
    /// </summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shorttex", "light", "stairs", "pointonline" };

    /// <summary>
    /// The switches for a remix of the map with <paramref name="checksum"/> from <paramref name="baseGame"/> (the IWAD), as
    /// MAPINFO key names without "compat_", and those the engine turns on that a remix cannot ask for. Reads the engine's
    /// files: call it on a worker.
    /// </summary>
    public static (IReadOnlyList<string> Carried, IReadOnlyList<string> Lost) For(ResourceContainer baseGame, string checksum, string engineDataPath)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var lost = new List<string>();
        var support = Path.Combine(Path.GetDirectoryName(engineDataPath)!, "game_support.pk3");
        if (File.Exists(support) && ResourceContainer.Open(support).FindPath("iwadinfo.txt") is { } info)
        {
            var compatibility = Identify(Encoding.Latin1.GetString(info.Read()), name => baseGame.Find(name) is not null);
            if (compatibility.Contains("shorttex", StringComparer.OrdinalIgnoreCase)) names.UnionWith(["shorttex", "light"]);
            if (compatibility.Contains("stairs", StringComparer.OrdinalIgnoreCase)) names.Add("stairs");
        }

        if (ResourceContainer.Open(engineDataPath).FindPath("compatibility.txt") is { } table)
        {
            foreach (var name in Switches(Encoding.Latin1.GetString(table.Read()), checksum))
            {
                if (Known.Contains(name)) names.Add(name.ToLowerInvariant());
                else if (!name.Equals("rebuildnodes", StringComparison.OrdinalIgnoreCase)) lost.Add(name);
            }
        }

        return ([.. names], lost);
    }

    /// <summary>
    /// The <c>Compatibility</c> list of the IWAD entry the engine would take a file for: the first in <c>iwadinfo.txt</c>
    /// whose <c>MustContain</c> lumps the file all has (the file lists the most particular first).
    /// </summary>
    public static IReadOnlyList<string> Identify(string iwadinfo, Func<string, bool> has)
    {
        var s = new Scanner(iwadinfo, "iwadinfo.txt");
        while (!s.AtEnd)
        {
            var t = s.Next();
            if (!t.Is("IWad"))
            {
                if (t.Is("{")) s.SkipBlock();
                continue;
            }

            s.Expect("{");
            var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            while (!s.Accept("}"))
            {
                // "Key = value, value" or a bare flag ("SkipBexStringsIfLanguage").
                var key = s.ExpectIdentifier();
                var list = values[key] = [];
                if (!s.Accept("=")) continue;
                do list.Add(s.Next().Text);
                while (s.Accept(","));
            }

            if (values.TryGetValue("MustContain", out var lumps) && lumps.All(has)) return values.GetValueOrDefault("Compatibility") ?? [];
        }

        return [];
    }

    /// <summary>The switch names <c>compatibility.txt</c> gives the map with <paramref name="checksum"/> (several checksums share one block).</summary>
    public static IReadOnlyList<string> Switches(string table, string checksum)
    {
        var names = new List<string>();
        var mine = false;
        foreach (Match m in Entry().Matches(table))
        {
            if (m.Groups["sum"].Success)
            {
                mine |= string.Equals(m.Groups["sum"].Value, checksum, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (mine) names.AddRange(m.Groups["body"].Value.Split('\n').Select(l => (l.IndexOf("//", StringComparison.Ordinal) is var c and >= 0 ? l[..c] : l).Trim()).Where(l => l.Length > 0).Select(l => l.Split([' ', '\t'])[0]));
            mine = false;
        }

        return names;
    }

    [GeneratedRegex(@"^(?<sum>[0-9A-Fa-f]{32})\b|^\{(?<body>[^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex Entry();
}
