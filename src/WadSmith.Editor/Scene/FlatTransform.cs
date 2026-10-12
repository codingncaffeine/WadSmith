using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Scene;

/// <summary>
/// How a floor's or ceiling's texture lies on it: GZDoom's UDMF fields xpanning, ypanning, rotation, xscale and yscale
/// (…floor or …ceiling). Measured against GZDoom 2026-10-12 (a 512 room floored with BIGDOOR1, seen from above): a texel is
/// scale × (the map point (x, −y) turned by −rotation, plus panning) — a rotation of 30 turns the texture clockwise as seen
/// from above, a positive panning moves it west and north, and a scale above 1 makes it smaller.
/// </summary>
public readonly record struct FlatTransform(double XPan, double YPan, double Rotation, double XScale, double YScale)
{
    public static FlatTransform Plain { get; } = new(0, 0, 0, 1, 1);

    public bool IsPlain => this == Plain;

    /// <summary>The texture position (in texels) at a map point.</summary>
    public (double U, double V) Texel(double x, double y)
    {
        if (IsPlain) return (x, -y);
        var a = -Rotation * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        return (XScale * (x * c + y * s + XPan), YScale * (x * s - y * c + YPan));
    }

    /// <summary>The floor's (or ceiling's) transform from an area's fields (any thread: frames).</summary>
    public static FlatTransform Of(KeyValuePair<string, UdmfValue>[] extra, bool ceiling)
    {
        var part = ceiling ? "ceiling" : "floor";
        double Get(string name, double otherwise)
        {
            foreach (var (k, v) in extra)
            {
                if (k == name && v.Kind is UdmfValueKind.Float or UdmfValueKind.Integer) return v.AsFloat();
            }

            return otherwise;
        }

        return new FlatTransform(Get("xpanning" + part, 0), Get("ypanning" + part, 0), Get("rotation" + part, 0), Get("xscale" + part, 1), Get("yscale" + part, 1));
    }

    /// <summary>The same for an area on the map (its own fields, on the UI thread).</summary>
    public static FlatTransform Of(Sector s, bool ceiling) => Of([.. s.Extra], ceiling);
}
