using System.Text.Json;
using System.Text.RegularExpressions;

namespace WadSmith.Core.Library;

/// <summary>
/// The Doom Wiki's texture packs (D61): the pages of its "Texture packs" category, each pointing at its file in the idgames
/// archive (<c>{{idgames|id=…}}</c>) — the community's own short list of packs worth using. MediaWiki's API, read only; its
/// images sit behind a browser check, so none are fetched.
/// </summary>
public static partial class DoomWiki
{
    public const string Api = "https://doomwiki.org/w/api.php";

    /// <summary>A pack as its page describes it: the archive file it names (by number or path), its first lines, its permissions.</summary>
    public sealed record WikiPack(string Title, Uri Page, int? IdgamesId, string? IdgamesPath, string Intro, string Permissions);

    /// <summary>The packs to offer when the wiki cannot be reached: its category as read on 2026-10-13.</summary>
    public static readonly IReadOnlyList<WikiPack> Known =
    [
        new("OTEX", PageOf("OTEX"), 19427, null, "", ""),
        new("cc4-tex", PageOf("Cc4-tex"), 16911, null, "", ""),
        new("GothicDM textures", PageOf("GothicDM textures"), 10203, null, "", ""),
        new("Ancient Aliens", PageOf("Ancient Aliens"), 18506, null, "", ""),
        new("The Darkening Episode 2", PageOf("The Darkening Episode 2"), 16331, null, "", ""),
        new("The Return", PageOf("The Return"), null, "themes/TeamTNT/return/retres.zip", "", ""),
    ];

    public static Uri PageOf(string title) => new("https://doomwiki.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')));

    public static Uri CategoryUrl => new($"{Api}?action=query&list=categorymembers&cmtitle=Category:Texture_packs&cmnamespace=0&cmlimit=200&format=json");

    public static Uri WikitextUrl(string title) => new($"{Api}?action=parse&page={Uri.EscapeDataString(title)}&prop=wikitext&format=json");

    /// <summary>The page titles of the category answer, the article about texture packs in general left out.</summary>
    public static IReadOnlyList<string> ParseCategory(string json)
    {
        using var doc = Read(json);
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("categorymembers", out var members)) return [];
        return [.. members.EnumerateArray().Select(m => m.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "").Where(t => t.Length > 0 && !t.Equals("Texture pack", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// A pack from its page's wikitext: of the archive files it links, the one named for textures or resources (a megawad's page
    /// also links the maps), else the first; null when it links none.
    /// </summary>
    public static WikiPack? ParsePage(string title, string json)
    {
        using var doc = Read(json);
        if (!doc.RootElement.TryGetProperty("parse", out var parse) || !parse.TryGetProperty("wikitext", out var wt) || !wt.TryGetProperty("*", out var star)) return null;
        var text = star.GetString() ?? "";
        var links = IdgamesLink().Matches(text).Select(m => m.Groups[1].Value).ToList();
        if (links.Count == 0) return null;
        var chosen = links.FirstOrDefault(l => TexturesWords().IsMatch(l)) ?? links[0];
        var fields = chosen.Split('|').Select(f => f.Split('=', 2)).ToList();
        string? Field(string name) => fields.FirstOrDefault(f => f.Length == 2 && f[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))?[1].Trim();
        int? id = int.TryParse(Field("id") ?? (fields.Count > 0 && fields[0].Length == 1 ? fields[0][0] : null), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        var path = Field("file") is { Length: > 0 } f ? (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? f : f + ".zip") : null;
        if (id is null && path is null) return null;
        var shown = DisplayTitle().Match(text) is { Success: true } d ? d.Groups[1].Value.Trim() : title;
        return new WikiPack(shown, PageOf(title), id, path, Plain(Intro(text)), Plain(Section(text, "Permissions")));
    }

    /// <summary>The packs of the category, each read from its page; a page that names no archive file is left out.</summary>
    public static async Task<IReadOnlyList<WikiPack>> FetchAsync(HttpClient http, CancellationToken cancel)
    {
        var packs = new List<WikiPack>();
        foreach (var title in ParseCategory(await http.GetStringAsync(CategoryUrl, cancel).ConfigureAwait(false)))
        {
            if (ParsePage(title, await http.GetStringAsync(WikitextUrl(title), cancel).ConfigureAwait(false)) is { } pack) packs.Add(pack);
        }

        return packs;
    }

    /// <summary>The page's lead: the text before its first heading, without the infobox.</summary>
    private static string Intro(string text)
    {
        var body = DisplayTitle().Replace(text, "");
        var box = body.IndexOf("{{Infobox", StringComparison.OrdinalIgnoreCase);
        if (box >= 0)
        {
            var depth = 0;
            for (var i = box; i < body.Length - 1; i++)
            {
                if (body[i] == '{' && body[i + 1] == '{') { depth++; i++; }
                else if (body[i] == '}' && body[i + 1] == '}') { depth--; i++; if (depth == 0) { body = body[(i + 1)..]; break; } }
            }
        }

        var heading = body.IndexOf("\n==", StringComparison.Ordinal);
        return heading >= 0 ? body[..heading] : body;
    }

    private static string Section(string text, string name)
    {
        var m = Regex.Match(text, @"\n==\s*" + Regex.Escape(name) + @"\s*==\s*\n(.*?)(?=\n==|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Wikitext as plain words: links as their words, templates and markup gone.</summary>
    internal static string Plain(string wikitext)
    {
        var t = Regex.Replace(wikitext, @"\{\{(?:idgames|wp)\|(?:[^}|]*\|)*?(?:title=)?([^}|]*)\}\}", "$1", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\{\{[^{}]*\}\}", "");
        t = Regex.Replace(t, @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1");
        t = Regex.Replace(t, @"\[https?://\S+\s+([^\]]*)\]", "$1");
        t = Regex.Replace(t, @"<ref[^>]*/>|<ref[^>]*>.*?</ref>|<[^>]+>", "", RegexOptions.Singleline);
        t = t.Replace("'''", "", StringComparison.Ordinal).Replace("''", "", StringComparison.Ordinal);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 600 ? t[..600].TrimEnd() + "…" : t;
    }

    private static JsonDocument Read(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CorruptDataException($"The Doom Wiki answered with something that is not its usual list: {ex.Message}");
        }
    }

    [GeneratedRegex(@"\{\{idgames\|([^}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex IdgamesLink();

    [GeneratedRegex(@"tex|resource|\bres\b|retres", RegexOptions.IgnoreCase)]
    private static partial Regex TexturesWords();

    [GeneratedRegex(@"\{\{DISPLAYTITLE:([^}]+)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayTitle();

}
