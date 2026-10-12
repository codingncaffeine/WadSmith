using System.Globalization;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>How a light shines: steadily, pulsing between two reaches, or flickering between them.</summary>
public enum LightKind
{
    Steady,
    Pulsing,
    Flickering,
    RandomFlicker,
}

/// <summary>
/// A light found in the map (D21): one of GZDoom's point light objects, its colour, how far it reaches (and its
/// second reach, for pulsing and flickering ones) and how high above the floor it hangs.
/// </summary>
/// <param name="Thing">The light object.</param>
/// <param name="Kind">How it shines.</param>
/// <param name="Soft">Whether it fades softly with distance and lights surfaces by the way they face (an attenuated light).</param>
/// <param name="Colour">Its colour, 0–255 each.</param>
/// <param name="Reach">How far it reaches, in map units (its radius).</param>
/// <param name="SecondReach">The other reach a pulsing or flickering light goes to.</param>
public sealed record Light(Thing Thing, LightKind Kind, bool Soft, (int R, int G, int B) Colour, int Reach, int SecondReach)
{
    public double Height => Thing.Height;
}

/// <summary>
/// GZDoom's point lights (editor numbers 9800–9834): plain, additive (+10), subtractive (+20) and attenuated (+30),
/// each steady (+0), pulsing (+1), flickering (+2) or flickering at random (+4). The colour is in the first three
/// arguments, the reach in the fourth and the second reach in the fifth. The Light tool makes soft (attenuated) ones.
/// </summary>
public static class Lights
{
    /// <summary>The colours the Inspector offers, by name.</summary>
    public static readonly IReadOnlyList<(string Name, int R, int G, int B)> Colours =
    [
        ("White", 255, 255, 255), ("Warm", 255, 224, 192), ("Red", 255, 64, 32), ("Orange", 255, 128, 32),
        ("Yellow", 255, 224, 64), ("Green", 64, 255, 96), ("Blue", 64, 128, 255), ("Purple", 192, 64, 255),
    ];

    /// <summary>What a new light is like: warm, 128 across, 48 above the floor.</summary>
    public const int DefaultReach = 128;
    public const double DefaultHeight = 48;

    /// <summary>The light <paramref name="thing"/> is, or null.</summary>
    public static Light? Recognise(Thing thing)
    {
        if (!thing.OnMap || Kind(thing.Type) is not { } kind) return null;
        var family = (thing.Type - 9800) / 10;
        return new Light(thing, kind, family == 3, (thing.Args[0], thing.Args[1], thing.Args[2]), thing.Args[3], thing.Args[4]);
    }

    /// <summary>The kind of point light an editor number is, or null (sector and spot lights are not point lights).</summary>
    public static LightKind? Kind(int type) => type is >= 9800 and <= 9834 ? ((type - 9800) % 10) switch
    {
        0 => LightKind.Steady,
        1 => LightKind.Pulsing,
        2 => LightKind.Flickering,
        4 => LightKind.RandomFlicker,
        _ => null,
    } : null;

    /// <summary>Whether a light of this editor number takes light away (a subtractive light).</summary>
    public static bool Subtracts(int type) => type is >= 9820 and <= 9824;

    /// <summary>The editor number for a kind of light, soft (attenuated) or not.</summary>
    public static int TypeOf(LightKind kind, bool soft) => (soft ? 9830 : 9800) + kind switch
    {
        LightKind.Pulsing => 1,
        LightKind.Flickering => 2,
        LightKind.RandomFlicker => 4,
        _ => 0,
    };

    /// <summary>A new soft, warm light at a point, on every difficulty and in every game mode.</summary>
    public static Thing New(MapPoint at, string engine)
    {
        var (_, r, g, b) = Colours[1];
        var light = new Thing { X = at.X, Y = at.Y, Type = TypeOf(LightKind.Steady, soft: true), Height = DefaultHeight };
        int[] args = [r, g, b, DefaultReach, DefaultReach / 2];
        args.CopyTo(light.Args, 0);
        foreach (var f in Things.Everywhere(engine)) light.SetFlag(f, true);
        return light;
    }

    /// <summary>A colour as #RRGGBB.</summary>
    public static string Hex((int R, int G, int B) c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    /// <summary>A colour from #RRGGBB (or RRGGBB), or null.</summary>
    public static (int R, int G, int B)? ParseHex(string text)
    {
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }
}
