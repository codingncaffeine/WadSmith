using System.Text.Json;

namespace WadSmith.Core.Games;

/// <summary>
/// A game's cover picture for the start screen (D53): the lead picture of its Wikipedia article, fetched once through the
/// page-summary API and kept in a cache folder. The game's own title screen stands in until it arrives, and when the
/// article has none. Nothing is fetched for a game without a <see cref="GameDefinition.Wiki"/> title.
/// </summary>
public static class CoverArt
{
    public const string SummaryApi = "https://en.wikipedia.org/api/rest_v1/page/summary/";

    /// <summary>How long an article found to have no picture is left alone before it is looked at again.</summary>
    public static readonly TimeSpan RetryNone = TimeSpan.FromDays(30);

    /// <summary>The cached picture for <paramref name="game"/> when one has been fetched, else null.</summary>
    public static string? Cached(GameDefinition game, string cacheFolder)
    {
        if (game.Wiki is null || !Directory.Exists(cacheFolder)) return null;
        return Directory.EnumerateFiles(cacheFolder, Slug(game.Wiki) + ".*").FirstOrDefault(f => !f.EndsWith(".none", StringComparison.Ordinal));
    }

    /// <summary>
    /// The picture's file in <paramref name="cacheFolder"/>: the cached one, else fetched now through <paramref name="http"/>.
    /// Null when the game has no article, the article has no picture (remembered for <see cref="RetryNone"/>), or the fetch
    /// failed (tried again next time).
    /// </summary>
    public static async Task<string?> FetchAsync(GameDefinition game, string cacheFolder, HttpClient http, CancellationToken cancel)
    {
        if (game.Wiki is null) return null;
        if (Cached(game, cacheFolder) is { } have) return have;
        var slug = Slug(game.Wiki);
        var none = Path.Combine(cacheFolder, slug + ".none");
        if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < RetryNone) return null;
        try
        {
            using var summary = await http.GetAsync(SummaryApi + Uri.EscapeDataString(game.Wiki.Replace(' ', '_')), cancel).ConfigureAwait(false);
            if (!summary.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await summary.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            var source = doc.RootElement.TryGetProperty("thumbnail", out var thumbnail) && thumbnail.TryGetProperty("source", out var src) ? src.GetString() : null;
            Directory.CreateDirectory(cacheFolder);
            if (string.IsNullOrEmpty(source))
            {
                await File.WriteAllTextAsync(none, "", cancel).ConfigureAwait(false);
                return null;
            }

            using var picture = await http.GetAsync(source, cancel).ConfigureAwait(false);
            if (!picture.IsSuccessStatusCode) return null;
            var bytes = await picture.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            var extension = Path.GetExtension(new Uri(source).AbsolutePath) is { Length: > 1 and <= 5 } e ? e.ToLowerInvariant() : ".img";
            var file = Path.Combine(cacheFolder, slug + extension);
            await File.WriteAllBytesAsync(file, bytes, cancel).ConfigureAwait(false);
            return file;
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException or UriFormatException) && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A file name for an article title: its letters and digits, anything else an underscore.</summary>
    public static string Slug(string title) => string.Concat(title.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
}
