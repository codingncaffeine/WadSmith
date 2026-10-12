using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>
/// The engine's own per-map fixes, keyed by map checksum: <c>compatibility.txt</c> (behaviour switches
/// for specific maps) and <c>zscript/level_compatibility.zs</c> (repairs to specific maps' lines,
/// sectors and things). Both apply only to the original maps; an edited map has a new checksum.
/// </summary>
public sealed partial class EngineCompatibility
{
    private readonly HashSet<string> _flagged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _repaired = new(StringComparer.OrdinalIgnoreCase);

    public static EngineCompatibility Load(ResourceStack engine)
    {
        var c = new EngineCompatibility();
        foreach (var container in engine.Containers)
        {
            if (container.FindPath("compatibility.txt") is { } flags)
            {
                foreach (Match m in Checksum().Matches(Encoding.Latin1.GetString(flags.Read()))) c._flagged.Add(m.Groups[1].Value);
            }

            if (container.FindPath("zscript/level_compatibility.zs") is { } script)
            {
                foreach (Match m in CaseChecksum().Matches(Encoding.Latin1.GetString(script.Read()))) c._repaired.Add(m.Groups[1].Value);
            }
        }

        return c;
    }

    /// <summary>The engine switches some of its behaviour for this map (compatibility.txt).</summary>
    public bool HasBehaviourSwitches(string checksum) => _flagged.Contains(checksum);

    /// <summary>The engine repairs this map's data when it loads it (level_compatibility.zs).</summary>
    public bool HasRepairs(string checksum) => _repaired.Contains(checksum);

    public int Count => _flagged.Count + _repaired.Count;

    /// <summary>Every map checksum the engine repairs.</summary>
    public IReadOnlyCollection<string> RepairedMaps => _repaired;

    /// <summary>
    /// The line flags a game's MAPINFO <c>gamedefaults</c> turns on for every line of every map
    /// (Strife's clipped middle textures, for one), as GZDoom's ML_ bits.
    /// </summary>
    public static int GameDefaultLineFlags(ResourceStack engine, string engineGame)
    {
        var path = engineGame switch
        {
            "heretic" => "mapinfo/heretic.txt",
            "hexen" => "mapinfo/hexen.txt",
            "strife" => "mapinfo/strife.txt",
            "chex" => "mapinfo/chex.txt",
            _ => "mapinfo/doomcommon.txt",
        };
        ResourceEntry? entry = null;
        for (var i = engine.Containers.Count - 1; i >= 0 && entry is null; i--) entry = engine.Containers[i].FindPath(path);
        if (entry is null) return 0;
        var text = Encoding.Latin1.GetString(entry.Read());
        var m = GameDefaults().Match(text);
        if (!m.Success) return 0;
        var flags = 0;
        foreach (var word in m.Groups[1].Value.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            flags |= word.ToLowerInvariant() switch
            {
                "clipmidtextures" => 0x00080000,
                "wrapmidtextures" => 0x00100000,
                "checkswitchrange" => 0x00400000,
                _ => 0,
            };
        }

        return flags;
    }

    [GeneratedRegex(@"gamedefaults\s*\{([^}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex GameDefaults();

    [GeneratedRegex(@"^\s*([0-9A-Fa-f]{32})\b", RegexOptions.Multiline)]
    private static partial Regex Checksum();

    [GeneratedRegex(@"case\s+'([0-9A-Fa-f]{32})'")]
    private static partial Regex CaseChecksum();
}
