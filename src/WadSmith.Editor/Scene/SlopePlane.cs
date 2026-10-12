using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Scene;

/// <summary>
/// A sloped floor or ceiling as GZDoom reads it from an area's UDMF fields (<c>floorplane_a</c> … <c>_d</c>,
/// <c>ceilingplane_a</c> … <c>_d</c>): the plane a·x + b·y + c·z + d = 0, set only when all four are given. The area's
/// own height still pegs its wall textures.
/// </summary>
public readonly record struct SlopePlane(double A, double B, double C, double D)
{
    /// <summary>The plane's height at a point.</summary>
    public double Z(double x, double y) => -(A * x + B * y + D) / C;

    public static SlopePlane? Floor(KeyValuePair<string, UdmfValue>[] extra) => Read(n => Find(extra, n), "floorplane_");

    public static SlopePlane? Ceiling(KeyValuePair<string, UdmfValue>[] extra) => Read(n => Find(extra, n), "ceilingplane_");

    public static SlopePlane? Floor(Sector s) => Read(n => s.Extra.TryGet(n, out var v) ? v : (UdmfValue?)null, "floorplane_");

    public static SlopePlane? Ceiling(Sector s) => Read(n => s.Extra.TryGet(n, out var v) ? v : (UdmfValue?)null, "ceilingplane_");

    /// <summary>An area's floor height at a point: its plane's when sloped, else its height.</summary>
    public static double FloorAt(SectorFrame area, MapPoint p) => Floor(area.Extra)?.Z(p.X, p.Y) ?? area.FloorHeight;

    /// <summary>An area's ceiling height at a point: its plane's when sloped, else its height.</summary>
    public static double CeilingAt(SectorFrame area, MapPoint p) => Ceiling(area.Extra)?.Z(p.X, p.Y) ?? area.CeilingHeight;

    /// <summary>
    /// The plane at height <paramref name="h0"/> along the line through <paramref name="p0"/> with <paramref name="normal"/>
    /// (a unit vector pointing into the room), changing by <paramref name="rise"/> per unit of distance along it. A floor's
    /// normal points up (c = 1), a ceiling's down (c = −1), as the engine expects.
    /// </summary>
    public static SlopePlane Rising(MapPoint p0, (double X, double Y) normal, double h0, double rise, bool ceiling)
    {
        var d = h0 - rise * (normal.X * p0.X + normal.Y * p0.Y);
        return ceiling ? new SlopePlane(rise * normal.X, rise * normal.Y, -1, d) : new SlopePlane(-rise * normal.X, -rise * normal.Y, 1, -d);
    }

    /// <summary>The four fields for the floor or the ceiling.</summary>
    public IEnumerable<(string Name, UdmfValue Value)> Fields(bool ceiling)
    {
        var prefix = ceiling ? "ceilingplane_" : "floorplane_";
        yield return (prefix + "a", UdmfValue.Float(A));
        yield return (prefix + "b", UdmfValue.Float(B));
        yield return (prefix + "c", UdmfValue.Float(C));
        yield return (prefix + "d", UdmfValue.Float(D));
    }

    public static IEnumerable<string> FieldNames(bool ceiling) => new[] { "a", "b", "c", "d" }.Select(n => (ceiling ? "ceilingplane_" : "floorplane_") + n);

    private static SlopePlane? Read(Func<string, UdmfValue?> get, string prefix)
    {
        double? Number(string name) => get(prefix + name) is { } v && v.Kind is UdmfValueKind.Float or UdmfValueKind.Integer ? v.AsFloat() : null;
        if (Number("a") is not { } a || Number("b") is not { } b || Number("c") is not { } c || Number("d") is not { } d || Math.Abs(c) < 1e-9) return null;
        return new SlopePlane(a, b, c, d);
    }

    private static UdmfValue? Find(KeyValuePair<string, UdmfValue>[] extra, string name)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return null;
    }
}
