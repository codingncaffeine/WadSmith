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

    /// <summary>An official add-on that loads over this base game (Deathkings over Hexen); null for a base game.</summary>
    public string? AddOnFor { get; init; }

    /// <summary>The title of the game's English Wikipedia article, whose lead picture is the game's cover on the start screen (D53); null for a game without one.</summary>
    public string? Wiki { get; init; }

    public bool IsBaseGame => AddOnFor is null;
}

/// <summary>The base games from <c>data/games/games.json</c>, and identification by content.</summary>
public static class GameCatalog
{
    private static readonly Lazy<IReadOnlyList<GameDefinition>> Games = new(Load);

    public static IReadOnlyList<GameDefinition> All => Games.Value;

    public static GameDefinition? Get(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>
    /// The lumps every complete base game of an engine family holds: its palette, light tables,
    /// player sprite and status bar picture. Add-ons that reuse map names (MAP01, E1M1) never carry
    /// the whole set, so they are not mistaken for a base game. The Doom family's set also names the
    /// sounds of the player and the world (<see cref="DoomSounds"/>): a map pack with a palette of its
    /// own brings every picture drawn in it, the player's and the status bar's among them, and would
    /// pass for the game by its pictures alone.
    /// </summary>
    public static IReadOnlyList<string> CoreLumps(string engine) => engine switch
    {
        "heretic" => ["PLAYPAL", "COLORMAP", "PLAYA1", "BARBACK"],
        "hexen" => ["PLAYPAL", "COLORMAP", "PLAYA1", "H2BAR"],
        "strife" => ["PLAYPAL", "COLORMAP", "PLAYA1", "INVBACK"],
        "doom64" => [],
        _ => DoomCore,
    };

    /// <summary>
    /// Sounds every game of the Doom family holds, the free and the stand-alone ones included: the pistol, the player's
    /// grunt, pain and death, the teleporter, a switch, a moving floor and a weapon picked up. A palette does not change a
    /// sound, so a map pack has no reason to bring them all.
    /// </summary>
    public static IReadOnlyList<string> DoomSounds { get; } = ["DSPISTOL", "DSOOF", "DSPLPAIN", "DSPLDETH", "DSTELEPT", "DSSWTCHX", "DSSTNMOV", "DSWPNUP"];

    private static readonly IReadOnlyList<string> DoomCore = ["PLAYPAL", "COLORMAP", "PLAYA1", "STBAR", .. DoomSounds];

    private static readonly Lazy<string> Rules = new(() =>
    {
        var text = new System.Text.StringBuilder(DataFiles.ReadText("games/games.json"));
        foreach (var engine in All.Select(g => g.Engine).Distinct().Order(StringComparer.Ordinal)) text.Append('\n').Append(engine).Append('=').AppendJoin(',', CoreLumps(engine));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    });

    /// <summary>
    /// A short mark of how games are told apart: the game list and each family's core lumps. What a scan remembered
    /// under another mark was judged by other rules, and is read again.
    /// </summary>
    public static string Stamp => Rules.Value;

    /// <summary>
    /// The game a container is, judged only by the lumps it holds (never by its file name or its
    /// IWAD/PWAD header, which repackaged copies change). Null when nothing matches.
    /// </summary>
    public static GameDefinition? Identify(ResourceContainer container) => Identify(container.Entries.Select(e => e.Name));

    /// <summary>The game a list of lump names belongs to (see <see cref="Identify(ResourceContainer)"/>).</summary>
    public static GameDefinition? Identify(IEnumerable<string> lumpNames)
    {
        var names = new HashSet<string>(lumpNames, LumpName.Comparer);
        return All.FirstOrDefault(g => g.MustContain.All(names.Contains) && (!g.IsBaseGame || CoreLumps(g.Engine).All(names.Contains)));
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
            if (g.AddOnFor is { } baseGame && !seen.Contains(baseGame) && file.Games.All(x => x.Id != baseGame)) throw new InvalidDataException($"games.json: \"{g.Id}\" is an add-on for \"{baseGame}\", which is not listed.");
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
