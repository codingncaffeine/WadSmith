using System.Text.Json;
using System.Text.Json.Serialization;

namespace WadSmith.Core.Games;

/// <summary>What a map pack fitted the last time it was read: the file as it was then, the games it was judged among, and the games it fitted.</summary>
public sealed record PackRecord
{
    public long Length { get; init; }

    public DateTime Written { get; init; }

    public string Among { get; init; } = "";

    public List<string> Games { get; init; } = [];
}

/// <summary>
/// The map packs a scan has already matched (D52), by path and, inside an archive, by entry, so a big collection is read
/// once: a pack is read again only when its file changed, or the base games it is judged among did: another set of
/// games, another file standing for one of them, or another way of judging.
/// </summary>
public sealed class PackCache
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Dictionary<string, PackRecord> _records;

    private PackCache(Dictionary<string, PackRecord> records) => _records = records;

    /// <summary>How many packs this scan took from the cache instead of reading them.</summary>
    public int Hits { get; private set; }

    public int Count => _records.Count;

    public static PackCache Empty() => new(new Dictionary<string, PackRecord>(StringComparer.Ordinal));

    public static PackCache Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty();
            var records = JsonSerializer.Deserialize<Dictionary<string, PackRecord>>(File.ReadAllText(path), Json);
            return new PackCache(records is null ? new Dictionary<string, PackRecord>(StringComparer.Ordinal) : new Dictionary<string, PackRecord>(records, StringComparer.Ordinal));
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

    /// <summary>A pack's key: its path, and its entry inside an archive.</summary>
    public static string KeyOf(MapFile pack) => pack.Inside is null ? pack.Path : pack.Path + "\u203a" + pack.Inside;

    /// <summary>
    /// The base games a fit is judged among, as one short string: each game with the file that stands for it (its path, size
    /// and date: a pack is matched by the names that file holds), and how games and packs are judged
    /// (<see cref="GameCatalog.Stamp"/>, <see cref="MapPacks.Rules"/>). A different string means every pack is judged again.
    /// </summary>
    public static string AmongOf(IEnumerable<FoundGame> bases)
    {
        var text = new System.Text.StringBuilder().Append(GameCatalog.Stamp).Append('|').Append(MapPacks.Rules);
        foreach (var g in bases.OrderBy(g => g.Game.Id, StringComparer.Ordinal))
        {
            text.Append('\n').Append(g.Game.Id).Append('|').Append(g.Path).Append('|').Append(g.Size).Append('|').Append(Written(g.Path));
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>When a game's file was last written, as a number; 0 for one that cannot be asked.</summary>
    private static long Written(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>The games <paramref name="pack"/> fitted when it was last read, when the file and the games judged among are the same.</summary>
    public bool TryGet(MapFile pack, long length, DateTime written, string among, out IReadOnlyList<string> games)
    {
        if (_records.TryGetValue(KeyOf(pack), out var r) && r.Length == length && r.Written == written && r.Among == among)
        {
            Hits++;
            games = r.Games;
            return true;
        }

        games = [];
        return false;
    }

    public void Put(MapFile pack, long length, DateTime written, string among, IEnumerable<string> games) =>
        _records[KeyOf(pack)] = new PackRecord { Length = length, Written = written, Among = among, Games = [.. games] };
}
