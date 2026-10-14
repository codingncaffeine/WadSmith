using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WadSmith.Core.Library;

/// <summary>
/// The actors a downloaded object declares in its DECORATE or ZSCRIPT, read well enough to place them (D61): name, parent,
/// size, the first picture of its Spawn state, whether it is a monster or flies as a missile; and the few changes WadSmith
/// makes before copying the text in — <c>replaces</c> taken off (placing an object must not change every Imp of the map),
/// DECORATE's own editor numbers taken off (the project gives the numbers), clashing sprite names renamed, include paths
/// pointed at where the files now live.
/// </summary>
public static partial class ActorScripts
{
    /// <summary>One declared actor; its sprite is the first its Spawn state shows (TNT1, the empty one, passed over), null when it shows none of its own.</summary>
    public sealed record Declared(string ClassName, string? Parent, string? Replaces, int? DoomEdNum, double? Radius, double? Height, double? Scale, string? Sprite, char Frame, bool Monster, bool Missile, bool Abstract);

    /// <summary>The actors of a DECORATE (<paramref name="zscript"/> false) or ZSCRIPT text, in their order.</summary>
    public static IReadOnlyList<Declared> Read(string text, bool zscript)
    {
        var clean = StripComments(text);
        var found = new List<Declared>();
        foreach (Match m in (zscript ? ZClass() : DecorateActor()).Matches(clean))
        {
            var open = clean.IndexOf('{', m.Index + m.Length - 1);
            if (open < 0) continue;
            var body = Block(clean, open);
            var name = Unquote(m.Groups["name"].Value);
            var parent = m.Groups["parent"].Success ? Unquote(m.Groups["parent"].Value) : null;
            var replaces = m.Groups["replaces"].Success ? Unquote(m.Groups["replaces"].Value) : null;
            int? number = m.Groups["num"].Success && int.TryParse(m.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            var (sprite, frame) = SpawnPicture(body);
            found.Add(new Declared(name, parent, replaces, number, Number(body, "Radius"), Number(body, "Height"), Number(body, "Scale"), sprite, frame,
                MonsterFlag().IsMatch(body), MissileFlag().IsMatch(body), zscript && m.Value.Contains("abstract", StringComparison.OrdinalIgnoreCase)));
        }

        return found;
    }

    /// <summary>
    /// The text as it goes into the project: <c>replaces</c> and DECORATE editor numbers taken off its headers, the sprites of
    /// <paramref name="sprites"/> renamed in state lines and quoted names, and every quoted path <paramref name="path"/> knows
    /// (an <c>#include</c>, a model folder) pointed where the file now is. Returns the text and what was taken off, in words.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Notes) Rewrite(string text, bool zscript, IReadOnlyDictionary<string, string> sprites, Func<string, string?> path)
    {
        var notes = new List<string>();
        var result = (zscript ? ZClass() : DecorateActor()).Replace(text, m =>
        {
            var header = m.Value;
            if (m.Groups["replaces"].Success)
            {
                notes.Add($"{Unquote(m.Groups["name"].Value)} replaced {Unquote(m.Groups["replaces"].Value)} in every map; here it is an object of its own, placed where you put it.");
                header = header.Remove(m.Groups["rkw"].Index - m.Index, m.Groups["replaces"].Index + m.Groups["replaces"].Length - m.Groups["rkw"].Index);
            }

            if (!zscript && m.Groups["num"].Success)
            {
                var at = header.LastIndexOf(m.Groups["num"].Value, StringComparison.Ordinal);
                if (at >= 0) header = header.Remove(at, m.Groups["num"].Value.Length);
            }

            return header;
        });

        if (sprites.Count > 0)
        {
            result = StateLine().Replace(result, m => sprites.TryGetValue(m.Groups["sprite"].Value.ToUpperInvariant(), out var to) ? m.Value.Remove(m.Groups["sprite"].Index - m.Index, 4).Insert(m.Groups["sprite"].Index - m.Index, to) : m.Value);
            result = QuotedSprite().Replace(result, m => sprites.TryGetValue(m.Groups[1].Value.ToUpperInvariant(), out var to) ? "\"" + to + m.Groups[2].Value + "\"" : m.Value);
        }

        // An #include, quoted or not (DECORATE takes both), then any other quoted path.
        result = Include().Replace(result, m => path(m.Groups[2].Value) is { } moved ? m.Groups[1].Value + "\"" + moved + "\"" : m.Value);
        result = Quoted().Replace(result, m => path(m.Groups[1].Value) is { } moved ? "\"" + moved + "\"" : m.Value);
        return (result, notes);
    }

    /// <summary>A GLDEFS text with the sprites of <paramref name="sprites"/> renamed in its frame and sprite names.</summary>
    public static string RenameInDefinitions(string text, IReadOnlyDictionary<string, string> sprites) =>
        sprites.Count == 0 ? text : DefinitionFrame().Replace(text, m => sprites.TryGetValue(m.Groups[2].Value.ToUpperInvariant(), out var to) ? m.Groups[1].Value + to : m.Value);

    /// <summary>The paths a text's <c>#include</c> lines name, quoted or not.</summary>
    public static IReadOnlyList<string> Includes(string text) => [.. Include().Matches(StripComments(text)).Select(m => m.Groups[2].Value)];

    /// <summary>The class names a text declares, both syntaxes (for clashes with what the game or project already has).</summary>
    public static IReadOnlyList<string> ClassNames(string text, bool zscript) => [.. Read(text, zscript).Select(d => d.ClassName)];

    private static (string? Sprite, char Frame) SpawnPicture(string body)
    {
        var spawn = SpawnLabel().Match(body);
        if (!spawn.Success) return (null, 'A');
        foreach (Match line in StateLine().Matches(body, spawn.Index + spawn.Length))
        {
            var sprite = line.Groups["sprite"].Value.ToUpperInvariant();
            if (sprite is "TNT1" or "####" or "----") continue;
            var frames = line.Groups["frames"].Value.Trim('"');
            return (sprite, frames.Length > 0 && char.IsLetterOrDigit(frames[0]) ? char.ToUpperInvariant(frames[0]) : 'A');
        }

        return (null, 'A');
    }

    private static double? Number(string body, string name)
    {
        var m = Regex.Match(body, @"(?im)(?:^|[{;])\s*" + name + @"\s+(-?\d+(?:\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>The text of the block opening at <paramref name="open"/>, braces matched.</summary>
    private static string Block(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[(open + 1)..i];
        }

        return text[(open + 1)..];
    }

    /// <summary>The text with // and /* */ comments blanked (strings kept), so positions stay where they were.</summary>
    internal static string StripComments(string text)
    {
        var sb = new StringBuilder(text);
        var inString = false;
        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '/')
            {
                while (i < sb.Length && sb[i] != '\n') sb[i++] = ' ';
            }
            else if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '*')
            {
                while (i < sb.Length && !(sb[i] == '*' && i + 1 < sb.Length && sb[i + 1] == '/'))
                {
                    if (sb[i] != '\n') sb[i] = ' ';
                    i++;
                }

                if (i + 1 < sb.Length) sb[i] = sb[i + 1] = ' ';
                i++;
            }
        }

        return sb.ToString();
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    [GeneratedRegex(@"(?im)^[ \t]*actor[ \t]+(?<name>""[^""]+""|[\w.]+)(?:[ \t]*:[ \t]*(?<parent>""[^""]+""|[\w.]+))?(?:[ \t]+(?<rkw>replaces)[ \t]+(?<replaces>""[^""]+""|[\w.]+))?(?:[ \t]+(?<num>\d+))?(?:[ \t]+native)?[ \t]*\r?\n?[ \t]*\{")]
    private static partial Regex DecorateActor();

    [GeneratedRegex(@"(?im)^[ \t]*class[ \t]+(?<name>\w+)(?:[ \t]*:[ \t]*(?<parent>\w+))?(?:[ \t]+(?<rkw>replaces)[ \t]+(?<replaces>\w+))?(?:[ \t]+(?:abstract|native|play|ui|version[ \t]*\([^)]*\)))*[ \t]*\r?\n?[ \t]*\{")]
    private static partial Regex ZClass();

    [GeneratedRegex(@"(?im)(?:^|[{;\s])spawn[ \t]*:")]
    private static partial Regex SpawnLabel();

    /// <summary>A state: a four-character sprite, its frames, then a duration (a number, a random or a variable); at a line's start or after a brace, a semicolon or a label.</summary>
    [GeneratedRegex(@"(?im)(?<=^|[{;:])[ \t]*""?(?<sprite>[A-Z0-9_#-]{4})""?[ \t]+(?<frames>""?[A-Z\[\]\\^#]+""?)[ \t]+(?:-?\d|random|\(|[A-Z_])")]
    private static partial Regex StateLine();

    [GeneratedRegex(@"""([A-Z0-9_]{4})([A-Z\[\]\\^][0-9A-Z]?(?:[A-Z\[\]\\^][0-9])?)""", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedSprite();

    [GeneratedRegex(@"""([^""\r\n]+)""")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?im)^([ \t]*#include[ \t]+)""?([^""\s]+)""?")]
    private static partial Regex Include();

    [GeneratedRegex(@"(?im)(\b(?:frame|sprite)[ \t]+""?)([A-Z0-9_]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionFrame();

    [GeneratedRegex(@"(?im)(^|[\s;{])(\+ISMONSTER|\+COUNTKILL|Monster)\b")]
    private static partial Regex MonsterFlag();

    [GeneratedRegex(@"(?im)(^|[\s;{])(\+MISSILE|Projectile)\b")]
    private static partial Regex MissileFlag();
}
