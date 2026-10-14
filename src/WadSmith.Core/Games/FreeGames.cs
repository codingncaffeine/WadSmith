namespace WadSmith.Core.Games;

/// <summary>
/// The free games WadSmith can fetch for the user (§3.2, D61): Freedoom (Doom's free twin) and Blasphemer (Heretic's), each
/// one release pinned by its checksum, unpacked into a folder of WadSmith's own games. Once there they are games like any
/// other: the shelves lend their textures and objects to any map.
/// </summary>
public static class FreeGames
{
    /// <summary>One free game's release: where it is, its checksum, the game files taken from it and the folder they go in.</summary>
    public sealed record Release(string Title, string Version, Uri Url, string Sha256, IReadOnlyList<string> Files, string Folder, long Size, string Licence, string What);

    public static readonly Release Freedoom = new("Freedoom", Games.Freedoom.Version, Games.Freedoom.Url, Games.Freedoom.Sha256, Games.Freedoom.Files, "freedoom", 28_000_000,
        "BSD licence: free to use, change and share, credit kept", "Doom's free twin: its own textures, monsters, weapons and items, drawn by the community to stand in for Doom's");

    public static readonly Release Blasphemer = new("Blasphemer", "0.1.8", new("https://github.com/Blasphemer/blasphemer/releases/download/v0.1.8/blasphem-0.1.8.zip"),
        "5838d211add81e1696a621485c1e40771035daf6609d08bad7018f52acfa5e54", ["blasphem.wad"], "blasphemer", 7_601_799,
        "BSD licence: free to use, change and share, credit kept", "Heretic's free twin: its own textures, monsters and items in Heretic's style");

    public static readonly IReadOnlyList<Release> All = [Freedoom, Blasphemer];

    /// <summary>
    /// Fetches <paramref name="release"/> into <paramref name="folder"/> (a part file beside the games, gone when done), checks it
    /// and unpacks its game files. <paramref name="progress"/> gets the bytes so far and the total when the server says it.
    /// </summary>
    public static async Task InstallAsync(Release release, string folder, HttpClient http, IProgress<(long Done, long? Total)>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, release.Folder + ".zip.part");
        try
        {
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using (from.ConfigureAwait(false))
                {
                    var to = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using (to.ConfigureAwait(false))
                    {
                        var buffer = new byte[1 << 16];
                        long done = 0;
                        int n;
                        while ((n = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                        {
                            await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                            done += n;
                            progress?.Report((done, total));
                        }
                    }
                }
            }

            Games.Freedoom.Unpack(zip, folder, release.Sha256, release.Files, $"{release.Title} {release.Version}");
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
        }
    }
}
