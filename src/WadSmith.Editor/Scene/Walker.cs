using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;

namespace WadSmith.Editor.Scene;

/// <summary>
/// Walking through a map as the player does, for the 3D view's Walk camera: eye height 41 above the
/// floor, steps up to 24, never into the void, through a solid or impassable wall, or under a
/// ceiling lower than the player (56). Drops to lower floors at once. Solid platforms are floors to
/// stand on or ceilings to fit under; water and passable ones are walked through.
/// </summary>
public static class Walker
{
    public const double EyeHeight = 41;
    public const double StepHeight = 24;
    public const double PlayerHeight = 56;

    /// <summary>The eye height at <paramref name="to"/> when the player standing at <paramref name="from"/> with feet at <paramref name="feet"/> can walk there; null when something stops them.</summary>
    public static double? Step(MapGeometry geometry, MapPoint from, double feet, MapPoint to)
    {
        if (geometry.SectorAt(to) is not { } sector || sector.Slot >= geometry.Frame.Sectors.Count) return null;
        var area = geometry.Frame.Sectors[sector.Slot];
        var (floor, ceiling) = (SlopePlane.FloorAt(area, to), SlopePlane.CeilingAt(area, to));

        // A solid platform whose top is within a step of the feet is stood on (the highest such); one higher is a ceiling.
        foreach (var p in geometry.PlatformsIn(sector.Slot))
        {
            if (!p.Solid) continue;
            if (p.Top <= feet + StepHeight) floor = Math.Max(floor, p.Top);
            else ceiling = Math.Min(ceiling, p.Bottom);
        }

        if (floor > feet + StepHeight || ceiling - floor < PlayerHeight) return null;
        foreach (var wall in geometry.WallsCrossing(from, to))
        {
            if (wall.Back < 0 || Blocking(wall.Extra)) return null;
        }

        return floor + EyeHeight;
    }

    private static bool Blocking(KeyValuePair<string, UdmfValue>[] extra)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, "blocking", StringComparison.OrdinalIgnoreCase)) return v.Kind == UdmfValueKind.Boolean && v.AsBool();
        }

        return false;
    }
}
