using System.IO.Compression;
using System.Text.Json;

namespace WadSmith.Core.Library;

/// <summary>
/// What WadSmith keeps of the free sources (D61), under one folder: the lists (kept 7 days, then asked again; an old list is
/// used when the site cannot be reached), the preview pictures and the downloaded files (kept for good). Downloads are
/// unpacked to the WAD or PK3 files inside, ready to be read. One request at a time per store, so a site is never crowded.
/// </summary>
public sealed class LibraryStore(string folder, HttpClient? http)
{
    /// <summary>How long a list is kept before it is asked for again.</summary>
    public static readonly TimeSpan ListAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly SemaphoreSlim _one = new(1, 1);

    public string Folder { get; } = folder;

    /// <summary>False when nothing may be fetched (WADSMITH_OFFLINE): only what is kept is shown.</summary>
    public bool Online => http is not null;

    /// <summary>The entries of one Realm667 category: the kept list while it is fresh, else fetched (an old list when that fails).</summary>
    public Task<IReadOnlyList<LibraryItem>> Realm667Async(Realm667.Category category, CancellationToken cancel) =>
        ListAsync(Path.Combine("realm667", category.Path.Replace('/', '_') + ".json"), async h => await Realm667.FetchAsync(h, category, null, cancel).ConfigureAwait(false), cancel);

    /// <summary>
    /// The texture packs to offer: the Doom Wiki's list, each looked up in the idgames archive for its file, size, author and
    /// readme permissions (the built-in list when the wiki cannot be read).
    /// </summary>
    public Task<IReadOnlyList<LibraryItem>> TexturePacksAsync(CancellationToken cancel) =>
        ListAsync(Path.Combine("idgames", "packs.json"), async h =>
        {
            IReadOnlyList<DoomWiki.WikiPack> wiki;
            try
            {
                wiki = await DoomWiki.FetchAsync(h, cancel).ConfigureAwait(false);
                if (wiki.Count == 0) wiki = DoomWiki.Known;
            }
            catch (Exception ex) when (ex is HttpRequestException or CorruptDataException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                wiki = DoomWiki.Known;
            }

            var items = new List<LibraryItem>();
            foreach (var pack in wiki)
            {
                var file = pack.IdgamesId is { } id ? await Idgames.GetAsync(h, id, cancel).ConfigureAwait(false) : await Idgames.GetAsync(h, pack.IdgamesPath!, cancel).ConfigureAwait(false);
                if (file is null) continue;
                items.Add(PackItem(file, pack));
            }

            return items;
        }, cancel);

    /// <summary>Texture packs and other resources in the idgames archive whose title or file name has <paramref name="words"/> (not kept).</summary>
    public async Task<IReadOnlyList<LibraryItem>> SearchIdgamesAsync(string words, CancellationToken cancel)
    {
        if (http is null) return [];
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var files = await Idgames.SearchAsync(http, words, cancel).ConfigureAwait(false);
            return [.. files.Where(f => f.Dir.StartsWith("graphics/", StringComparison.Ordinal) || f.Dir.StartsWith("combos/", StringComparison.Ordinal) || f.Dir.StartsWith("themes/", StringComparison.Ordinal)).Take(40).Select(f => PackItem(f, null))]; // archive path
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>An archive file as a pack to offer, with what its wiki page says when it has one.</summary>
    public static LibraryItem PackItem(Idgames.ArchiveFile file, DoomWiki.WikiPack? wiki)
    {
        var permissions = Idgames.Permissions(file.TextFile);
        if (permissions.Length == 0 && wiki is { Permissions.Length: > 0 }) permissions = wiki.Permissions;
        var title = wiki?.Title ?? (file.Title.Length > 0 ? file.Title : Path.GetFileNameWithoutExtension(file.FileName));
        var credits = string.Join('\n', new[] { file.Author.Length > 0 ? "Author: " + file.Author : "", file.Credits.Length > 0 ? "Thanks: " + file.Credits : "" }.Where(l => l.Length > 0));
        var description = wiki is { Intro.Length: > 0 } ? wiki.Intro : file.Description;
        return new LibraryItem("idgames", "idgames-" + file.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), title, LibraryKind.TexturePack, "Texture packs", "Doom", "doom",
            null, file.Downloads[0], wiki?.Page ?? file.Page, credits, description, $"File: {file.Path}", file.Size > 0 ? file.Size : null, permissions.Length > 0 ? permissions : null);
    }

    /// <summary>The entry's preview picture: kept from the first time, else fetched; null when it has none or it cannot be had.</summary>
    public async Task<byte[]?> PreviewAsync(LibraryItem item, CancellationToken cancel)
    {
        if (item.Preview is not { } url) return null;
        var path = Path.Combine(Folder, "previews", item.Id + ExtensionOf(url, ".png"));
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false);
        if (http is null) return null;
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var data = await http.GetByteArrayAsync(url, cancel).ConfigureAwait(false);
            Write(path, data);
            return data;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The WAD and PK3 files of an entry fetched before, or null when it has not been.</summary>
    public IReadOnlyList<string>? Fetched(LibraryItem item)
    {
        var dir = FilesOf(item);
        var list = Path.Combine(dir, "files.txt");
        if (!File.Exists(list)) return null;
        var files = File.ReadAllLines(list).Where(l => l.Length > 0).Select(l => Path.Combine(dir, l)).ToList();
        return files.Count > 0 && files.All(File.Exists) ? files : null;
    }

    /// <summary>
    /// The entry's WAD and PK3 files: kept from the first time, else downloaded (a mirror after another for the idgames
    /// archive), unpacked and kept with the entry's description. <paramref name="progress"/> gets the bytes so far and the total.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchAsync(LibraryItem item, IProgress<(long Done, long? Total)>? progress, CancellationToken cancel)
    {
        if (Fetched(item) is { } kept) return kept;
        if (http is null) throw new IOException($"{item.Title} has not been downloaded, and WadSmith is set not to use the internet.");
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (Fetched(item) is { } raced) return raced;
            var dir = FilesOf(item);
            Directory.CreateDirectory(dir);
            var part = Path.Combine(dir, "download.part");
            var urls = item.Source == "idgames" && item.Info.StartsWith("File: ", StringComparison.Ordinal)
                ? Idgames.Mirrors.Select(m => new Uri(m + item.Info["File: ".Length..])).ToList()
                : [item.Download];
            Exception? last = null;
            foreach (var url in urls)
            {
                try
                {
                    await DownloadAsync(url, part, progress, cancel).ConfigureAwait(false);
                    last = null;
                    break;
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                }
            }

            if (last is not null) throw new IOException($"{item.Title} could not be downloaded: {last.Message}", last);
            try
            {
                var files = Unpack(part, dir, item);
                File.WriteAllText(Path.Combine(dir, "item.json"), JsonSerializer.Serialize(item, Json));
                File.WriteAllLines(Path.Combine(dir, "files.txt"), files.Select(Path.GetFileName).OfType<string>());
                return files;
            }
            finally
            {
                File.Delete(part);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The texture packs fetched before, each with its files, by title.</summary>
    public IReadOnlyList<(LibraryItem Item, IReadOnlyList<string> Files)> InstalledPacks()
    {
        var found = new List<(LibraryItem, IReadOnlyList<string>)>();
        foreach (var source in new[] { "idgames", "realm667" })
        {
            var root = Path.Combine(Folder, source, "files");
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var meta = Path.Combine(dir, "item.json");
                if (!File.Exists(meta)) continue;
                LibraryItem? item;
                try
                {
                    item = JsonSerializer.Deserialize<LibraryItem>(File.ReadAllText(meta), Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (item is { Kind: LibraryKind.TexturePack } && Fetched(item) is { } files) found.Add((item, files));
            }
        }

        return [.. found.OrderBy(p => p.Item1.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Where an entry's files are kept.</summary>
    public string FilesOf(LibraryItem item) => Path.Combine(Folder, item.Source, "files", item.Id);

    private async Task<IReadOnlyList<LibraryItem>> ListAsync(string relative, Func<HttpClient, Task<IReadOnlyList<LibraryItem>>> fetch, CancellationToken cancel)
    {
        var path = Path.Combine(Folder, relative);
        var kept = Read(path);
        if (kept is not null && (http is null || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ListAge)) return kept;
        if (http is null) return [];
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var fresh = await fetch(http).ConfigureAwait(false);
            Write(path, JsonSerializer.SerializeToUtf8Bytes(fresh, Json));
            return fresh;
        }
        catch (Exception ex) when (kept is not null && (ex is HttpRequestException or CorruptDataException or TaskCanceledException) && !cancel.IsCancellationRequested)
        {
            return kept;
        }
        finally
        {
            _one.Release();
        }
    }

    private static IReadOnlyList<LibraryItem>? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<LibraryItem>>(File.ReadAllBytes(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        File.WriteAllBytes(part, data);
        File.Move(part, path, overwrite: true);
    }

    private async Task DownloadAsync(Uri url, string part, IProgress<(long Done, long? Total)>? progress, CancellationToken cancel)
    {
        using var response = await http!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using (from.ConfigureAwait(false))
        {
            var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
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

    /// <summary>The biggest file kept from a download (a zip bomb stops here).</summary>
    private const long MostBytes = 512L << 20;

    /// <summary>
    /// The WAD and PK3 files a download holds, written beside it: the WADs and PK3s in the zip (in a zip inside it too); for a
    /// texture pack the ones named for textures first, when some are; the zip itself when it is laid out as a PK3; a lone WAD as
    /// it is. Names are the files' own names only, so nothing is written outside <paramref name="dir"/>.
    /// </summary>
    internal static IReadOnlyList<string> Unpack(string download, string dir, LibraryItem item)
    {
        Span<byte> magic = stackalloc byte[4];
        using (var f = File.OpenRead(download))
        {
            if (f.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4) throw new CorruptDataException($"The download of {item.Title} is empty.");
        }

        if (magic.SequenceEqual("PWAD"u8) || magic.SequenceEqual("IWAD"u8))
        {
            var wad = Path.Combine(dir, item.Id + ".wad");
            File.Copy(download, wad, overwrite: true);
            return [wad];
        }

        if (!(magic[0] == 'P' && magic[1] == 'K')) throw new CorruptDataException($"The download of {item.Title} is neither a zip nor a WAD (WadSmith reads no 7z or rar).");

        using var zip = ZipFile.OpenRead(download);
        var found = new List<(string Name, Func<Stream> Open, long Size)>();
        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is ".wad" or ".pk3" or ".pk7" or ".zip" or ".ipk3") found.Add((entry.Name, entry.Open, entry.Length));
        }

        // A zip laid out as a PK3 (its own DECORATE, sprites folder…) is the resource itself.
        var laidOut = zip.Entries.Any(e => e.FullName.Contains('/', StringComparison.Ordinal) && ResourceDirs.Contains(e.FullName[..e.FullName.IndexOf('/', StringComparison.Ordinal)].ToLowerInvariant())) // archive path
            || zip.Entries.Any(e => !e.FullName.Contains('/', StringComparison.Ordinal) && RootLumps.Contains(Path.GetFileNameWithoutExtension(e.Name).ToUpperInvariant()));
        if (found.Count == 0 || (laidOut && !found.Any(f => !f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))))
        {
            if (!laidOut) throw new CorruptDataException($"The download of {item.Title} holds no WAD or PK3.");
            var pk3 = Path.Combine(dir, item.Id + ".pk3");
            File.Copy(download, pk3, overwrite: true);
            return [pk3];
        }

        var written = new List<string>();
        foreach (var (name, open, size) in Prefer(found, item))
        {
            if (size > MostBytes) throw new CorruptDataException($"{name} in the download of {item.Title} is bigger than WadSmith takes ({size >> 20} MB).");
            var target = Path.Combine(dir, Path.GetFileName(name));
            using (var from = open())
            using (var to = File.Create(target + ".part"))
            {
                from.CopyTo(to);
            }

            File.Move(target + ".part", target, overwrite: true);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // A zip in the zip (a texture pack shipped beside its maps): its own WADs and PK3s, once.
                var inner = Path.Combine(dir, Path.GetFileNameWithoutExtension(name));
                Directory.CreateDirectory(inner);
                try
                {
                    var nested = Unpack(target, inner, item with { Id = Path.GetFileNameWithoutExtension(name) });
                    foreach (var n in nested)
                    {
                        var moved = Path.Combine(dir, Path.GetFileName(n));
                        File.Move(n, moved, overwrite: true);
                        written.Add(moved);
                    }
                }
                catch (CorruptDataException)
                {
                    // A zip of something else (screenshots, sources) is not a resource.
                }
                finally
                {
                    File.Delete(target);
                    Directory.Delete(inner, recursive: true);
                }

                continue;
            }

            written.Add(target);
        }

        if (written.Count == 0) throw new CorruptDataException($"The download of {item.Title} holds no WAD or PK3.");
        return written;
    }

    /// <summary>For a texture pack, the files named for textures or resources when there are any (its maps are not wanted); else all.</summary>
    private static IEnumerable<(string Name, Func<Stream> Open, long Size)> Prefer(List<(string Name, Func<Stream> Open, long Size)> found, LibraryItem item)
    {
        if (item.Kind != LibraryKind.TexturePack) return found;
        var named = found.Where(f => f.Name.Contains("tex", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("res", StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count > 0 ? named : found;
    }

    private static readonly HashSet<string> ResourceDirs = ["sprites", "sounds", "textures", "patches", "flats", "graphics", "models", "voxels", "brightmaps", "actors", "zscript", "decorate", "music", "acs", "hires"];

    private static readonly HashSet<string> RootLumps = ["DECORATE", "ZSCRIPT", "SNDINFO", "GLDEFS", "TEXTURES", "ANIMDEFS", "KEYCONF", "MODELDEF", "LANGUAGE"];

    private static string ExtensionOf(Uri url, string otherwise)
    {
        var ext = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        return ext is ".png" or ".gif" or ".jpg" or ".jpeg" or ".webp" ? ext : otherwise;
    }
}
