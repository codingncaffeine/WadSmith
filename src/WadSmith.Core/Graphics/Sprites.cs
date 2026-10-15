using WadSmith.Core.Containers;

namespace WadSmith.Core.Graphics;

/// <summary>
/// A sprite lump name: four letters naming the sprite, a frame letter and a rotation (0 for "all
/// angles", 1–8 around the actor, 9 and A–G for GZDoom's 16 angles), optionally followed by a second
/// frame and rotation drawn mirrored (TROOA2A8 is frame A at angle 2, and at angle 8 flipped).
/// </summary>
public readonly record struct SpriteFrameName(string Sprite, char Frame, char Rotation, char? MirrorFrame, char? MirrorRotation)
{
    public static bool TryParse(string lumpName, out SpriteFrameName name)
    {
        name = default;
        var n = LumpName.Normalize(lumpName);
        if (n.Length is not (6 or 8)) return false;
        if (!IsFrame(n[4]) || !IsRotation(n[5])) return false;
        char? f2 = null, r2 = null;
        if (n.Length == 8)
        {
            if (!IsFrame(n[6]) || !IsRotation(n[7])) return false;
            f2 = n[6];
            r2 = n[7];
        }

        // GZDoom reads ^ as the \ frame a file name cannot hold (R_InstallSprite).
        static char Frame(char c) => c == '^' ? '\\' : c;
        name = new SpriteFrameName(n[..4], Frame(n[4]), n[5], f2 is { } m ? Frame(m) : null, r2);
        return true;
    }

    /// <summary>The lump name this frame has, as a file name can hold it (the \ frame written ^).</summary>
    public string Lump
    {
        get
        {
            static char Written(char c) => c == '\\' ? '^' : c;
            var name = $"{Sprite}{Written(Frame)}{Rotation}";
            return MirrorFrame is { } m && MirrorRotation is { } r ? $"{name}{Written(m)}{r}" : name;
        }
    }

    /// <summary>A through Z and the four characters GZDoom allows after them.</summary>
    private static bool IsFrame(char c) => c is >= 'A' and <= ']' or '^';

    private static bool IsRotation(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'G';
}

/// <summary>
/// The sprite lumps of a resource stack, grouped by sprite name, under the names the engine gives them. GZDoom renames some
/// of Heretic's, Hexen's and Strife's own sprites as it loads the game (RenameSprites), so an object of any game can be
/// carried into any other without two sprites sharing a name, and the objects name the new ones: Strife's stalker is SPID
/// in STRIFE1.WAD and STLK in the engine, leaving SPID to Doom's spider mastermind.
/// </summary>
public sealed class SpriteIndex
{
    private readonly Dictionary<string, List<(SpriteFrameName Name, ResourceEntry Entry)>> _bySprite = new(StringComparer.Ordinal);

    /// <param name="stack">The game's files first (the renames touch only the first, the game's own), then add-ons and the project.</param>
    /// <param name="family">The family the stack plays as (doom, heretic, hexen, strife, chex); null renames nothing.</param>
    public SpriteIndex(ResourceStack stack, string? family = null)
    {
        var renames = family is null ? null : Renames.GetValueOrDefault(family);
        // Maulotaur frames F to K become U to Z unless some file draws the whole animation itself (a frame Z).
        var minotaurWhole = family is null || stack.Containers.SelectMany(c => c.Entries).Any(e => !e.IsMarker && e.Namespace == ResourceNamespace.Sprites && LumpName.Normalize(e.Name).StartsWith("MNTRZ", StringComparison.Ordinal));
        // Outside Doom and Chex Quest every file's BLOD is BLUD, so one blood serves every game.
        var blood = family is "heretic" or "hexen" or "strife";

        // One picture per name, the last loaded one, in first-seen order, as the engine's own lookup has it.
        var order = new List<string>();
        var winner = new Dictionary<string, (SpriteFrameName, ResourceEntry)>(StringComparer.Ordinal);
        for (var i = 0; i < stack.Containers.Count; i++)
        {
            foreach (var e in stack.Containers[i].Entries)
            {
                if (e.IsMarker || e.Namespace != ResourceNamespace.Sprites || !SpriteFrameName.TryParse(e.Name, out var n)) continue;
                if (i == 0 && renames is not null && renames.TryGetValue(n.Sprite, out var renamed)) n = n with { Sprite = renamed };
                if (!minotaurWhole && n.Sprite == "MNTR")
                {
                    static char Later(char f) => f is >= 'F' and <= 'K' ? (char)(f + ('U' - 'F')) : f;
                    n = n with { Frame = Later(n.Frame), MirrorFrame = n.MirrorFrame is { } m ? Later(m) : null };
                }

                if (blood && n.Sprite == "BLOD") n = n with { Sprite = "BLUD" };
                var key = n.Lump;
                if (!winner.ContainsKey(key)) order.Add(key);
                winner[key] = (n, e);
            }
        }

        foreach (var key in order)
        {
            var (n, e) = winner[key];
            if (!_bySprite.TryGetValue(n.Sprite, out var list)) _bySprite[n.Sprite] = list = [];
            list.Add((n, e));
        }
    }

    /// <summary>The sprites GZDoom renames in each family's own file (d_main.cpp, RenameSprites), old name to new.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Renames = new(StringComparer.Ordinal)
    {
        ["heretic"] = new(StringComparer.Ordinal) { ["HEAD"] = "LICH" },
        ["hexen"] = new(StringComparer.Ordinal)
        {
            ["BARL"] = "ZBAR", ["ARM1"] = "AR_1", ["ARM2"] = "AR_2", ["ARM3"] = "AR_3", ["ARM4"] = "AR_4", ["SUIT"] = "ZSUI", ["TRE1"] = "ZTRE",
            ["TRE2"] = "TRES", ["CAND"] = "BCAN", ["ROCK"] = "ROKK", ["WATR"] = "HWAT", ["GIBS"] = "POL5", ["EGGM"] = "PRKM", ["INVU"] = "DEFN",
        },
        ["strife"] = new(StringComparer.Ordinal)
        {
            ["MISL"] = "SMIS", ["ARM1"] = "ARM3", ["ARM2"] = "ARM4", ["PMAP"] = "SMAP", ["TLMP"] = "TECH", ["TRE1"] = "TRET", ["BAR1"] = "BARC",
            ["SHT2"] = "MPUF", ["BARL"] = "BBAR", ["TRCH"] = "TRHL", ["SHRD"] = "SHAR", ["BLST"] = "MAUL", ["LOGG"] = "LOGW", ["VASE"] = "VAZE",
            ["CNDL"] = "KNDL", ["POT1"] = "MPOT", ["SPID"] = "STLK",
        },
    };

    public IReadOnlyCollection<string> Sprites => _bySprite.Keys;

    public IReadOnlyList<(SpriteFrameName Name, ResourceEntry Entry)> Frames(string sprite) =>
        _bySprite.TryGetValue(LumpName.Normalize(sprite), out var list) ? list : [];

    /// <summary>
    /// The lump that shows a sprite frame from the front, as an editor thumbnail does: rotation 0,
    /// else rotation 1 (also as the second half of a mirrored pair). Null when the frame is missing.
    /// </summary>
    public (ResourceEntry Entry, bool Mirrored)? Front(string sprite, char frame = 'A')
    {
        var frames = Frames(sprite);
        foreach (var rotation in new[] { '0', '1' })
        {
            foreach (var (n, e) in frames)
            {
                if (n.Frame == frame && n.Rotation == rotation) return (e, false);
            }

            foreach (var (n, e) in frames)
            {
                if (n.MirrorFrame == frame && n.MirrorRotation == rotation) return (e, true);
            }
        }

        return null;
    }
}
