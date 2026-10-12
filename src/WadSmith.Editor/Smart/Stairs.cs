using System.Globalization;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// Stairs drawn as a rectangle from the bottom step to the top one: the steps run along the drag's longer
/// side, each an area of its own, climbing from the floor of the room before the first step to the floor
/// of the room past the last (or 16 a step where there is none), never more than the player can climb.
/// </summary>
public static class Stairs
{
    /// <summary>The highest step the player walks up (the engine's rule).</summary>
    public const double MaxRise = 24;

    /// <summary>The rise of a comfortable step, and the most a step rises where it can.</summary>
    public const double ComfortRise = 16;

    /// <summary>The shallowest step the tool makes, and the depth of a step where nothing else decides.</summary>
    public const double MinDepth = 16;
    public const double DefaultDepth = 32;

    /// <summary>The headroom kept over every step.</summary>
    public const double Headroom = 72;

    /// <summary>
    /// Makes stairs in the rectangle from <paramref name="from"/> (the bottom) to <paramref name="to"/> (the top)
    /// through <paramref name="edit"/>. Returns the steps, or none and why.
    /// </summary>
    public static (IReadOnlyList<Sector> Steps, string? Why) Make(StructureEdit edit, MapGeometry before, MapPoint from, MapPoint to, Look look)
    {
        var alongX = Math.Abs(to.X - from.X) >= Math.Abs(to.Y - from.Y);
        var (start, end) = alongX ? (from.X, to.X) : (from.Y, to.Y);
        var (side0, side1) = alongX ? (Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y)) : (Math.Min(from.X, to.X), Math.Max(from.X, to.X));
        var length = Math.Abs(end - start);
        var dir = Math.Sign(end - start);
        if (length < 2 * MinDepth || side1 - side0 < MinDepth) return ([], "Drag a longer rectangle for stairs: from where the bottom step starts to where the top one ends.");

        MapPoint At(double along, double across) => alongX ? new MapPoint(along, across) : new MapPoint(across, along);
        var middle = (side0 + side1) / 2;

        // The floors the stairs join: just before the first step and just past the last.
        var below = before.AreaAt(At(start - dir * 8, middle)) ?? before.AreaAt(At(start + dir * 8, middle));
        var above = before.AreaAt(At(end + dir * 8, middle));
        var bottom = below?.FloorHeight ?? look.FloorHeight;
        int count;
        double rise;
        if (above is { } top && top.FloorHeight != bottom)
        {
            var total = top.FloorHeight - bottom;
            count = Math.Max(1, (int)Math.Ceiling(Math.Abs(total) / ComfortRise) - 1);
            count = Math.Min(count, Math.Max(1, (int)Math.Floor(length / MinDepth)));
            rise = total / (count + 1);
            if (Math.Abs(rise) > MaxRise) return ([], string.Create(CultureInfo.InvariantCulture, $"These stairs would need steps of {Math.Abs(rise):0}, more than the player can climb ({MaxRise:0}). Make them longer."));
        }
        else
        {
            count = Math.Max(2, (int)Math.Round(length / DefaultDepth));
            rise = ComfortRise;
        }

        var depth = length / count;
        var cuts = new List<(MapPoint, MapPoint)>();
        for (var i = 1; i < count; i++) cuts.Add((At(start + dir * depth * i, side0), At(start + dir * depth * i, side1)));
        MapPoint[] outline = [At(start, side0), At(end, side0), At(end, side1), At(start, side1)];
        var made = ShapeDrawing.Draw(edit, before, outline, look, cuts, look.Step);

        // Each new area takes the height of the step it lies in, found from where its walls are.
        var walls = edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct().ToList();
        foreach (var area in made)
        {
            var points = walls.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area))).SelectMany(l => new[] { l.Start, l.End }).ToList();
            if (points.Count == 0) continue;
            var position = points.Average(v => alongX ? v.X : v.Y);
            var step = Math.Clamp((int)Math.Floor((position - start) * dir / depth), 0, count - 1);
            area.FloorHeight = Math.Round(bottom + (step + 1) * rise);
            area.CeilingHeight = Math.Max(area.CeilingHeight, area.FloorHeight + Headroom);
        }

        return (made, null);
    }
}
