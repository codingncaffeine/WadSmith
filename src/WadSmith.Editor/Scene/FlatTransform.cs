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

    /// <summary>
    /// The transform that shows, at each point's image under <paramref name="p"/>, the texel the point showed before: the
    /// texture carried with a room that is moved, turned, resized or mirrored (T′ = T ∘ P⁻¹, taken apart again into scale,
    /// rotation and the map's flip; a mirror comes out as a negative scale, which GZDoom draws mirrored — measured). A resize
    /// of one side only with a turn that is not a quarter cannot be drawn exactly: the nearest without a slant is kept.
    /// With the texture's <paramref name="size"/>, panning is kept within one repeat, so a room moved by whole repeats keeps
    /// no fields.
    /// </summary>
    public FlatTransform Through(Editing.Placement p, (int Width, int Height)? size = null)
    {
        if (p == Editing.Placement.Identity) return this;
        var det = p.A * p.D - p.B * p.C;
        if (Math.Abs(det) < 1e-12) return this;

        // Now: texel = M·point + m, M = S·R(a)·F, m = S·panning, a = −rotation, F the flip of the map's y.
        var a = -Rotation * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        double m00 = XScale * c, m01 = XScale * s, m10 = YScale * s, m11 = -YScale * c;
        double mx = XScale * XPan, my = YScale * YPan;

        // After: M′ = M·P⁻¹ and m′ = m − M′·t.
        double i00 = p.D / det, i01 = -p.B / det, i10 = -p.C / det, i11 = p.A / det;
        double n00 = m00 * i00 + m01 * i10, n01 = m00 * i01 + m01 * i11, n10 = m10 * i00 + m11 * i10, n11 = m10 * i01 + m11 * i11;
        double px = mx - (n00 * p.Tx + n01 * p.Ty), py = my - (n10 * p.Tx + n11 * p.Ty);

        // M′·F = diag(s1, s2)·R(a′): the first row gives a′ and s1, the second (projected) s2.
        double q00 = n00, q01 = -n01, q10 = n10, q11 = -n11;
        var turned = Math.Atan2(-q01, q00);
        var s1 = Math.Sqrt(q00 * q00 + q01 * q01);
        var s2 = q10 * Math.Sin(turned) + q11 * Math.Cos(turned);
        if (s1 < 1e-9 || Math.Abs(s2) < 1e-9) return this;
        var rotation = -turned * 180 / Math.PI;
        rotation = Math.Round(rotation <= -180 ? rotation + 360 : rotation > 180 ? rotation - 360 : rotation, 6);
        var (xpan, ypan) = (px / s1, py / s2);
        if (size is (> 0, > 0) and var (w, h))
        {
            (xpan, ypan) = (Within(xpan, w / s1), Within(ypan, h / Math.Abs(s2)));
        }

        return new FlatTransform(Tidy(xpan), Tidy(ypan), rotation == -0.0 ? 0 : rotation, Tidy(s1), Tidy(s2));

        static double Within(double v, double period)
        {
            var r = v % period;
            if (r < 0) r += period;
            return Math.Abs(r - period) < 1e-6 ? 0 : r;
        }

        static double Tidy(double v)
        {
            var r = Math.Round(v * 65536) / 65536;
            return r == -0.0 ? 0 : r;
        }
    }

    /// <summary>The same for an area on the map (its own fields, on the UI thread).</summary>
    public static FlatTransform Of(Sector s, bool ceiling) => Of([.. s.Extra], ceiling);
}
