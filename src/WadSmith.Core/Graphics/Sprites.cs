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

    /// <summary>A through Z and the four characters GZDoom allows after them.</summary>
    private static bool IsFrame(char c) => c is >= 'A' and <= ']' or '^';

    private static bool IsRotation(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'G';
}

/// <summary>The sprite lumps of a resource stack, grouped by sprite name.</summary>
public sealed class SpriteIndex
{
    private readonly Dictionary<string, List<(SpriteFrameName Name, ResourceEntry Entry)>> _bySprite = new(StringComparer.Ordinal);

    public SpriteIndex(ResourceStack stack)
    {
        foreach (var e in stack.All(ResourceNamespace.Sprites))
        {
            if (!SpriteFrameName.TryParse(e.Name, out var n)) continue;
            if (!_bySprite.TryGetValue(n.Sprite, out var list)) _bySprite[n.Sprite] = list = [];
            list.Add((n, e));
        }
    }

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
