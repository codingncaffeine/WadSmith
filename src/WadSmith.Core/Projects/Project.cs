using System.Text.Json;
using System.Text.Json.Serialization;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Projects;

/// <summary>Where a map in a project came from.</summary>
public sealed record MapOrigin
{
    /// <summary>"new" for a map made in WadSmith, "generated" for one "Make me a map" made (its seed in <see cref="Map"/>), "remix" for one started from a base game's map, "import" for one brought in from another file.</summary>
    public required string Kind { get; init; }

    public string? Game { get; init; }

    public string? Map { get; init; }

    /// <summary>GZDoom's checksum of the original map, which its compatibility fixes are keyed by.</summary>
    public string? Checksum { get; init; }

    public string? File { get; init; }
}

public sealed record ProjectMap
{
    /// <summary>The engine's name for the map's slot: MAP01, E1M1 …</summary>
    public required string Slot { get; init; }

    /// <summary>The name players see (empty: the base game's name for the slot).</summary>
    public string Title { get; init; } = "";

    /// <summary>The music lump or file played (null: the base game's for the slot).</summary>
    public string? Music { get; init; }

    /// <summary>The sky texture (null: the base game's for the slot).</summary>
    public string? Sky { get; init; }

    /// <summary>The map the exit leads to (null: the base game's next map), or <see cref="ProjectMapInfo.EndToTitle"/>.</summary>
    public string? Next { get; init; }

    /// <summary>The map file, relative to the project folder.</summary>
    public required string File { get; init; }

    public MapOrigin? Origin { get; init; }
}

/// <summary>Something copied into the project from another game the user owns (D43), for the Share check to name.</summary>
public sealed record CopiedAsset
{
    /// <summary>"wall" or "flat".</summary>
    public required string Kind { get; init; }

    /// <summary>The game it came from (an id from data/games/games.json).</summary>
    public required string Game { get; init; }

    /// <summary>Its name in that game.</summary>
    public required string Original { get; init; }

    /// <summary>Its name in the project (the original's, unless this game already had one by that name).</summary>
    public required string Name { get; init; }

    /// <summary>The file, relative to the project folder.</summary>
    public required string File { get; init; }
}

/// <summary>
/// What a project plays in (D51): GZDoom's own format and actions, or a classic port's — the 1993 engine's format with
/// its limits (vanilla: Chocolate Doom, the DOS engine), or that format without the limits (limit-removing: Crispy Doom).
/// A classic target keeps the project's maps in the classic format, written back from the editor's own.
/// </summary>
public static class Targets
{
    public const string GZDoom = "gzdoom";
    public const string Vanilla = "vanilla";
    public const string LimitRemoving = "limit-removing";

    /// <summary>The targets as the user chooses them, in order.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> All =
    [
        (GZDoom, "GZDoom"),
        (Vanilla, "Vanilla Doom (Chocolate Doom)"),
        (LimitRemoving, "Limit-removing (Crispy Doom)"),
    ];

    /// <summary>Whether a target keeps the map in the classic format.</summary>
    public static bool IsClassic(string target) => target is Vanilla or LimitRemoving;

    public static string TitleOf(string target) => All.FirstOrDefault(t => t.Id == target).Title ?? target;

    public static bool IsKnown(string target) => All.Any(t => t.Id == target);
}

/// <summary>The project's own record, <c>wadsmith.json</c>: what GZDoom does not need to know but WadSmith does.</summary>
public sealed record ProjectInfo
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public required string Name { get; init; }

    /// <summary>The base game the project is for (an id from data/games/games.json).</summary>
    public required string Game { get; init; }

    /// <summary>What the project plays in (see <see cref="Targets"/>); GZDoom when not said.</summary>
    public string Target { get; init; } = Targets.GZDoom;

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    public List<ProjectMap> Maps { get; init; } = [];

    /// <summary>Add-on files the project needs loaded before itself, in order.</summary>
    /// <summary>Files loaded with the game, before the project, in play and in the editor: the map pack a map was started from.</summary>
    public List<string> AddOns { get; init; } = [];

    /// <summary>What was copied in from the user's other games.</summary>
    public List<CopiedAsset> Copied { get; init; } = [];
}

/// <summary>
/// A WadSmith project: a plain folder in GZDoom's PK3 layout (maps in <c>maps/</c>, textures in
/// <c>textures/</c> …) plus <c>wadsmith.json</c>. GZDoom plays the folder as it is; nothing in it is
/// in a WadSmith-only format. Every write goes to a temporary file first and replaces the old one
/// in a single step, so a crash never leaves a half-written map.
/// </summary>
public sealed class Project
{
    public const string SidecarName = "wadsmith.json";

    private Project(string folder, ProjectInfo info)
    {
        Folder = folder;
        Info = info;
    }

    public string Folder { get; }

    public ProjectInfo Info { get; private set; }

    public static Project Create(string folder, string name, string game, string target = Targets.GZDoom)
    {
        folder = Path.GetFullPath(folder);
        if (File.Exists(Path.Combine(folder, SidecarName))) throw new IOException($"{folder} already holds a WadSmith project.");
        if (!Targets.IsKnown(target)) throw new ArgumentException($"{target} is not a target WadSmith knows.", nameof(target));
        Directory.CreateDirectory(Path.Combine(folder, "maps"));
        var p = new Project(folder, new ProjectInfo { Name = name, Game = game, Target = target });
        p.SaveInfo();
        return p;
    }

    public static Project Open(string folder)
    {
        folder = Path.GetFullPath(folder);
        var path = Path.Combine(folder, SidecarName);
        if (!File.Exists(path)) throw new FileNotFoundException($"{folder} is not a WadSmith project (it has no {SidecarName}).", path);
        ProjectInfo info;
        try
        {
            info = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(path), Json) ?? throw new CorruptDataException($"{SidecarName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new CorruptDataException($"{SidecarName} in {folder} cannot be read: {ex.Message}", ex);
        }

        if (info.Format > ProjectInfo.CurrentFormat) throw new CorruptDataException($"This project was saved by a newer WadSmith (format {info.Format}); update WadSmith to open it.");
        return new Project(folder, info);
    }

    /// <summary>Reads a map of the project.</summary>
    /// <summary>The classic map namespace the project's game uses (doom, heretic, hexen or strife).</summary>
    public string ClassicNamespace => Games.GameCatalog.Get(Info.Game)?.MapNamespace ?? "doom";

    /// <summary>Whether the project's maps are kept in the classic format (a classic target).</summary>
    public bool ClassicFormat => Targets.IsClassic(Info.Target);

    /// <summary>Changes what the project plays in and saves the record; the next save of each map writes the target's format.</summary>
    public void SetTarget(string target)
    {
        if (!Targets.IsKnown(target)) throw new ArgumentException($"{target} is not a target WadSmith knows.", nameof(target));
        lock (_gate)
        {
            Info = Info with { Target = target };
            WriteAtomically(Path.Combine(Folder, SidecarName), JsonSerializer.SerializeToUtf8Bytes(Info, Json));
        }
    }

    /// <summary>The map as the file holds it: in GZDoom's namespace, or, for a classic target, in the game's classic one.</summary>
    public MapModel LoadMap(string slot)
    {
        var entry = Info.Maps.FirstOrDefault(m => LumpName.Comparer.Equals(m.Slot, slot)) ?? throw new KeyNotFoundException($"The project has no map {slot}.");
        var wad = WadContainer.Open(Path.Combine(Folder, entry.File));
        var lumps = MapLocator.Find(wad).FirstOrDefault() ?? throw new CorruptDataException($"{entry.File} holds no map.");
        return MapConversion.Read(lumps, ClassicNamespace);
    }

    /// <summary>
    /// Writes a map as <c>maps/SLOT.wad</c>, adding it to the project's list if it is new: in UDMF for a GZDoom target,
    /// in the classic format for a classic one (the map comes in the game's classic namespace then, see
    /// <see cref="Translation.ClassicTranslation"/>).
    /// </summary>
    public void SaveMap(MapModel map, string title = "", MapOrigin? origin = null)
    {
        if (!ClassicFormat && map.Namespace != "zdoom") throw new ArgumentException($"Projects hold maps in GZDoom's namespace; translate this {map.Namespace} map first.", nameof(map));
        if (ClassicFormat && map.Namespace == "zdoom") throw new ArgumentException("A project with a classic target keeps its maps in the classic format; translate this map back first.", nameof(map));
        var relative = $"maps/{map.Name.ToUpperInvariant()}.wad";
        var lumps = new List<(string, ReadOnlyMemory<byte>)> { (map.Name.ToUpperInvariant(), Array.Empty<byte>()) };
        if (ClassicFormat)
        {
            // The classic ports need the map's nodes: built by WadSmith on every save (6b.2).
            var classic = MapConversion.ToClassic(map, map.Namespace == "hexen" ? MapFormat.Hexen : MapFormat.Doom);
            WadSmith.Core.Maps.Classic.NodeBuilder.Build(classic);
            lumps.AddRange(classic.WriteLumps().Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
        }
        else lumps.AddRange(MapConversion.ToUdmfLumps(map).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
        WriteAtomically(Path.Combine(Folder, relative), WadWriter.Write(WadKind.Pwad, lumps));

        lock (_gate)
        {
            var existing = Info.Maps.FindIndex(m => LumpName.Comparer.Equals(m.Slot, map.Name));
            var entry = new ProjectMap { Slot = map.Name.ToUpperInvariant(), Title = title, File = relative, Origin = origin };
            if (existing >= 0)
            {
                var old = Info.Maps[existing];
                Info.Maps[existing] = old with { File = relative, Title = title.Length > 0 ? title : old.Title, Origin = origin ?? old.Origin };
            }
            else
            {
                Info.Maps.Add(entry);
            }

            WriteAtomically(Path.Combine(Folder, SidecarName), JsonSerializer.SerializeToUtf8Bytes(Info, Json));
        }
    }

    /// <summary>The project's entry for <paramref name="slot"/>, or null (any thread).</summary>
    public ProjectMap? Map(string slot)
    {
        lock (_gate) return Info.Maps.FirstOrDefault(m => LumpName.Comparer.Equals(m.Slot, slot));
    }

    /// <summary>The project's maps as they are now (any thread).</summary>
    public IReadOnlyList<ProjectMap> Maps()
    {
        lock (_gate) return [.. Info.Maps];
    }

    /// <summary>Changes a map's settings (its title, music, sky, next map) and writes the record (any thread). Returns the entry as it is now.</summary>
    public ProjectMap UpdateMap(string slot, Func<ProjectMap, ProjectMap> change)
    {
        lock (_gate)
        {
            var i = Info.Maps.FindIndex(m => LumpName.Comparer.Equals(m.Slot, slot));
            if (i < 0) throw new KeyNotFoundException($"The project has no map {slot}.");
            var changed = change(Info.Maps[i]) with { Slot = Info.Maps[i].Slot, File = Info.Maps[i].File };
            Info.Maps[i] = changed;
            WriteAtomically(Path.Combine(Folder, SidecarName), JsonSerializer.SerializeToUtf8Bytes(Info, Json));
            return changed;
        }
    }

    public void SaveInfo()
    {
        lock (_gate) WriteAtomically(Path.Combine(Folder, SidecarName), JsonSerializer.SerializeToUtf8Bytes(Info, Json));
    }

    /// <summary>A project found in the projects folder: where it is, what its sidecar says, and when it was last changed.</summary>
    public sealed record Found(string Folder, ProjectInfo Info, DateTime Modified);

    /// <summary>
    /// Every project directly inside <paramref name="projectsFolder"/>, newest change first; a folder whose sidecar cannot
    /// be read is left out. The Deleted folder (see <see cref="Delete"/>) is not looked in.
    /// </summary>
    public static IReadOnlyList<Found> List(string projectsFolder)
    {
        var found = new List<Found>();
        if (!Directory.Exists(projectsFolder)) return found;
        foreach (var folder in Directory.EnumerateDirectories(projectsFolder))
        {
            if (string.Equals(Path.GetFileName(folder), DeletedFolderName, StringComparison.Ordinal)) continue;
            var sidecar = Path.Combine(folder, SidecarName);
            if (!File.Exists(sidecar)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(sidecar), Json);
                if (info is null) continue;
                var maps = Path.Combine(folder, "maps");
                var modified = File.GetLastWriteTimeUtc(sidecar);
                if (Directory.Exists(maps)) modified = Directory.EnumerateFiles(maps).Select(File.GetLastWriteTimeUtc).Append(modified).Max();
                found.Add(new Found(Path.GetFullPath(folder), info, modified));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return [.. found.OrderByDescending(f => f.Modified)];
    }

    /// <summary>Where deleted projects go, inside the projects folder: nothing is lost, only moved.</summary>
    public const string DeletedFolderName = "Deleted";

    /// <summary>Adds a file the project loads with its game (a map pack it was started from), in play and in the editor, and saves the record.</summary>
    public void AddAddOn(string path)
    {
        if (Info.AddOns.Contains(path, StringComparer.Ordinal)) return;
        Info.AddOns.Add(path);
        SaveInfo();
    }

    /// <summary>Gives the project a new name (its folder stays where it is).</summary>
    public void Rename(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A map needs a name.", nameof(name));
        Info = Info with { Name = name };
        SaveInfo();
    }

    /// <summary>A whole copy of the project at <paramref name="folder"/> in <paramref name="toFolder"/> (which must not exist), named <paramref name="name"/>.</summary>
    public static Project Copy(string folder, string toFolder, string name)
    {
        folder = Path.GetFullPath(folder);
        toFolder = Path.GetFullPath(toFolder);
        if (Directory.Exists(toFolder)) throw new IOException($"{toFolder} already exists.");
        CopyTree(folder, toFolder);
        var copy = Open(toFolder);
        copy.Rename(name);
        return copy;
    }

    /// <summary>
    /// Moves the project at <paramref name="folder"/> into the projects folder's Deleted folder, under its own name and the
    /// time, and returns where it went: a deleted map can be brought back by moving it out again.
    /// </summary>
    public static string Delete(string folder, string projectsFolder)
    {
        folder = Path.GetFullPath(folder);
        var deleted = Path.Combine(projectsFolder, DeletedFolderName);
        Directory.CreateDirectory(deleted);
        var target = Path.Combine(deleted, $"{Path.GetFileName(folder)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}");
        for (var n = 2; Directory.Exists(target); n++) target = Path.Combine(deleted, $"{Path.GetFileName(folder)} {DateTime.Now:yyyy-MM-dd HH.mm.ss} {n}");
        Directory.Move(folder, target);
        return target;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private readonly Lock _gate = new();

    /// <summary>The copy of <paramref name="original"/> from <paramref name="game"/> already in the project, if any (any thread).</summary>
    public CopiedAsset? FindCopy(string game, string original, string kind)
    {
        lock (_gate) return Info.Copied.LastOrDefault(c => c.Game == game && c.Kind == kind && string.Equals(c.Original, original, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the project's record says was copied in, as it stands now (any thread: a copy taken under the record's lock).</summary>
    public IReadOnlyList<CopiedAsset> CopiedNow()
    {
        lock (_gate) return [.. Info.Copied];
    }

    /// <summary>Notes a copy in the project's record and writes the record (any thread).</summary>
    public void Record(CopiedAsset copy)
    {
        lock (_gate)
        {
            Info.Copied.Add(copy);
            WriteAtomically(Path.Combine(Folder, SidecarName), JsonSerializer.SerializeToUtf8Bytes(Info, Json));
        }
    }

    /// <summary>Notes many copies in the project's record and writes the record once (any thread).</summary>
    public void Record(IReadOnlyCollection<CopiedAsset> copies)
    {
        if (copies.Count == 0) return;
        lock (_gate)
        {
            Info.Copied.AddRange(copies);
            WriteAtomically(Path.Combine(Folder, SidecarName), JsonSerializer.SerializeToUtf8Bytes(Info, Json));
        }
    }

    /// <summary>
    /// Writes many files into the project, each in one step, then makes them all durable at once: one sync of the file system
    /// on Linux (a disk sync per picture took a minute for a few hundred on a spinning disk), each file's own elsewhere.
    /// Record the copies after this returns, so a record never names a file that is not on the disk.
    /// </summary>
    public void WriteFiles(IReadOnlyList<(string RelativePath, byte[] Data)> files)
    {
        if (files.Count == 0) return;
        var linux = OperatingSystem.IsLinux();
        foreach (var (relativePath, data) in files)
        {
            var path = Path.Combine(Folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".saving";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(data);
                if (!linux) f.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }

        if (linux) SyncFileSystem(Folder);
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int syncfs(int fd);

    /// <summary>Flushes the file system holding <paramref name="folder"/> to the disk (Linux's syncfs).</summary>
    private static void SyncFileSystem(string folder)
    {
        using var handle = File.OpenHandle(Path.Combine(folder, SidecarName), FileMode.Open, FileAccess.Read);
        if (syncfs((int)handle.DangerousGetHandle()) != 0) throw new IOException($"The folder {folder} could not be written to the disk (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>Writes a file into the project in one step (a crash leaves the old file or the new, never half).</summary>
    public void WriteFile(string relativePath, byte[] data)
    {
        var path = Path.Combine(Folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, data);
    }

    internal static void WriteAtomically(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".saving";
        using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(data);
            f.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
