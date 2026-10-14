using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Disconnecting at a corner (6c, Eureka's Disconnect): rooms that touch only at a corner each get a corner of their own, pulled a
/// few units into its rooms so they no longer meet. Going round the corner, the walls fall into groups split by open space; the
/// first group keeps the corner and each other one gets its own. A corner inside one connected area is left as it is: splitting
/// it would open the rooms round it, which WadSmith never does.
/// </summary>
public static class Disconnecting
{
    /// <summary>How far a new corner is pulled into its rooms.</summary>
    public const double Pull = 4;

    /// <summary>Disconnects the rooms that meet at <paramref name="corner"/>; returns how many corners were made, or why none could be.</summary>
    public static (int Made, string? WhyNot) Corner(StructureEdit edit, Vertex corner)
    {
        var walls = edit.Map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Start, corner) || ReferenceEquals(l.End, corner))).ToList();
        if (walls.Count < 2) return (0, "Only one wall ends at this corner: there is nothing to disconnect.");

        // Each wall by the direction it leaves the corner in, anticlockwise from east.
        var round = walls.Select(w =>
        {
            var other = ReferenceEquals(w.Start, corner) ? w.End : w.Start;
            return (Wall: w, Angle: Math.Atan2(other.Y - corner.Y, other.X - corner.X));
        }).OrderBy(x => x.Angle).ToList();

        // The space anticlockwise of a wall (between it and the next one round): the wall's side that faces it, or open space.
        // A line's front faces right of its direction, so a wall leaving the corner at its start has its back that way.
        bool OpenAfter(int i)
        {
            var w = round[i].Wall;
            return ReferenceEquals(w.Start, corner) ? w.Back is null : w.Front is null;
        }

        var cuts = Enumerable.Range(0, round.Count).Where(OpenAfter).ToList();
        if (cuts.Count < 2) return (0, "The walls at this corner close one area round it: disconnecting them would open its rooms. Move or reshape the rooms instead.");

        // The groups between open spaces, each from the wall after a cut to the next cut; the first keeps the corner.
        var made = 0;
        for (var g = 1; g < cuts.Count; g++)
        {
            var group = new List<(Line Wall, double Angle)>();
            for (var i = (cuts[g - 1] + 1) % round.Count; ; i = (i + 1) % round.Count)
            {
                group.Add(round[i]);
                if (i == cuts[g]) break;
            }

            // Pulled along the middle of the group's walls, into its rooms.
            var (sx, sy) = (group.Sum(x => Math.Cos(x.Angle)), group.Sum(x => Math.Sin(x.Angle)));
            var length = Math.Sqrt(sx * sx + sy * sy);
            var (dx, dy) = length < 1e-9 ? (0.0, 0.0) : (sx / length * Pull, sy / length * Pull);
            var own = edit.Add(new Vertex { X = Math.Round(corner.X + dx), Y = Math.Round(corner.Y + dy) });
            foreach (var (wall, _) in group)
            {
                if (ReferenceEquals(wall.Start, corner)) edit.Set(wall, x => x.Start, (x, v) => x.Start = v, own);
                else edit.Set(wall, x => x.End, (x, v) => x.End = v, own);
            }

            made++;
        }

        return (made, null);
    }
}
