using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Text;

namespace WadSmith.Core.Games;

/// <summary>
/// A map's definition in MAPINFO's braces format, as GZDoom reads it: what follows the slot in the header
/// (<c>lookup "HUSTR_1"</c>, or the title in quotes) and the properties as written, one a line — the file's
/// <c>defaultmap</c> first, then the map's own block.
/// </summary>
public sealed record MapDefinition(string Slot, string Header, IReadOnlyList<string> Properties)
{
    /// <summary>The name of a property line (the word before any "=").</summary>
    public static string KeyOf(string property)
    {
        var end = property.IndexOfAny([' ', '\t', '=']);
        return end < 0 ? property : property[..end];
    }

    /// <summary>The last value written for <paramref name="key"/>, as plain text (the first value, without quotes); null when it has none.</summary>
    public string? Text(string key)
    {
        var line = Properties.LastOrDefault(p => string.Equals(KeyOf(p), key, StringComparison.OrdinalIgnoreCase));
        var eq = line?.IndexOf('=', StringComparison.Ordinal) ?? -1;
        if (line is null || eq < 0) return null;
        var value = line[(eq + 1)..].Trim();
        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            return close < 0 ? value[1..] : value[1..close];
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        return (comma < 0 ? value : value[..comma]).Trim();
    }
}

/// <summary>The base game's map definitions as the engine reads them.</summary>
/// <param name="Maps">By slot (MAP01, E1M1 …): the last definition read wins, as in the engine.</param>
/// <param name="OldFormat">The game defines maps in MAPINFO's old format (Hexen's, without braces): read too, each written in the braces format (D62/Phase 9).</param>
/// <param name="Strings">The engine's language table (English), which a value starting with "$" names into.</param>
public sealed record MapDefinitions(IReadOnlyDictionary<string, MapDefinition> Maps, bool OldFormat, IReadOnlyDictionary<string, string> Strings)
{
    public static readonly MapDefinitions None = new(new Dictionary<string, MapDefinition>(LumpName.Comparer), false, new Dictionary<string, string>());

    public MapDefinition? For(string slot) => Maps.GetValueOrDefault(slot);

    /// <summary>A value as the engine shows it: "$MUSIC_RUNNIN" becomes D_RUNNIN through the language table; anything else is itself.</summary>
    public string? Resolve(string? value) => value is { Length: > 1 } && value[0] == '$' ? Strings.GetValueOrDefault(value[1..]) ?? value : value;

    /// <summary>The slots defined, in the game's order: the standard names (MAP01 before MAP02, E1M1 before E1M9 before E1M10) first, then any others (an add-on episode's LEVEL01).</summary>
    public IReadOnlyList<string> Slots => Maps.Keys.OrderBy(s => s, SlotOrder.Instance).ToList();

    private sealed class SlotOrder : IComparer<string>
    {
        public static readonly SlotOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var (xl, yl) = (Digits(x), Digits(y));
            var byKind = Standard(xl.Letters).CompareTo(Standard(yl.Letters));
            if (byKind != 0) return byKind;
            var byLetters = string.Compare(xl.Letters, yl.Letters, StringComparison.OrdinalIgnoreCase);
            if (byLetters != 0) return byLetters;
            var byLength = xl.Numbers.Length.CompareTo(yl.Numbers.Length);
            return byLength != 0 ? byLength : string.Compare(xl.Numbers, yl.Numbers, StringComparison.Ordinal);
        }

        private static int Standard(string letters) => letters.Equals("MAP", StringComparison.OrdinalIgnoreCase) || letters.Equals("EM", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        /// <summary>A slot's letters and its runs of digits, each run padded to three places, so E1M9 sorts before E1M10.</summary>
        private static (string Letters, string Numbers) Digits(string s)
        {
            var letters = new StringBuilder();
            var numbers = new StringBuilder();
            var run = new StringBuilder();
            foreach (var c in s + " ")
            {
                if (char.IsDigit(c))
                {
                    run.Append(c);
                    continue;
                }

                if (run.Length > 0)
                {
                    numbers.Append(run.ToString().PadLeft(3, '0'));
                    run.Clear();
                }

                if (c != ' ') letters.Append(c);
            }

            return (letters.ToString(), numbers.ToString());
        }
    }
}

/// <summary>
/// Reads map definitions from MAPINFO in the braces format GZDoom's own files use. Measured in GZDoom 4.14
/// (2026-10-12): a project's <c>map</c> block replaces the base game's definition of that map outright — a block
/// with only a title loses the next map, the sky, the music and the par time — so a project block starts from the
/// base definition; <c>defaultmap</c> applies to the maps after it in the same file (and the files it includes).
/// </summary>
public static class MapInfo
{
    /// <summary>
    /// The game's definitions: the engine's MAPINFO for the base game, then each file's own ZMAPINFO (or MAPINFO) in load
    /// order (<paramref name="files"/>: the base game, then an add-on over it), with the engine's language table for the
    /// values that name into it. Reads files: call it on a worker.
    /// </summary>
    public static MapDefinitions For(GameDefinition game, IReadOnlyList<ResourceContainer> files, string engineDataPath)
    {
        var maps = new Dictionary<string, MapDefinition>(LumpName.Comparer);
        var engine = ResourceContainer.Open(engineDataPath);
        string? FromEngine(string path) => engine.FindPath(path) is { } e ? Encoding.Latin1.GetString(e.Read()) : null;
        var old = false;
        var file = MapTitles.MapInfoFile(game.AddOnFor ?? game.Id);
        if (FromEngine(file) is { } text) old |= Parse(text, file, FromEngine, maps, []);

        // GZDoom reads one of a file's own: ZMAPINFO when it has both.
        foreach (var container in files)
        {
            if ((container.Find("ZMAPINFO") ?? container.Find("MAPINFO")) is { } own)
            {
                old |= Parse(Encoding.Latin1.GetString(own.Read()), own.Name, name => container.Find(name) is { } e ? Encoding.Latin1.GetString(e.Read()) : FromEngine(name), maps, []);
            }
        }

        if (old) ResolveWarps(maps);
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var support = Path.Combine(Path.GetDirectoryName(engineDataPath)!, "game_support.pk3");
        if (File.Exists(support) && ResourceContainer.Open(support).FindPath("language.csv") is { } lang) MapTitles.ReadLanguageCsv(lang.Read(), strings);
        if (engine.FindPath("language.def") is { } def) ReadLanguageDef(Encoding.Latin1.GetString(def.Read()), strings);
        return new MapDefinitions(maps, old, strings);
    }

    /// <summary>
    /// The engine's <c>language.def</c>: the strings that are never translated, one a line as <c>KEY = "value";</c> — the
    /// music names among them (MUSIC_RUNNIN = "runnin": Doom's lump is D_RUNNIN, the engine adds the prefix). A key the
    /// table already has keeps its value.
    /// </summary>
    public static void ReadLanguageDef(string text, Dictionary<string, string> into)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('[') || eq <= 0) continue;
            var key = line[..eq].Trim();
            var open = line.IndexOf('"', eq);
            var close = open < 0 ? -1 : line.IndexOf('"', open + 1);
            if (open < 0 || close < 0 || key.Any(char.IsWhiteSpace)) continue;
            into.TryAdd(key, line[(open + 1)..close]);
        }
    }

    /// <summary>
    /// Reads the map definitions in one MAPINFO text into <paramref name="into"/>, following its includes through
    /// <paramref name="include"/> with the same <c>defaultmap</c>. Returns whether it found a map in the old format.
    /// </summary>
    public static bool Parse(string text, string source, Func<string, string?> include, Dictionary<string, MapDefinition> into, List<string> defaults)
    {
        var s = new Scanner(text, source);
        var old = false;
        while (!s.AtEnd)
        {
            var t = s.Next();
            if (t.Is("include"))
            {
                var name = s.ExpectName();
                if (include(name) is { } inner) old |= Parse(inner, name, include, into, defaults);
                continue;
            }

            if (t.Is("defaultmap") || t.Is("adddefaultmap"))
            {
                s.Expect("{");
                if (t.Is("defaultmap")) defaults.Clear();
                defaults.AddRange(Body(s));
                continue;
            }

            if (t.Is("map"))
            {
                var slotToken = s.Next();
                var slot = slotToken.Text.ToUpperInvariant();

                // The header is what follows the slot on its own line (the old format's properties start on the lines after it).
                var header = new List<Token>();
                while (!s.AtEnd && !s.Peek().Is("{") && !TopLevel(s.Peek()) && s.Peek().Line == slotToken.Line) header.Add(s.Next());
                if (!s.Accept("{"))
                {
                    old = true;
                    var number = int.TryParse(slot, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
                    var name = number > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MAP{number:00}") : slot;
                    into[name] = OldMap(s, name, number, Render(header));
                    continue;
                }

                into[slot] = new MapDefinition(slot, Render(header), [.. defaults, .. Body(s)]);
                continue;
            }

            // Anything else (gameinfo, episode, cluster, skill …): its head, then its block when it has one.
            while (!s.AtEnd && !s.Peek().Is("{") && !TopLevel(s.Peek())) s.Next();
            if (s.Accept("{")) s.SkipBlock();
        }

        return old;
    }

    /// <summary>
    /// A map in MAPINFO's old format (Hexen's: <c>map 1 "WINNOWING HALL"</c>, then a property a line until the next map or
    /// cluster), written in the braces format GZDoom reads in a project's ZMAPINFO: its number as <c>levelnum</c> (which
    /// Hexen's exits, its SNDINFO music and its scripts name it by), and the properties it gives, a <c>next</c> by warp number
    /// kept as GZDoom keeps it (<c>&amp;wt@02</c>) until <see cref="ResolveWarps"/> names the map.
    /// </summary>
    private static MapDefinition OldMap(Scanner s, string slot, int number, string header)
    {
        var properties = new List<string>();
        if (number > 0) properties.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"levelnum = {number}"));
        // "cluster" is a map's own line in this format (its clusters are "clusterdef"), so only the other heads end the map.
        static bool Ends(Token t) => TopLevel(t) && !t.Is("cluster");
        while (!s.AtEnd && !Ends(s.Peek()))
        {
            var key = s.Next();
            var values = new List<Token>();
            while (!s.AtEnd && s.Peek().Line == key.Line && !Ends(s.Peek())) values.Add(s.Next());
            string Value(int i) => i < values.Count ? values[i].Kind == TokenKind.String ? Quote(values[i].Text) : values[i].Text : "";
            string Named(int i) => i < values.Count ? Quote(values[i].Text) : "\"\"";
            var line = key.Text.ToLowerInvariant() switch
            {
                "warptrans" or "cluster" or "cdtrack" or "par" when values.Count > 0 => $"{key.Text.ToLowerInvariant()} = {Value(0)}",
                "next" or "secretnext" when values.Count > 0 => values[0].Kind == TokenKind.Integer ? $"{key.Text.ToLowerInvariant()} = \"&wt@{int.Parse(values[0].Text, System.Globalization.CultureInfo.InvariantCulture):00}\"" : $"{key.Text.ToLowerInvariant()} = {Named(0)}",
                "sky1" or "sky2" when values.Count > 0 => $"{key.Text.ToLowerInvariant()} = {Named(0)}{(values.Count > 1 ? ", " + Value(1) : "")}",
                "fadetable" or "music" or "titlepatch" when values.Count > 0 => $"{key.Text.ToLowerInvariant()} = {Named(0)}",
                "lightning" or "doublesky" or "nointermission" or "evenlighting" or "smoothlighting" or "nosoundclipping" => key.Text.ToLowerInvariant(),
                _ => null,
            };
            if (line is not null) properties.Add(line);
        }

        return new MapDefinition(slot, header, properties);
    }

    /// <summary>
    /// The old format's <c>next</c> by warp number named as the map with that <c>warptrans</c> (GZDoom resolves it as the
    /// game starts), so a project block can say it; a warp no map has is left as it is.
    /// </summary>
    public static void ResolveWarps(Dictionary<string, MapDefinition> maps)
    {
        var byWarp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slot, map) in maps)
        {
            if (map.Text("warptrans") is { } warp && int.TryParse(warp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var w)) byWarp.TryAdd(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"&wt@{w:00}"), slot);
        }

        foreach (var slot in maps.Keys.ToList())
        {
            var map = maps[slot];
            if (!map.Properties.Any(p => p.Contains("&wt@", StringComparison.Ordinal))) continue;
            maps[slot] = map with
            {
                Properties = [.. map.Properties.Select(p => byWarp.FirstOrDefault(w => p.Contains(w.Key, StringComparison.Ordinal)) is { Key: { } key } found ? p.Replace(key, found.Value, StringComparison.Ordinal) : p)],
            };
        }
    }

    private static bool TopLevel(Token t) => t.Kind == TokenKind.Identifier && (t.Is("map") || t.Is("clusterdef") || t.Is("include") || t.Is("defaultmap") || t.Is("adddefaultmap") || t.Is("gamedefaults") || t.Is("clearepisodes") || t.Is("clearskills") || t.Is("episode") || t.Is("cluster") || t.Is("gameinfo"));

    /// <summary>The lines of a block whose opening brace has been read, each as written (tokens on one line make one property).</summary>
    private static List<string> Body(Scanner s)
    {
        var lines = new List<string>();
        var line = new List<Token>();
        var depth = 1;
        while (true)
        {
            var t = s.Next();
            if (t.Kind == TokenKind.End) throw s.Error("a { block is never closed.", t.Line);
            if (t.Is("{")) depth++;
            if (t.Is("}") && --depth == 0) break;
            if (line.Count > 0 && t.Line != line[0].Line)
            {
                lines.Add(Render(line));
                line.Clear();
            }

            line.Add(t);
        }

        if (line.Count > 0) lines.Add(Render(line));
        return lines;
    }

    /// <summary>Tokens back as text: strings quoted again, a space between words, none before a comma.</summary>
    private static string Render(IReadOnlyList<Token> tokens)
    {
        var b = new StringBuilder();
        foreach (var t in tokens)
        {
            if (b.Length > 0 && !t.Is(",")) b.Append(' ');
            b.Append(t.Kind == TokenKind.String ? Quote(t.Text) : t.Text);
        }

        return b.ToString();
    }

    /// <summary>Text as a MAPINFO string.</summary>
    public static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
