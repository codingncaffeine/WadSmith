using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>
/// The names players know maps by ("Entryway", "Hangar"), as the engine names them: the game's
/// MAPINFO from gzdoom.pk3 points at a string, the string comes from the language table in
/// game_support.pk3 (with per-game overrides), and a base game's own DEHACKED strings (Freedoom's)
/// replace it. Prefixes like "Level 1:" and "E1M1:" are dropped and shouted names are softened.
/// </summary>
public static partial class MapTitles
{
    /// <param name="game">The base game.</param>
    /// <param name="baseGame">The base game's file, for its own DEHACKED names.</param>
    /// <param name="engineDataPath">gzdoom.pk3; game_support.pk3 is expected beside it.</param>
    public static IReadOnlyDictionary<string, string> For(GameDefinition game, ResourceContainer baseGame, string? engineDataPath)
    {
        var titles = new Dictionary<string, string>(LumpName.Comparer);
        if (engineDataPath is null || !File.Exists(engineDataPath)) return titles;
        var engine = ResourceContainer.Open(engineDataPath);
        var support = Path.Combine(Path.GetDirectoryName(engineDataPath)!, "game_support.pk3");
        var supportContainer = File.Exists(support) ? ResourceContainer.Open(support) : null;

        var mapinfo = engine.FindPath(MapInfoFile(game.Id));
        if (mapinfo is null) return titles;

        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (supportContainer?.FindPath("language.csv") is { } lang) ReadLanguageCsv(lang.Read(), strings);
        foreach (var filter in FilterFolders(game.Id))
        {
            foreach (var path in new[] { $"filter/{filter}/language.csv", $"filter/{filter}/after_iwad/language.csv" })
            {
                if (supportContainer?.FindPath(path) is { } over) ReadLanguageCsv(over.Read(), strings);
            }
        }

        if (baseGame.Find("DEHACKED") is { } deh) ReadBexStrings(Encoding.Latin1.GetString(deh.Read()), strings);

        // The engine's MAPINFO for the game, then the base game's own (Hexen names its maps there).
        var sources = new List<string> { Encoding.Latin1.GetString(mapinfo.Read()) };
        foreach (var own in new[] { "MAPINFO", "ZMAPINFO" })
        {
            if (baseGame.Find(own) is { } lump) sources.Add(Encoding.Latin1.GetString(lump.Read()));
        }

        foreach (var source in sources)
        {
            foreach (Match m in MapLine().Matches(source))
            {
                var slot = m.Groups[1].Value.ToUpperInvariant();
                if (int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) slot = string.Create(CultureInfo.InvariantCulture, $"MAP{number:00}");
                var text = m.Groups[2].Success ? strings.GetValueOrDefault(m.Groups[2].Value) : m.Groups[3].Value;
                if (!string.IsNullOrWhiteSpace(text)) titles[slot] = Clean(text);
            }
        }

        return titles;
    }

    internal static string MapInfoFile(string id) => id switch
    {
        "doom-ultimate" or "doom" or "doom-shareware" or "freedoom1" => "mapinfo/doom1.txt",
        "tnt" => "mapinfo/tnt.txt",
        "plutonia" => "mapinfo/plutonia.txt",
        "heretic" or "heretic-sotsr" or "heretic-shareware" or "blasphemer" => "mapinfo/heretic.txt",
        "hexen" or "hexen-demo" => "mapinfo/hexen.txt",
        "hexen-deathkings" => "mapinfo/hexdd.txt",
        "strife" or "strife-veteran" or "strife-teaser" => "mapinfo/strife.txt",
        "chex" or "chex3" => "mapinfo/chex.txt",
        _ => "mapinfo/doom2.txt",
    };

    private static IEnumerable<string> FilterFolders(string id) => id switch
    {
        "chex3" => ["chex.chex3"],
        "harmony" => ["harmony"],
        "hacx" => ["hacx.hacx1"],
        _ => [],
    };

    /// <summary>"Level 1: Entryway" → "Entryway"; "E1M1:  THE DOCKS" → "The Docks"; "WINNOWING HALL" → "Winnowing Hall".</summary>
    internal static string Clean(string text)
    {
        var t = text.Trim();
        var colon = t.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < 12) t = t[(colon + 1)..].Trim();
        if (t.Length > 0 && t.Any(char.IsLetter) && t.Where(char.IsLetter).All(char.IsUpper))
        {
            t = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant());
        }

        return t;
    }

    /// <summary>The first two columns of the engine's language table: the default (English) text and its identifier.</summary>
    internal static void ReadLanguageCsv(byte[] data, Dictionary<string, string> into)
    {
        var text = Encoding.UTF8.GetString(data);
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var first = true;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\n';
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (!first && row.Count >= 2 && row[1].Length > 0 && row[0].Length > 0) into[row[1]] = row[0];
                    first = false;
                    row.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
    }

    /// <summary>The [STRINGS] section of a BEX/DEHACKED lump: <c>KEY = text</c>, a trailing backslash continuing a line.</summary>
    internal static void ReadBexStrings(string deh, Dictionary<string, string> into)
    {
        var inStrings = false;
        string? key = null;
        var value = new StringBuilder();
        foreach (var raw in deh.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (key is not null)
            {
                var more = line.TrimEnd().EndsWith('\\');
                value.Append(more ? line.TrimEnd()[..^1] : line.Trim());
                if (more) continue;
                into[key] = value.ToString();
                key = null;
                continue;
            }

            var t = line.Trim();
            if (t.StartsWith('['))
            {
                inStrings = t.Equals("[STRINGS]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inStrings || t.Length == 0 || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            key = t[..eq].Trim();
            var rest = t[(eq + 1)..].Trim();
            value.Clear();
            if (rest.EndsWith('\\'))
            {
                value.Append(rest[..^1]);
                continue;
            }

            into[key] = rest;
            key = null;
        }
    }

    [GeneratedRegex("^\\s*map\\s+(\\w+)\\s+(?:lookup\\s+\"([^\"]+)\"|\"([^\"]*)\")", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex MapLine();
}
