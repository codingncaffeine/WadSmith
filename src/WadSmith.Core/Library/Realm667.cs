using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WadSmith.Core.Library;

/// <summary>
/// Realm667, the community's repository of monsters, weapons, items, props and textures for the Doom engine games (D61).
/// It has no API; each category has an RSS feed of its entries (60 a page, paged by <c>start=</c>), and each entry carries
/// its preview picture, an Info list ("Summon: Agaures"), the Credits, a description and the <c>/get/</c> link of its zip.
/// </summary>
public static partial class Realm667
{
    public const string Site = "https://www.realm667.com";

    /// <summary>How many entries a feed page holds.</summary>
    public const int PageSize = 60;

    /// <summary>One category: its path on the site, the shelf group its entries go under, and the style they were made in.</summary>
    public sealed record Category(string Path, string Group, string Style, LibraryKind Kind = LibraryKind.Object);

    /// <summary>The categories WadSmith shows (fonts and sounds are not shelf things), objects first.</summary>
    public static readonly IReadOnlyList<Category> Categories =
    [
        new("beastiary/doom-style", "Monsters", "Doom"),
        new("beastiary/heretic-hexen-style", "Monsters", "Heretic/Hexen"),
        new("beastiary/strife-style", "Monsters", "Strife"),
        new("beastiary/other-style", "Monsters", "Other"),
        new("armory/doom-style", "Weapons", "Doom"),
        new("armory/heretic-hexen-style", "Weapons", "Heretic/Hexen"),
        new("armory/other-sources-styles", "Weapons", "Other"),
        new("item-store/ammunition", "Ammunition", ""),
        new("item-store/health-armor", "Health", ""),
        new("item-store/keys-puzzle", "Keys", ""),
        new("item-store/powerups-artifacts", "Power-ups", ""),
        new("item-store/gadgets", "Power-ups", ""),
        new("item-store/others", "Power-ups", ""),
        new("prop-stop/technical", "Decorations", ""),
        new("prop-stop/vegetation", "Decorations", ""),
        new("prop-stop/light-sources", "Decorations", ""),
        new("prop-stop/gore-corpses", "Decorations", ""),
        new("prop-stop/hell-magic", "Decorations", ""),
        new("prop-stop/friendlies-fauna", "Decorations", ""),
        new("sfx-shoppe/elemental-effects", "Decorations", ""),
        new("sfx-shoppe/particle-spawners", "Decorations", ""),
        new("sfx-shoppe/weather-generators", "Decorations", ""),
        new("sfx-shoppe/other", "Decorations", ""),
        new("texture-stock/doom-style", "Texture packs", "Doom", LibraryKind.TexturePack),
        new("texture-stock/heretic-hexen-style", "Texture packs", "Heretic/Hexen", LibraryKind.TexturePack),
        new("texture-stock/other-sources-styles", "Texture packs", "Other", LibraryKind.TexturePack),
    ];

    /// <summary>The feed page of <paramref name="category"/> starting at entry <paramref name="start"/>.</summary>
    public static Uri FeedUrl(Category category, int start) =>
        new($"{Site}/repository/{category.Path}?format=feed&type=rss{(start > 0 ? string.Create(CultureInfo.InvariantCulture, $"&start={start}") : "")}");

    private static readonly string[] Headings = ["Preview", "Info", "Credits", "Description", "Download"];

    /// <summary>The entries of one feed page of <paramref name="category"/>; an entry without a download link is left out.</summary>
    public static IReadOnlyList<LibraryItem> Parse(string feed, Category category)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(feed);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CorruptDataException($"Realm667's list of {category.Path} could not be read: {ex.Message}");
        }

        var items = new List<LibraryItem>();
        foreach (var item in doc.Descendants("item"))
        {
            var title = WebUtility.HtmlDecode(item.Element("title")?.Value ?? "").Trim();
            var link = item.Element("link")?.Value.Trim() ?? "";
            var html = item.Element("description")?.Value ?? "";
            if (title.Length == 0 || !Uri.TryCreate(link, UriKind.Absolute, out var page)) continue;
            if (Download().Match(html) is not { Success: true } get) continue;
            var download = new Uri(Site + "/get/" + get.Groups[1].Value);
            Uri? preview = Picture().Match(html) is { Success: true } img && Uri.TryCreate(new Uri(Site + "/"), WebUtility.HtmlDecode(img.Groups[1].Value), out var p) ? p : null;

            var sections = Sections(Text(html));
            var info = sections.GetValueOrDefault("Info", "");
            var credits = sections.GetValueOrDefault("Credits", "");
            var description = sections.GetValueOrDefault("Description", "");
            long? size = SizeText().Match(Text(html)) is { Success: true } s ? Bytes(s.Groups[1].Value, s.Groups[2].Value) : null;

            var slug = page.Segments[^1];
            var number = new string([.. slug.TakeWhile(char.IsAsciiDigit)]);
            var id = "r667-" + (number.Length > 0 ? number : Slug(slug));
            var style = category.Style.Length > 0 ? category.Style : StyleOf(info);
            items.Add(new LibraryItem("realm667", id, title, category.Kind, category.Group, style, FamilyOf(info, style), preview, download, page, credits, description, info, size));
        }

        return items;
    }

    /// <summary>
    /// Every entry of <paramref name="category"/>, page after page, each page handed to <paramref name="page"/> as it arrives
    /// (the feed text, for keeping). Stops at a page that brings nothing new.
    /// </summary>
    public static async Task<IReadOnlyList<LibraryItem>> FetchAsync(HttpClient http, Category category, Action<string>? page, CancellationToken cancel)
    {
        var all = new List<LibraryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start < 5000; start += PageSize)
        {
            var text = await http.GetStringAsync(FeedUrl(category, start), cancel).ConfigureAwait(false);
            var items = Parse(text, category);
            var fresh = items.Where(i => seen.Add(i.Id)).ToList();
            if (fresh.Count == 0) break;
            page?.Invoke(text);
            all.AddRange(fresh);
            if (items.Count < PageSize) break;
        }

        return all;
    }

    /// <summary>The page's HTML as lines of text: blocks on lines of their own, tags gone, entities read.</summary>
    internal static string Text(string html)
    {
        var text = Comment().Replace(html, "");
        text = Script().Replace(text, "");
        text = BlockEnd().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        return string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>The text under each heading (Preview, Info, Credits, Description, Download), by heading.</summary>
    internal static Dictionary<string, string> Sections(string text)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (Headings.FirstOrDefault(h => h.Equals(line, StringComparison.OrdinalIgnoreCase)) is { } heading)
            {
                if (current is not null && !found.ContainsKey(current)) found[current] = string.Join('\n', lines);
                current = heading;
                lines.Clear();
                continue;
            }

            if (current is not null) lines.Add(line);
        }

        if (current is not null && !found.ContainsKey(current)) found[current] = string.Join('\n', lines);
        return found;
    }

    private static string StyleOf(string info)
    {
        var palette = Field(info, "Palette");
        return palette.Contains("Heretic", StringComparison.OrdinalIgnoreCase) || palette.Contains("Hexen", StringComparison.OrdinalIgnoreCase) ? "Heretic/Hexen"
            : palette.Contains("Strife", StringComparison.OrdinalIgnoreCase) ? "Strife" : "Doom";
    }

    /// <summary>The family whose colours the entry's pictures are in: its Palette line first, then the style it was made in.</summary>
    internal static string FamilyOf(string info, string style)
    {
        var palette = Field(info, "Palette");
        if (palette.Contains("Hexen", StringComparison.OrdinalIgnoreCase) && !palette.Contains("Heretic", StringComparison.OrdinalIgnoreCase)) return "hexen";
        if (palette.Contains("Heretic", StringComparison.OrdinalIgnoreCase)) return "heretic";
        if (palette.Contains("Strife", StringComparison.OrdinalIgnoreCase)) return "strife";
        if (palette.Contains("Doom", StringComparison.OrdinalIgnoreCase)) return "doom";
        return style switch { "Heretic/Hexen" => "heretic", "Strife" => "strife", _ => "doom" };
    }

    private static string Field(string info, string name) =>
        info.Split('\n').Select(l => l.Split(':', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";

    private static long? Bytes(string number, string unit)
    {
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        return (long)(n * unit.ToUpperInvariant() switch { "MB" => 1048576, "KB" => 1024, _ => 1 });
    }

    private static string Slug(string text) => new([.. text.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40)]);

    [GeneratedRegex(@"href=""[^""]*?/get/([^""?#]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex Download();

    [GeneratedRegex(@"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex Picture();

    [GeneratedRegex(@"\(\s*([\d.,]+)\s*(KB|MB|B)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<(script|style)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Script();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h\d|/tr|/button|/td)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
}
