using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>
/// An object a map can place, as the engine defines it: its number in maps, its name, the sprite and
/// frame it shows when it appears, and its size.
/// </summary>
public sealed record ThingType(int Number, string ClassName, string Name, string? Sprite, char Frame, double Radius, double Height, double ScaleX, double ScaleY, bool OnCeiling);

/// <summary>
/// The objects of one game family, read from the engine's own data (gzdoom.pk3): the MAPINFO
/// <c>DoomEdNums</c> give each number its class, the ZScript actor classes give size, tag and the
/// first frame of the Spawn state (following parent classes), and the language tables turn a tag
/// such as <c>$FN_IMP</c> into "Imp". Read once per family on a worker.
/// </summary>
public sealed partial class ThingCatalog
{
    private readonly Dictionary<int, ThingType> _byNumber;

    private ThingCatalog(Dictionary<int, ThingType> byNumber) => _byNumber = byNumber;

    public static ThingCatalog Empty { get; } = new([]);

    public IReadOnlyCollection<ThingType> All => _byNumber.Values;

    public ThingType? Find(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>The MAPINFO file each engine family starts from (GZDoom's iwadinfo.txt picks the same ones).</summary>
    public static string MapInfoFor(string family) => family switch
    {
        "heretic" => "mapinfo/heretic.txt",
        "hexen" => "mapinfo/hexen.txt",
        "strife" => "mapinfo/strife.txt",
        "chex" => "mapinfo/chex.txt",
        _ => "mapinfo/doomcommon.txt",
    };

    /// <summary>The class the engine uses for the player in each family.</summary>
    public static string PlayerClassFor(string family) => family switch
    {
        "heretic" => "HereticPlayer",
        "hexen" => "FighterPlayer",
        "strife" => "StrifePlayer",
        "chex" => "ChexPlayer",
        _ => "DoomPlayer",
    };

    public static ThingCatalog Load(ResourceContainer engine, string family)
    {
        string? Text(string path) => engine.FindPath(path) is { } e ? Encoding.UTF8.GetString(e.Read()) : null;

        // Numbers to class names, following includes; later entries replace earlier ones.
        var numbers = new Dictionary<int, string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ReadMapInfo(string path)
        {
            if (!seen.Add(path) || Text(path) is not { } text) return;
            text = StripComments(text);
            foreach (Match m in IncludeLine().Matches(text)) ReadMapInfo(m.Groups[1].Value);
            foreach (Match block in DoomEdNumsBlock().Matches(text))
            {
                foreach (Match entry in EdNumEntry().Matches(block.Groups[1].Value))
                {
                    numbers[int.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture)] = entry.Groups[2].Value;
                }
            }
        }

        ReadMapInfo(MapInfoFor(family));

        // Player and deathmatch starts are built into the engine rather than listed in MAPINFO.
        var player = PlayerClassFor(family);
        var starts = new Dictionary<int, string>
        {
            [1] = "Player 1 start",
            [2] = "Player 2 start",
            [3] = "Player 3 start",
            [4] = "Player 4 start",
            [11] = "Deathmatch start",
        };
        if (family == "hexen")
        {
            for (var i = 0; i < 4; i++) starts[9100 + i] = $"Player {i + 5} start";
        }

        foreach (var number in starts.Keys) numbers[number] = player;

        var classes = new Dictionary<string, ActorClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in engine.Entries)
        {
            if (!e.FullPath.StartsWith("zscript/", StringComparison.OrdinalIgnoreCase) || !e.FullPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var c in ParseClasses(StripComments(Encoding.UTF8.GetString(e.Read())))) classes.TryAdd(c.Name, c);
        }

        var strings = LanguageStrings(engine);
        var types = new Dictionary<int, ThingType>();
        foreach (var (number, className) in numbers)
        {
            if (!classes.ContainsKey(className)) continue;
            var radius = Inherited(classes, className, c => c.Radius) ?? 20;
            var height = Inherited(classes, className, c => c.Height) ?? 16;
            var scale = Inherited(classes, className, c => c.Scale) ?? 1;
            var scaleX = Inherited(classes, className, c => c.ScaleX) ?? scale;
            var scaleY = Inherited(classes, className, c => c.ScaleY) ?? scale;
            var ceiling = Inherited(classes, className, c => c.SpawnCeiling) ?? false;
            var tag = Inherited(classes, className, c => c.Tag);
            var (sprite, frame) = SpawnFrame(classes, className);
            var declared = classes[className].Name;
            var name = starts.TryGetValue(number, out var start) ? start : NameOf(tag, declared, strings);
            types[number] = new ThingType(number, declared, name, sprite, frame, radius, height, scaleX, scaleY, ceiling);
        }

        return new ThingCatalog(types);
    }

    private sealed class ActorClass
    {
        public required string Name { get; init; }

        public string? Parent { get; init; }

        public double? Radius { get; set; }

        public double? Height { get; set; }

        public double? Scale { get; set; }

        public double? ScaleX { get; set; }

        public double? ScaleY { get; set; }

        public bool? SpawnCeiling { get; set; }

        public string? Tag { get; set; }

        /// <summary>State lines in order, with labels as "Label:" entries.</summary>
        public List<string> States { get; } = [];
    }

    private static T? Inherited<T>(Dictionary<string, ActorClass> classes, string name, Func<ActorClass, T?> get)
        where T : struct
    {
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            if (get(c) is { } v) return v;
        }

        return null;
    }

    private static string? Inherited(Dictionary<string, ActorClass> classes, string name, Func<ActorClass, string?> get)
    {
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            if (get(c) is { } v) return v;
        }

        return null;
    }

    /// <summary>The sprite and frame of the first state under Spawn, following Goto and parent classes; null for an invisible object.</summary>
    private static (string? Sprite, char Frame) SpawnFrame(Dictionary<string, ActorClass> classes, string name)
    {
        var label = "Spawn";
        for (var hops = 0; hops < 16; hops++)
        {
            // The nearest class (itself or a parent) that has the label.
            ActorClass? owner = null;
            int at = -1;
            for (var c = classes.GetValueOrDefault(name); c is not null && owner is null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
            {
                at = c.States.FindIndex(s => string.Equals(s, label + ":", StringComparison.OrdinalIgnoreCase));
                if (at >= 0) owner = c;
            }

            if (owner is null) return (null, 'A');
            for (var i = at + 1; i < owner.States.Count; i++)
            {
                var line = owner.States[i];
                if (line.EndsWith(':')) continue;
                var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words[0].Equals("Goto", StringComparison.OrdinalIgnoreCase) && words.Length > 1)
                {
                    var target = words[1];
                    if (target.StartsWith("Super::", StringComparison.OrdinalIgnoreCase))
                    {
                        name = owner.Parent ?? name;
                        target = target[7..];
                    }
                    else
                    {
                        name = owner.Name;
                    }

                    label = target.Split('+')[0];
                    break;
                }

                if (words[0] is "Stop" or "stop" or "Loop" or "loop" or "Wait" or "wait" or "Fail" or "fail") return (null, 'A');
                if (words.Length < 2) return (null, 'A');
                var sprite = words[0].Trim('"').ToUpperInvariant();
                var frames = words[1].Trim('"').ToUpperInvariant();
                if (sprite.Length != 4 || sprite is "TNT1" or "####" or "----" || frames.Length == 0 || frames[0] is < 'A' or > ']') return (null, 'A');
                return (sprite, frames[0]);
            }
        }

        return (null, 'A');
    }

    private static string NameOf(string? tag, string className, Dictionary<string, string> strings)
    {
        if (tag is not null)
        {
            var text = tag;
            for (var i = 0; i < 4 && text.StartsWith('$'); i++)
            {
                var key = text.TrimStart('$');
                if (!strings.TryGetValue(key, out var next)) break;
                text = next;
            }

            if (!text.StartsWith('$') && text.Length > 0) return text;
        }

        // No tag: the class name in words ("TallGreenColumn" becomes "Tall green column").
        var words = CamelWords().Replace(className, " $1").Trim();
        return words.Length == 0 ? className : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    /// <summary>English strings by identifier, from language.def (aliases such as "$$CC_IMP") and the language CSV tables.</summary>
    private static Dictionary<string, string> LanguageStrings(ResourceContainer engine)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in engine.Entries)
        {
            var path = e.FullPath;
            if (!path.StartsWith("language.", StringComparison.OrdinalIgnoreCase)) continue;
            var text = Encoding.UTF8.GetString(e.Read());
            if (path.EndsWith(".def", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".enu", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in DefEntry().Matches(StripComments(text))) strings.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            }
            else if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".0", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in Csv(text))
                {
                    if (row.Count >= 2 && row[1].Length > 0 && row[0].Length > 0) strings.TryAdd(row[1], row[0]);
                }
            }
        }

        return strings;
    }

    /// <summary>Rows of a CSV table (quoted fields may hold commas, quotes and line breaks).</summary>
    private static IEnumerable<List<string>> Csv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\n':
                    row.Add(field.ToString().TrimEnd('\r'));
                    field.Clear();
                    yield return row;
                    row = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>The actor classes declared in one ZScript file: their parent, the properties WadSmith uses and their state lines.</summary>
    private static List<ActorClass> ParseClasses(string text)
    {
        var found = new List<ActorClass>();
        foreach (Match m in ClassHeader().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchingBrace(text, open);
            if (close < 0) continue;
            var body = text[(open + 1)..close];
            var c = new ActorClass { Name = m.Groups[1].Value, Parent = m.Groups[2].Success ? m.Groups[2].Value : null };

            if (BlockAfter(body, DefaultHeader()) is { } defaults)
            {
                foreach (var raw in defaults.Split((char[])[';', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var item = raw.Trim();
                    if (item.Length == 0) continue;
                    if (item[0] is '+' or '-')
                    {
                        foreach (var flag in item.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            var on = flag[0] == '+';
                            var name = flag.TrimStart('+', '-');
                            if (name.Equals("SPAWNCEILING", StringComparison.OrdinalIgnoreCase)) c.SpawnCeiling = on;
                        }

                        continue;
                    }

                    var space = item.IndexOfAny([' ', '\t']);
                    if (space < 0) continue;
                    var key = item[..space];
                    var value = item[(space + 1)..].Trim();
                    switch (key.ToUpperInvariant())
                    {
                        case "RADIUS": c.Radius = Number(value); break;
                        case "HEIGHT": c.Height = Number(value); break;
                        case "SCALE": c.Scale = Number(value); break;
                        case "XSCALE": c.ScaleX = Number(value); break;
                        case "YSCALE": c.ScaleY = Number(value); break;
                        case "TAG": c.Tag = value.Trim('"'); break;
                    }
                }
            }

            if (BlockAfter(body, StatesHeader()) is { } states)
            {
                foreach (var raw in states.Split((char[])[';', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var item = raw.Trim();
                    // Leading labels ("Melee:", "Spawn:"), possibly several, possibly followed by a state.
                    while (LeadingLabel().Match(item) is { Success: true } label)
                    {
                        c.States.Add(label.Groups[1].Value + ":");
                        item = item[label.Length..].Trim();
                    }

                    if (item.Length > 0) c.States.Add(item);
                }
            }

            found.Add(c);
        }

        return found;
    }

    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? BlockAfter(string body, Regex header)
    {
        var m = header.Match(body);
        if (!m.Success) return null;
        var open = m.Index + m.Length - 1;
        var close = MatchingBrace(body, open);
        return close < 0 ? null : body[(open + 1)..close];
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return i;
        }

        return -1;
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }

                i++;
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"(?im)^\s*include\s+""([^""]+)""")]
    private static partial Regex IncludeLine();

    [GeneratedRegex(@"(?is)\bDoomEdNums\s*\{(.*?)\}")]
    private static partial Regex DoomEdNumsBlock();

    [GeneratedRegex("""(?m)^\s*(\d+)\s*=\s*"?([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex EdNumEntry();

    [GeneratedRegex(@"(?<![A-Za-z_])(?<!extend\s+)(?<!mixin\s+)class\s+([A-Za-z_]\w*)\s*(?::\s*([A-Za-z_]\w*))?[^{;]*\{")]
    private static partial Regex ClassHeader();

    [GeneratedRegex(@"(?i)\bDefault\s*\{")]
    private static partial Regex DefaultHeader();

    [GeneratedRegex(@"(?i)\bStates\s*(\([^)]*\))?\s*\{")]
    private static partial Regex StatesHeader();

    [GeneratedRegex(@"^([A-Za-z_][\w.]*)\s*:(?!:)")]
    private static partial Regex LeadingLabel();

    [GeneratedRegex("""(?m)^\s*([A-Za-z_]\w*)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;""")]
    private static partial Regex DefEntry();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex CamelWords();
}
