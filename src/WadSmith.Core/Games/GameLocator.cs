using System.IO.Compression;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>
/// A base game or add-on file found on this computer. <paramref name="Iwad"/> says whether its header reads IWAD (a copy
/// reading PWAD, the 2024 re-release's, is one GZDoom refuses as the base game). <paramref name="Inside"/> names the
/// entry when the file sits inside an archive at <paramref name="Path"/>: such a copy is not usable until it is taken out
/// (<see cref="GameLocator.Extract"/>).
/// </summary>
public sealed record FoundGame(GameDefinition Game, string Path, long Size, bool Iwad = true, string? Inside = null)
{
    /// <summary>Whether the engine can be handed this file as it is.</summary>
    public bool Usable => Inside is null;
}

/// <summary>A file with maps in it that is not a base game (a map pack): a WAD, or a WAD inside an archive (<paramref name="Inside"/> names it).</summary>
public sealed record MapFile(string Path, string? Inside, IReadOnlyList<string> Maps)
{
    public string Name => Inside is null ? System.IO.Path.GetFileName(Path) : $"{System.IO.Path.GetFileName(Path)} › {Inside}";
}

/// <summary>What one folder held: how many WADs and archives were looked at, and the games and map files among them.</summary>
public sealed record FolderReport(string Folder, int Wads, int Archives, IReadOnlyList<FoundGame> Games, IReadOnlyList<MapFile> MapFiles)
{
    public bool Empty => Wads == 0 && Archives == 0;
}

/// <summary>
/// A folder to look in, how many levels down, whether the user added it (then the folders around it are looked in too),
/// and whether archives in it are read (not inside a Steam or GOG library, where every installed game keeps its own ZIPs).
/// </summary>
public sealed record ScanTarget(string Folder, int Depth, bool Added = false, bool ReadArchives = true);

/// <summary>Everything a scan found, folder by folder and as one list of games (each file once, the usable copies first).</summary>
public sealed record ScanReport(IReadOnlyList<FolderReport> Folders, IReadOnlyList<FoundGame> Games)
{
    public IEnumerable<MapFile> MapFiles => Folders.SelectMany(f => f.MapFiles);
}

/// <summary>
/// Finds the base games installed on this computer, judging every candidate by its lumps: Steam and
/// GOG (Heroic, Lutris, Bottles) installs, the folders Linux packages and source ports use, GZDoom's
/// own folders and the ones its settings search, and the folders the user added — looking into ZIP,
/// PK3 and IPK3 archives as well, and remembering what each held.
/// </summary>
public static partial class GameLocator
{
    /// <summary>How many levels down a folder is searched.</summary>
    public const int DefaultDepth = 4;

    /// <summary>A WAD inside an archive bigger than this is not read.</summary>
    private const long LargestInnerWad = 512L * 1024 * 1024;

    /// <summary>The folders worth looking in on this computer, existing ones only.</summary>
    public static IReadOnlyList<string> StandardFolders(Func<string, string?> environment) => StandardTargets(environment).Select(t => t.Folder).ToList();

    /// <summary>
    /// The folders worth looking in on this computer, existing ones only, each with whether its archives are read: the
    /// game libraries (Steam, Heroic, Bottles) are not, every installed game there keeping its own ZIPs.
    /// </summary>
    public static IReadOnlyList<ScanTarget> StandardTargets(Func<string, string?> environment)
    {
        var home = environment("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var folders = new List<string>
        {
            "/usr/share/games/doom", "/usr/share/doom", "/usr/local/share/games/doom", "/usr/local/share/doom",
            "/usr/share/games/freedoom", "/usr/share/freedoom",
            Path.Combine(home, ".local/share/games/doom"),
            Path.Combine(home, ".config/gzdoom"),
            Path.Combine(home, ".var/app/org.zdoom.GZDoom/.config/gzdoom"),
            Path.Combine(home, ".config/uzdoom"),
        };
        if (environment("DOOMWADDIR") is { Length: > 0 } waddir) folders.Add(waddir);
        if (environment("DOOMWADPATH") is { Length: > 0 } wadpath) folders.AddRange(wadpath.Split(':', StringSplitOptions.RemoveEmptyEntries));
        folders.AddRange(EngineSearchFolders(home, environment));

        var libraries = new List<string> { Path.Combine(home, "Games/Heroic"), Path.Combine(home, ".local/share/bottles/bottles") };
        // Steam: every library folder's installed games.
        foreach (var steam in new[] { ".local/share/Steam", ".steam/steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            var vdf = Path.Combine(home, steam, "steamapps/libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match m in SteamLibraryPath().Matches(File.ReadAllText(vdf)))
                {
                    libraries.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal), "steamapps/common"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<ScanTarget>();
        foreach (var f in folders.Where(Directory.Exists)) if (seen.Add(f)) targets.Add(new ScanTarget(f, DefaultDepth));
        foreach (var f in libraries.Where(Directory.Exists)) if (seen.Add(f)) targets.Add(new ScanTarget(f, DefaultDepth, ReadArchives: false));
        return targets;
    }

    /// <summary>
    /// The folders GZDoom and UZDoom were told to search for games (their settings' IWADSearch and FileSearch
    /// directories): a player who already runs the engine has told it where the WADs are.
    /// </summary>
    public static IEnumerable<string> EngineSearchFolders(string home, Func<string, string?> environment)
    {
        var inis = new[]
        {
            Path.Combine(home, ".config/gzdoom/gzdoom.ini"),
            Path.Combine(home, ".var/app/org.zdoom.GZDoom/.config/gzdoom/gzdoom.ini"),
            Path.Combine(home, ".config/uzdoom/uzdoom.ini"),
        };
        foreach (var ini in inis)
        {
            string[] lines;
            try
            {
                if (!File.Exists(ini)) continue;
                lines = File.ReadAllLines(ini);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var inSearch = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSearch = line.Equals("[IWADSearch.Directories]", StringComparison.OrdinalIgnoreCase) || line.Equals("[FileSearch.Directories]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSearch || !line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[5..].Trim();
                if (value is "." or "$PROGDIR" or "") continue;
                value = Expand(Expand(value, "$HOME", home), "$DOOMWADDIR", environment("DOOMWADDIR"));
                if (value.Contains('$') || !Path.IsPathRooted(value)) continue;
                yield return value;
            }
        }
    }

    /// <summary>
    /// An ini entry beginning with <paramref name="variable"/> ("$HOME/mywads") as a path under <paramref name="with"/>, written the way
    /// this system writes paths; the entry as it was when it does not begin so, or the variable has no value.
    /// </summary>
    private static string Expand(string value, string variable, string? with)
    {
        if (with is null || !value.StartsWith(variable, StringComparison.Ordinal)) return value;
        var rest = value[variable.Length..].TrimStart('/', '\\');
        return rest.Length == 0 ? with : Path.Combine(with, rest.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Where to look for a folder the user added: the folder itself, <paramref name="depth"/> levels down; the folder
    /// above it, two levels down (the WADs are often beside the folder a player picks, as in a port's own folder); and
    /// the one above that, its own files only. Never the root of the file system.
    /// </summary>
    public static IReadOnlyList<ScanTarget> Around(string folder, int depth = DefaultDepth)
    {
        var targets = new List<ScanTarget> { new(folder, depth, Added: true) };
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(full) is { } parent && !IsRoot(parent))
        {
            targets.Add(new(parent, 2));
            if (Path.GetDirectoryName(parent) is { } above && !IsRoot(above)) targets.Add(new(above, 1));
        }

        return targets;
    }

    private static bool IsRoot(string path) => string.Equals(Path.GetPathRoot(path), path, StringComparison.Ordinal) || path == "/";

    /// <summary>
    /// Every base game in the folders (searched <paramref name="depth"/> levels down), each file once. Files are judged
    /// by content, so a renamed or repackaged copy is still found.
    /// </summary>
    public static IReadOnlyList<FoundGame> Scan(IEnumerable<string> folders, int depth = DefaultDepth, CancellationToken cancel = default) =>
        Report(folders.Select(f => new ScanTarget(f, depth)), cancel: cancel).Games;

    /// <summary>
    /// Looks in every target (and around the ones the user added) and reports what each folder held. Archives are read
    /// where the target allows, their contents remembered in <paramref name="cache"/> when one is given so an unchanged
    /// archive is not opened again; <paramref name="progress"/> hears which archive is being read. Nothing is written:
    /// a base game inside an archive is reported with <see cref="FoundGame.Inside"/> set, and taken out only when used.
    /// </summary>
    public static ScanReport Report(IEnumerable<ScanTarget> targets, ArchiveCache? cache = null, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var folders = new List<FolderReport>();
        var games = new List<FoundGame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var expanded = targets.SelectMany(t => t.Added ? Around(t.Folder, t.Depth) : [t]).GroupBy(t => Path.GetFullPath(t.Folder), StringComparer.Ordinal).Select(g => g.OrderByDescending(t => t.Depth).First());
        foreach (var target in expanded)
        {
            if (!Directory.Exists(target.Folder)) continue;
            var (wads, archives) = (0, 0);
            var here = new List<FoundGame>();
            var maps = new List<MapFile>();
            foreach (var file in Candidates(target.Folder, target.Depth, target.ReadArchives, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                string real;
                try
                {
                    real = Path.GetFullPath(new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file);
                }
                catch (IOException)
                {
                    real = Path.GetFullPath(file);
                }

                if (!seen.Add(real)) continue;
                if (IsArchive(real))
                {
                    archives++;
                    LookInside(real, cache, progress, here, maps, cancel);
                    continue;
                }

                // A Build group file (Duke Nukem 3D, D48): its art and sounds can be borrowed; GZDoom does not play it.
                if (real.EndsWith(".grp", StringComparison.OrdinalIgnoreCase))
                {
                    archives++;
                    if (Build.GrpArchive.IsGrp(real) && GameCatalog.Get("duke3d") is { } duke) here.Add(new FoundGame(duke, real, new FileInfo(real).Length, Iwad: false));
                    continue;
                }

                // Quake's first PAK file (Phase 8b.6): its textures can be borrowed; GZDoom does not play it. Only PAK0, with the
                // palette and Quake's first level, stands for the game (its other PAK files are read with it).
                if (real.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                {
                    archives++;
                    if (Path.GetFileName(real).Equals("pak0.pak", StringComparison.OrdinalIgnoreCase) && Quake.PakArchive.IsPak(real) && GameCatalog.Get("quake") is { } quake)
                    {
                        try
                        {
                            var pak = Quake.PakArchive.Open(real);
                            if (quake.MustContain.All(n => pak.Names.Contains(n, StringComparer.OrdinalIgnoreCase))) here.Add(new FoundGame(quake, real, new FileInfo(real).Length, Iwad: false));
                        }
                        catch (CorruptDataException)
                        {
                        }
                    }

                    continue;
                }

                wads++;
                var names = WadDirectory.ReadNames(real);
                if (names is null) continue;
                if (GameCatalog.Identify(names) is { } game) here.Add(new FoundGame(game, real, new FileInfo(real).Length, WadDirectory.IsIwad(real)));
                else if (MapsIn(names) is { Count: > 0 } found) maps.Add(new MapFile(real, null, found));
            }

            folders.Add(new FolderReport(target.Folder, wads, archives, here, maps));
            games.AddRange(here);
        }

        // The first copy of a game is the one used: a usable file before one inside an archive, a copy whose header says IWAD before one saying PWAD, otherwise in the order found.
        return new ScanReport(folders, [.. games.OrderBy(g => g.Usable ? 0 : 1).ThenBy(g => g.Iwad ? 0 : 1)]);
    }

    /// <summary>
    /// The copies the engine can be handed: every usable one, and, for a game found only inside archives, its first copy
    /// taken out into <paramref name="extractTo"/>. <paramref name="failed"/> hears a copy that could not be taken out.
    /// </summary>
    public static IReadOnlyList<FoundGame> Usable(IReadOnlyList<FoundGame> games, string extractTo, Action<FoundGame, Exception>? failed = null)
    {
        var usable = games.Where(g => g.Usable).ToList();
        var have = new HashSet<string>(usable.Select(g => g.Game.Id), StringComparer.Ordinal);
        foreach (var group in games.Where(g => !g.Usable).GroupBy(g => g.Game.Id))
        {
            if (have.Contains(group.Key)) continue;
            foreach (var copy in group)
            {
                try
                {
                    usable.Add(Extract(copy, extractTo));
                    have.Add(group.Key);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    failed?.Invoke(copy, ex);
                }
            }
        }

        return usable;
    }

    /// <summary>
    /// Takes a copy found inside an archive out into <paramref name="extractTo"/> (its own subfolder, named after the
    /// archive) and returns it as a usable file; a copy of the same size already there is kept. A usable copy comes back as it is.
    /// </summary>
    public static FoundGame Extract(FoundGame found, string extractTo)
    {
        if (found.Inside is null) return found;
        var dest = Path.Combine(extractTo, Path.GetFileNameWithoutExtension(found.Path), Path.GetFileName(found.Inside.Replace('\\', '/')));
        if (!(File.Exists(dest) && new FileInfo(dest).Length == found.Size))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var zip = ZipFile.OpenRead(found.Path);
            var entry = zip.GetEntry(found.Inside) ?? throw new FileNotFoundException($"{found.Inside} is no longer in {found.Path}.", found.Path);
            entry.ExtractToFile(dest, overwrite: true);
        }

        return found with { Path = dest, Inside = null };
    }

    /// <summary>The files the game <paramref name="gameId"/> plays with, in load order (its base game first for an add-on); empty when it was not found.</summary>
    public static IReadOnlyList<string> FilesFor(IReadOnlyList<FoundGame> games, string gameId)
    {
        var found = games.FirstOrDefault(g => g.Game.Id == gameId);
        if (found is null) return [];
        var files = new List<string>();
        if (found.Game.AddOnFor is { } baseId && games.FirstOrDefault(g => g.Game.Id == baseId) is { } baseGame) files.Add(baseGame.Path);
        files.Add(found.Path);
        return files;
    }

    /// <summary>The maps a WAD's lump names announce: a name followed by THINGS and LINEDEFS (classic) or by TEXTMAP (UDMF).</summary>
    public static IReadOnlyList<string> MapsIn(IReadOnlyList<string> names)
    {
        var maps = new List<string>();
        for (var i = 0; i + 1 < names.Count; i++)
        {
            if (names[i].Length == 0) continue;
            var classic = names[i + 1] == "THINGS" && i + 2 < names.Count && names[i + 2] == "LINEDEFS";
            if (classic || names[i + 1] == "TEXTMAP") maps.Add(names[i]);
        }

        return maps;
    }

    private static bool IsArchive(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ipk3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An archive: a base game laid out as a PK3 (judged by its entry names) is the archive itself; a WAD inside is read and
    /// judged like a loose one and reported as sitting inside. What it held is remembered in the cache.
    /// </summary>
    private static void LookInside(string archive, ArchiveCache? cache, IProgress<string>? progress, List<FoundGame> games, List<MapFile> maps, CancellationToken cancel)
    {
        try
        {
            var info = new FileInfo(archive);
            if (cache?.TryGet(archive, info.Length, info.LastWriteTimeUtc, out var remembered) == true && Recall(archive, info.Length, remembered, games, maps)) return;

            progress?.Report($"Looking inside {Path.GetFileName(archive)}…");
            var record = new ArchiveRecord { Length = info.Length, Written = info.LastWriteTimeUtc };
            using var zip = ZipFile.OpenRead(archive);
            var entryNames = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => PathNamespaces.Classify(e.FullName.Replace('\\', '/')).Name).ToList(); // archive path
            if (GameCatalog.Identify(entryNames) is { } packed)
            {
                games.Add(new FoundGame(packed, archive, info.Length));
                cache?.Put(archive, record with { Packed = packed.Id });
                return;
            }

            foreach (var entry in zip.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                if (!entry.FullName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || entry.Length < WadContainer.HeaderSize || entry.Length > LargestInnerWad) continue;
                var data = new byte[entry.Length];
                using (var s = entry.Open()) s.ReadExactly(data);
                var names = WadDirectory.ReadNames(data);
                if (names is null) continue;
                if (GameCatalog.Identify(names) is { } game)
                {
                    var iwad = WadDirectory.IsIwad(data);
                    games.Add(new FoundGame(game, archive, data.Length, iwad, entry.FullName));
                    record.Games.Add(new CachedGame { Id = game.Id, Inside = entry.FullName, Size = data.Length, Iwad = iwad });
                }
                else if (MapsIn(names) is { Count: > 0 } found)
                {
                    maps.Add(new MapFile(archive, entry.FullName, found));
                    record.MapFiles.Add(new CachedMapFile { Inside = entry.FullName, Maps = [.. found] });
                }
            }

            cache?.Put(archive, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            // Not an archive this reads, or one that cannot be read here: it still counts as one looked at.
        }
    }

    /// <summary>What the cache remembers of an archive, as found games and map files; false when a remembered game is no longer in the catalogue.</summary>
    private static bool Recall(string archive, long length, ArchiveRecord record, List<FoundGame> games, List<MapFile> maps)
    {
        if (record.Packed is { } packedId)
        {
            if (GameCatalog.Get(packedId) is not { } packed) return false;
            games.Add(new FoundGame(packed, archive, length));
            return true;
        }

        var found = new List<FoundGame>();
        foreach (var g in record.Games)
        {
            if (GameCatalog.Get(g.Id) is not { } game) return false;
            found.Add(new FoundGame(game, archive, g.Size, g.Iwad, g.Inside));
        }

        games.AddRange(found);
        maps.AddRange(record.MapFiles.Select(m => new MapFile(archive, m.Inside, m.Maps)));
        return true;
    }

    private static IEnumerable<string> Candidates(string folder, int depth, bool archives, CancellationToken cancel)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System };
        var queue = new Queue<(string Dir, int Depth)>([(folder, 0)]);
        while (queue.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (dir, d) = queue.Dequeue();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options).Where(f => f.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".iwad", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".grp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || (archives && IsArchive(f))).ToList();
                dirs = d < depth ? Directory.EnumerateDirectories(dir, "*", options).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files) yield return f;
            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "proc" or "sys" or "dev" or "shadercache" or "compatdata" or "downloading" or "temp") continue;
                queue.Enqueue((sub, d + 1));
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex SteamLibraryPath();
}
