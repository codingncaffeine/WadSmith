using System.Text.Json;
using System.Text.Json.Serialization;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>A base game WadSmith can recognise from its lumps.</summary>
public sealed record GameDefinition
{
    /// <summary>A stable short id such as "doom2" or "heretic-sotsr".</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>The game family the engine runs: doom, heretic, hexen, strife, chex (or doom64, which no supported engine runs).</summary>
    public required string Engine { get; init; }

    /// <summary>What the fields of a classic-format map mean in this game: doom, heretic, hexen or strife.</summary>
    public required string MapNamespace { get; init; }

    /// <summary>Lump names that must all be present.</summary>
    public required IReadOnlyList<string> MustContain { get; init; }

    /// <summary>"MAPxx" or "ExMy".</summary>
    public string? MapNames { get; init; }

    /// <summary>Free to share (Freedoom and friends).</summary>
    public bool Free { get; init; }

    /// <summary>False for games WadSmith recognises only to say it cannot edit them.</summary>
    public bool Supported { get; init; } = true;
}

/// <summary>The base games from <c>data/games/games.json</c>, and identification by content.</summary>
public static class GameCatalog
{
    private static readonly Lazy<IReadOnlyList<GameDefinition>> Games = new(Load);

    public static IReadOnlyList<GameDefinition> All => Games.Value;

    public static GameDefinition? Get(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>
    /// The game a container is, judged only by the lumps it holds (never by its file name or its
    /// IWAD/PWAD header, which repackaged copies change). Null when nothing matches.
    /// </summary>
    public static GameDefinition? Identify(ResourceContainer container)
    {
        var names = new HashSet<string>(container.Entries.Select(e => e.Name), LumpName.Comparer);
        return All.FirstOrDefault(g => g.MustContain.All(names.Contains));
    }

    internal static IReadOnlyList<GameDefinition> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<GamesFile>(json, JsonOptions) ?? throw new InvalidDataException("games.json is empty.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in file.Games)
        {
            if (!seen.Add(g.Id)) throw new InvalidDataException($"games.json lists \"{g.Id}\" twice.");
            if (g.MustContain.Count == 0) throw new InvalidDataException($"games.json: \"{g.Id}\" has no lumps to recognise it by.");
            if (g.MapNamespace is not ("doom" or "heretic" or "hexen" or "strife")) throw new InvalidDataException($"games.json: \"{g.Id}\" has an unknown mapNamespace \"{g.MapNamespace}\".");
            if (g.Supported && g.MapNames is not ("MAPxx" or "ExMy")) throw new InvalidDataException($"games.json: \"{g.Id}\" needs mapNames MAPxx or ExMy.");
        }

        return file.Games;
    }

    private static IReadOnlyList<GameDefinition> Load() => Parse(DataFiles.ReadText("games/games.json"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record GamesFile
    {
        public string? Comment { get; init; }

        public required List<GameDefinition> Games { get; init; }
    }
}
