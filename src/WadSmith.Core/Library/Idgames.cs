using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WadSmith.Core.Library;

/// <summary>
/// The idgames archive, the community's archive of Doom engine files, through Doomworld's public API (D61): a file by its
/// number or path, a search, and the file's readme — whose "Copyright / Permissions" section says what the author allows.
/// The files themselves come from the archive's mirrors, tried in turn.
/// </summary>
public static partial class Idgames
{
    public const string Api = "https://www.doomworld.com/idgames/api/api.php";

    /// <summary>Mirrors of the archive that serve over HTTPS, tried in this order.</summary>
    public static readonly IReadOnlyList<string> Mirrors =
    [
        "https://mirrors.lug.mtu.edu/idgames/",
        "https://youfailit.net/pub/idgames/",
        "https://ftpmirror1.infania.net/pub/idgames/",
    ];

    /// <summary>One file of the archive as the API describes it.</summary>
    public sealed record ArchiveFile(int Id, string Title, string Dir, string FileName, long Size, string Author, string Description, string Credits, string TextFile)
    {
        /// <summary>Its path in the archive ("graphics/otex_1_1.zip").</summary>
        public string Path => Dir + FileName; // archive path

        /// <summary>Its page on Doomworld.</summary>
        public Uri Page => new($"https://www.doomworld.com/idgames/{Dir}{System.IO.Path.GetFileNameWithoutExtension(FileName)}");

        /// <summary>Where it can be downloaded, the first mirror first.</summary>
        public IReadOnlyList<Uri> Downloads => [.. Mirrors.Select(m => new Uri(m + Path))];
    }

    public static Uri GetUrl(int id) => new(string.Create(CultureInfo.InvariantCulture, $"{Api}?action=get&id={id}&out=json"));

    public static Uri GetUrl(string path) => new($"{Api}?action=get&file={Uri.EscapeDataString(path)}&out=json");

    public static Uri SearchUrl(string query, string type) => new($"{Api}?action=search&query={Uri.EscapeDataString(query)}&type={type}&sort=rating&dir=desc&out=json");

    /// <summary>The file the API answer <paramref name="json"/> describes (a get), or null when it describes none.</summary>
    public static ArchiveFile? ParseFile(string json)
    {
        using var doc = Read(json);
        return doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("id", out _) ? File(content) : null;
    }

    /// <summary>The files of a search answer: none, one (the API then sends an object) or many.</summary>
    public static IReadOnlyList<ArchiveFile> ParseSearch(string json)
    {
        using var doc = Read(json);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("file", out var files)) return [];
        return files.ValueKind switch
        {
            JsonValueKind.Array => [.. files.EnumerateArray().Select(File)],
            JsonValueKind.Object => [File(files)],
            _ => [],
        };
    }

    public static async Task<ArchiveFile?> GetAsync(HttpClient http, int id, CancellationToken cancel) => ParseFile(await http.GetStringAsync(GetUrl(id), cancel).ConfigureAwait(false));

    public static async Task<ArchiveFile?> GetAsync(HttpClient http, string path, CancellationToken cancel) => ParseFile(await http.GetStringAsync(GetUrl(path), cancel).ConfigureAwait(false));

    /// <summary>Files whose title or file name has <paramref name="query"/>, best rated first, each once.</summary>
    public static async Task<IReadOnlyList<ArchiveFile>> SearchAsync(HttpClient http, string query, CancellationToken cancel)
    {
        var found = new List<ArchiveFile>();
        foreach (var type in new[] { "title", "filename" })
        {
            found.AddRange(ParseSearch(await http.GetStringAsync(SearchUrl(query, type), cancel).ConfigureAwait(false)));
        }

        return [.. found.DistinctBy(f => f.Id)];
    }

    /// <summary>
    /// What a readme says the author allows: its "Copyright / Permissions" section (the archive's template), else a paragraph
    /// that speaks of permission; "" when it says nothing.
    /// </summary>
    public static string Permissions(string textFile)
    {
        var lines = textFile.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!PermissionsHeading().IsMatch(lines[i])) continue;
            var said = new List<string>();
            for (var j = i + 1; j < lines.Length && said.Count < 14; j++)
            {
                var line = lines[j].Trim();
                if (line.StartsWith('*') && line.EndsWith('*') && line.Length > 2) break;
                if (line.StartsWith("===", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) break;
                if (line.Length == 0 && said.Count > 0 && said[^1].Length == 0) continue;
                said.Add(line);
            }

            var text = Collapse(string.Join('\n', said));
            if (text.Length > 0) return text;
        }

        foreach (var paragraph in textFile.Replace("\r", "", StringComparison.Ordinal).Split("\n\n"))
        {
            if (Allows().IsMatch(paragraph)) return Collapse(paragraph);
        }

        return "";
    }

    private static string Collapse(string text)
    {
        var words = Spaces().Replace(text, " ").Trim();
        return words.Length > 700 ? words[..700].TrimEnd() + "…" : words;
    }

    private static JsonDocument Read(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CorruptDataException($"The idgames archive answered with something that is not its usual list: {ex.Message}");
        }
    }

    private static ArchiveFile File(JsonElement e) => new(
        Int(e, "id"), Str(e, "title"), Str(e, "dir"), Str(e, "filename"), Long(e, "size"), Str(e, "author"), Str(e, "description"), Str(e, "credits"), Str(e, "textfile"));

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    [GeneratedRegex(@"^\s*\*?\s*(Copyright|Permissions)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionsHeading();

    [GeneratedRegex(@"\b(may (be )?(use|used|distribute|modif)|permission|free(ly)? to use|base for modification|public domain|creative commons)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Allows();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
