using System.Text.Json;
using System.Text.Json.Serialization;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Projects;

/// <summary>Where a map in a project came from.</summary>
public sealed record MapOrigin
{
    /// <summary>"new" for a map made in WadSmith, "remix" for one started from a base game's map, "import" for one brought in from another file.</summary>
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

/// <summary>The project's own record, <c>wadsmith.json</c>: what GZDoom does not need to know but WadSmith does.</summary>
public sealed record ProjectInfo
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public required string Name { get; init; }

    /// <summary>The base game the project is for (an id from data/games/games.json).</summary>
    public required string Game { get; init; }

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    public List<ProjectMap> Maps { get; init; } = [];

    /// <summary>Add-on files the project needs loaded before itself, in order.</summary>
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

    public static Project Create(string folder, string name, string game)
    {
        folder = Path.GetFullPath(folder);
        if (File.Exists(Path.Combine(folder, SidecarName))) throw new IOException($"{folder} already holds a WadSmith project.");
        Directory.CreateDirectory(Path.Combine(folder, "maps"));
        var p = new Project(folder, new ProjectInfo { Name = name, Game = game });
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
    public MapModel LoadMap(string slot)
    {
        var entry = Info.Maps.FirstOrDefault(m => LumpName.Comparer.Equals(m.Slot, slot)) ?? throw new KeyNotFoundException($"The project has no map {slot}.");
        var wad = WadContainer.Open(Path.Combine(Folder, entry.File));
        var lumps = MapLocator.Find(wad).FirstOrDefault() ?? throw new CorruptDataException($"{entry.File} holds no map.");
        return MapConversion.Read(lumps);
    }

    /// <summary>Writes a map as <c>maps/SLOT.wad</c> in UDMF, adding it to the project's list if it is new.</summary>
    public void SaveMap(MapModel map, string title = "", MapOrigin? origin = null)
    {
        if (map.Namespace != "zdoom") throw new ArgumentException($"Projects hold maps in GZDoom's namespace; translate this {map.Namespace} map first.", nameof(map));
        var relative = $"maps/{map.Name.ToUpperInvariant()}.wad";
        var lumps = new List<(string, ReadOnlyMemory<byte>)> { (map.Name.ToUpperInvariant(), Array.Empty<byte>()) };
        lumps.AddRange(MapConversion.ToUdmfLumps(map).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
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

    private readonly Lock _gate = new();

    /// <summary>The copy of <paramref name="original"/> from <paramref name="game"/> already in the project, if any (any thread).</summary>
    public CopiedAsset? FindCopy(string game, string original, string kind)
    {
        lock (_gate) return Info.Copied.LastOrDefault(c => c.Game == game && c.Kind == kind && string.Equals(c.Original, original, StringComparison.OrdinalIgnoreCase));
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
