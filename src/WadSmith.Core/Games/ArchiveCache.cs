using System.Text.Json;
using System.Text.Json.Serialization;

namespace WadSmith.Core.Games;

/// <summary>What one archive held the last time it was read, kept by its size and date so an unchanged archive is not opened again.</summary>
public sealed record ArchiveRecord
{
    public long Length { get; init; }

    public DateTime Written { get; init; }

    /// <summary>The archive is itself a base game laid out as a PK3 (this id), or null.</summary>
    public string? Packed { get; init; }

    /// <summary>How games were told apart and what was noted of each WAD when it was read (<see cref="ArchiveCache.Mark"/>); null in a record older than the mark.</summary>
    public string? Rules { get; init; }

    public List<CachedGame> Games { get; init; } = [];

    public List<CachedMapFile> MapFiles { get; init; } = [];
}

public sealed class CachedGame
{
    public required string Id { get; init; }

    public required string Inside { get; init; }

    public long Size { get; init; }

    public bool Iwad { get; init; }
}

public sealed class CachedMapFile
{
    public required string Inside { get; init; }

    public List<string> Maps { get; init; } = [];

    /// <summary>The WAD's own length in bytes.</summary>
    public long Size { get; init; }
}

/// <summary>
/// The archives a scan has read, remembered between runs (a JSON file in the cache folder): a map collection of hundreds
/// of ZIPs is read once, and later scans only look at what changed. Losing the file costs one slow scan, nothing else.
/// What an archive held is remembered as games and map packs, so a record made when games were told apart another way
/// is not believed: that archive is read again.
/// </summary>
public sealed class ArchiveCache
{
    private readonly Dictionary<string, ArchiveRecord> _records;

    /// <summary>What a record holds, as a number raised when more is noted of an archive (2: each WAD's length).</summary>
    private const int Format = 2;

    /// <summary>
    /// The mark a record is believed under: how games are told apart (<see cref="GameCatalog.Stamp"/>) and what a record
    /// holds. A record under another mark is read again.
    /// </summary>
    public static string Mark => $"{GameCatalog.Stamp}.{Format}";

    private ArchiveCache(Dictionary<string, ArchiveRecord> records) => _records = records;

    /// <summary>How many archives were answered from memory in this scan.</summary>
    public int Hits { get; private set; }

    public int Count => _records.Count;

    public static ArchiveCache Empty() => new(new Dictionary<string, ArchiveRecord>(StringComparer.Ordinal));

    /// <summary>The cache in <paramref name="path"/>, or an empty one when there is none or it cannot be read.</summary>
    public static ArchiveCache Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty();
            var records = JsonSerializer.Deserialize<Dictionary<string, ArchiveRecord>>(File.ReadAllText(path), Json);
            return new ArchiveCache(records is null ? new Dictionary<string, ArchiveRecord>(StringComparer.Ordinal) : new Dictionary<string, ArchiveRecord>(records, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(_records, Json));
        File.Move(temp, path, overwrite: true);
    }

    internal bool TryGet(string archive, long length, DateTime written, out ArchiveRecord record)
    {
        if (_records.TryGetValue(archive, out var found) && found.Length == length && found.Written == written && found.Rules == Mark)
        {
            Hits++;
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    internal void Put(string archive, ArchiveRecord record) => _records[archive] = record with { Rules = Mark };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
