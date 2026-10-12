using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>Which edge or middle line things are lined up on.</summary>
public enum LineUp
{
    Left,
    Centre,
    Right,
    Bottom,
    Middle,
    Top,
}

/// <summary>Which way things in a ring face.</summary>
public enum RingFacing
{
    /// <summary>As they faced before.</summary>
    AsTheyWere,

    /// <summary>Toward the middle of the ring: monsters closing in.</summary>
    Inward,

    /// <summary>Away from the middle: guards looking out.</summary>
    Outward,
}

/// <summary>
/// Placing several objects at once, through an edit (one undo step): lined up, spaced evenly, in a ring, in a grid
/// ("eight imps in a circle"). Spots land on whole units, as the games keep them.
/// </summary>
public static class Arranging
{
    /// <summary>Lines the things up on the leftmost, middle or rightmost of their spots (or bottom, middle, top).</summary>
    public static void Line(StructureEdit edit, IReadOnlyList<Thing> things, LineUp on)
    {
        if (things.Count < 2) return;
        var across = on is LineUp.Left or LineUp.Centre or LineUp.Right;
        var values = things.Select(t => across ? t.X : t.Y).ToList();
        var to = Math.Round(on switch
        {
            LineUp.Left or LineUp.Bottom => values.Min(),
            LineUp.Right or LineUp.Top => values.Max(),
            _ => (values.Min() + values.Max()) / 2,
        });
        foreach (var t in things) Place(edit, t, across ? to : t.X, across ? t.Y : to);
    }

    /// <summary>
    /// Spaces the things evenly between the two farthest apart, across (left to right) or up and down, keeping their order.
    /// </summary>
    public static void Spread(StructureEdit edit, IReadOnlyList<Thing> things, bool across)
    {
        if (things.Count < 3) return;
        var ordered = things.OrderBy(t => across ? t.X : t.Y).ThenBy(t => across ? t.Y : t.X).ToList();
        var (first, last) = (across ? ordered[0].X : ordered[0].Y, across ? ordered[^1].X : ordered[^1].Y);
        for (var i = 1; i < ordered.Count - 1; i++)
        {
            var at = Math.Round(first + (last - first) * i / (ordered.Count - 1));
            Place(edit, ordered[i], across ? at : ordered[i].X, across ? ordered[i].Y : at);
        }
    }

    /// <summary>The middle of the things' spots, how far the farthest is from it, and how far they are on average.</summary>
    public static (MapPoint Centre, double Radius, double Mean) Spread(IReadOnlyList<Thing> things)
    {
        var (cx, cy) = ((things.Min(t => t.X) + things.Max(t => t.X)) / 2, (things.Min(t => t.Y) + things.Max(t => t.Y)) / 2);
        var reach = things.Select(t => Math.Sqrt((t.X - cx) * (t.X - cx) + (t.Y - cy) * (t.Y - cy))).ToList();
        return (new MapPoint(cx, cy), reach.Max(), reach.Average());
    }

    /// <summary>
    /// The reach a new ring of the things gets: how far they are from their middle on average (a ring through the farthest
    /// would carry the others out past it), and at least 64 between neighbours; in steps of 8.
    /// </summary>
    public static double RingReach(IReadOnlyList<Thing> things) => Math.Max(64, Math.Ceiling(Math.Max(Spread(things).Mean, 32 * things.Count / Math.PI) / 8) * 8);

    /// <summary>
    /// Puts the things evenly round a ring of <paramref name="radius"/> about <paramref name="centre"/>, in the order they
    /// already go round it, the first due east of the middle, facing as asked.
    /// </summary>
    public static void Ring(StructureEdit edit, IReadOnlyList<Thing> things, MapPoint centre, double radius, RingFacing facing)
    {
        if (things.Count == 0) return;
        var ordered = things.OrderBy(t => Bearing(centre, t)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var a = 2 * Math.PI * i / ordered.Count;
            Place(edit, ordered[i], Math.Round(centre.X + radius * Math.Cos(a)), Math.Round(centre.Y + radius * Math.Sin(a)));
            if (facing == RingFacing.AsTheyWere) continue;
            var degrees = (int)Math.Round(a * 180 / Math.PI) + (facing == RingFacing.Inward ? 180 : 0);
            var angle = ((degrees % 360) + 360) % 360;
            edit.Set(ordered[i], t => t.Angle, (t, v) => t.Angle = v, angle);
        }
    }

    /// <summary>
    /// Puts the things in rows of <paramref name="columns"/>, <paramref name="spacing"/> apart, from the top left of where
    /// they are, in reading order: top row first, left to right, where things less than half a spacing below the top of a row
    /// count as in it.
    /// </summary>
    public static void Grid(StructureEdit edit, IReadOnlyList<Thing> things, int columns, double spacing)
    {
        if (things.Count == 0 || columns < 1) return;
        var (left, top) = (Math.Round(things.Min(t => t.X)), Math.Round(things.Max(t => t.Y)));
        var ordered = new List<Thing>();
        var rest = things.OrderByDescending(t => t.Y).ToList();
        while (rest.Count > 0)
        {
            var rowTop = rest[0].Y;
            List<Thing> row = [rest[0], .. rest.Skip(1).Where(t => t.Y > rowTop - spacing / 2)];
            ordered.AddRange(row.OrderBy(t => t.X));
            rest = [.. rest.Except(row)];
        }

        for (var i = 0; i < ordered.Count; i++) Place(edit, ordered[i], left + (i % columns) * spacing, top - (i / columns) * spacing);
    }

    /// <summary>The angle from east, counter-clockwise, 0 to 2π, of a thing seen from the middle.</summary>
    private static double Bearing(MapPoint centre, Thing t)
    {
        var a = Math.Atan2(t.Y - centre.Y, t.X - centre.X);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    private static void Place(StructureEdit edit, Thing t, double x, double y)
    {
        if (t.X != x) edit.Set(t, e => e.X, (e, v) => e.X = v, x);
        if (t.Y != y) edit.Set(t, e => e.Y, (e, v) => e.Y = v, y);
    }
}
