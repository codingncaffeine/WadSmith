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
    /// through <paramref name="edit"/>: <paramref name="steps"/> of them when given, else as many as climb comfortably;
    /// running along the rectangle's longer side unless <paramref name="alongX"/> says. Returns the steps, or none and why.
    /// </summary>
    public static (IReadOnlyList<Sector> Steps, string? Why) Make(StructureEdit edit, MapGeometry before, MapPoint from, MapPoint to, Look look, int? steps = null, bool? alongX = null)
    {
        var runsX = alongX ?? Math.Abs(to.X - from.X) >= Math.Abs(to.Y - from.Y);
        var (start, end) = runsX ? (from.X, to.X) : (from.Y, to.Y);
        var (side0, side1) = runsX ? (Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y)) : (Math.Min(from.X, to.X), Math.Max(from.X, to.X));
        var length = Math.Abs(end - start);
        var dir = Math.Sign(end - start);
        if (length < 2 * MinDepth || side1 - side0 < MinDepth) return ([], "Drag a longer rectangle for stairs: from where the bottom step starts to where the top one ends.");

        MapPoint At(double along, double across) => runsX ? new MapPoint(along, across) : new MapPoint(across, along);
        var middle = (side0 + side1) / 2;

        // The floors the stairs join: just before the first step and just past the last.
        var below = before.AreaAt(At(start - dir * 8, middle)) ?? before.AreaAt(At(start + dir * 8, middle));
        var above = before.AreaAt(At(end + dir * 8, middle));
        var bottom = below?.FloorHeight ?? look.FloorHeight;
        int count;
        double rise;
        if (steps is { } wanted && wanted >= 1)
        {
            if (length / wanted < MinDepth) return ([], string.Create(CultureInfo.InvariantCulture, $"{wanted} steps would each be under {MinDepth:0} deep in {length:0}; the most that fit is {Math.Max(1, (int)Math.Floor(length / MinDepth))}."));
            count = wanted;
            rise = above is { } high && high.FloorHeight != bottom ? (high.FloorHeight - bottom) / (count + 1) : ComfortRise;
            if (Math.Abs(rise) > MaxRise) return ([], string.Create(CultureInfo.InvariantCulture, $"{wanted} steps would each rise {Math.Abs(rise):0}, more than the player can climb ({MaxRise:0}); it needs at least {(int)Math.Ceiling(Math.Abs((above?.FloorHeight ?? bottom) - bottom) / MaxRise) - 1}."));
        }
        else if (above is { } top && top.FloorHeight != bottom)
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
            var position = points.Average(v => runsX ? v.X : v.Y);
            var step = Math.Clamp((int)Math.Floor((position - start) * dir / depth), 0, count - 1);
            area.FloorHeight = Math.Round(bottom + (step + 1) * rise);
            area.CeilingHeight = Math.Max(area.CeilingHeight, area.FloorHeight + Headroom);
        }

        return (made, null);
    }
}

/// <summary>
/// A staircase found in the map (D21): steps (areas the player stands in, each with a floor no more than 24 below it on
/// one side and no more than 24 above on another), joined into one flight; pieces of one step at one height count once.
/// </summary>
/// <param name="Steps">Every area of the flight, lowest step first.</param>
/// <param name="Heights">The steps' floor heights, lowest first: one a step.</param>
/// <param name="Below">The floor the flight climbs from: the lowest floor a step down from its lowest step.</param>
/// <param name="Above">The floor it climbs to: the highest floor a step up from its highest step (a ledge too high to climb is not where it goes).</param>
public sealed record Staircase(IReadOnlyList<Sector> Steps, IReadOnlyList<double> Heights, double Below, double Above)
{
    public int Count => Heights.Count;

    /// <summary>How much each step rises on average, counting the climb from below and onto the top: the rise that evens them out.</summary>
    public double Rise => (Above - Below) / (Count + 1);

    /// <summary>Every rise climbing the flight: onto the first step, each next step, and onto the floor above.</summary>
    public IReadOnlyList<double> Rises => [Heights[0] - Below, .. Heights.Zip(Heights.Skip(1), (a, b) => b - a), Above - Heights[^1]];

    /// <summary>The rise of the first step: what every step rises once the rise is set.</summary>
    public double StepRise => Heights[0] - Below;

    /// <summary>Whether every rise is the same, onto the top too.</summary>
    public bool Even => Rises.Distinct().Count() == 1;

    /// <summary>The climb in words: "from 0 to 64, 16 a step", "…, 20 a step and 4 onto the top", or "…, steps of 8 to 24".</summary>
    public string Describe()
    {
        var steps = Rises.Take(Count).Distinct().Count() == 1;
        var how = Even ? $"{StepRise:0} a step" : steps ? $"{StepRise:0} a step and {Rises[^1]:0} onto the top" : $"steps of {Rises.Min():0} to {Rises.Max():0}";
        return string.Create(CultureInfo.InvariantCulture, $"climbing from {Below:0} to {Above:0}, {how}");
    }
}

/// <summary>Recognising stairs, and changing how they climb.</summary>
public static class StairsFinder
{
    /// <summary>The staircase <paramref name="area"/> is a step of, or null (two steps at least).</summary>
    public static Staircase? Recognise(MapGeometry geometry, Sector area)
    {
        if (!IsStep(geometry, area)) return null;
        var flight = new HashSet<Sector>(ReferenceEqualityComparer.Instance) { area };
        var queue = new Queue<Sector>([area]);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            foreach (var n in Beside(geometry, s))
            {
                if (!flight.Contains(n) && Math.Abs(n.FloorHeight - s.FloorHeight) <= Stairs.MaxRise && IsStep(geometry, n) && flight.Add(n)) queue.Enqueue(n);
            }
        }

        var heights = flight.Select(s => s.FloorHeight).Distinct().Order().ToList();
        if (heights.Count < 2) return null;
        var lowest = flight.Where(s => s.FloorHeight == heights[0]).ToList();
        var highest = flight.Where(s => s.FloorHeight == heights[^1]).ToList();
        var below = lowest.SelectMany(s => Beside(geometry, s)).Where(n => !flight.Contains(n) && heights[0] - n.FloorHeight is > 0 and <= Stairs.MaxRise).Select(n => n.FloorHeight).DefaultIfEmpty(heights[0]).Min();
        var above = highest.SelectMany(s => Beside(geometry, s)).Where(n => !flight.Contains(n) && n.FloorHeight - heights[^1] is > 0 and <= Stairs.MaxRise).Select(n => n.FloorHeight).DefaultIfEmpty(heights[^1]).Max();
        return new Staircase([.. flight.OrderBy(s => s.FloorHeight).ThenBy(s => s.Slot)], heights, below, above);
    }

    /// <summary>
    /// Sets the steps' floors through <paramref name="edit"/>: the lowest step <paramref name="rise"/> above the floor below,
    /// each next one <paramref name="rise"/> higher (pieces of one step together), keeping 72 of headroom over each.
    /// </summary>
    public static void SetRise(StructureEdit edit, Staircase stairs, double rise)
    {
        for (var i = 0; i < stairs.Heights.Count; i++)
        {
            var height = Math.Round(stairs.Below + (i + 1) * rise);
            foreach (var step in stairs.Steps.Where(s => s.FloorHeight == stairs.Heights[i]).ToList())
            {
                edit.Set(step, s => s.FloorHeight, (s, v) => s.FloorHeight = v, height);
                if (step.CeilingHeight < height + Stairs.Headroom) edit.Set(step, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, height + Stairs.Headroom);
            }
        }
    }

    /// <summary>
    /// Where a straight flight lies, when its steps together fill one rectangle square to the map's axes: its bottom and top
    /// corners as the Stairs tool takes them, and whether it climbs along the map's x; null for any other shape.
    /// </summary>
    public static (MapPoint From, MapPoint To, bool AlongX)? Run(MapGeometry geometry, Staircase flight)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, area = 0;
        foreach (var step in flight.Steps)
        {
            if (geometry.Loops(step) is not { Count: 1 } loops) return null;
            var corners = Corners(loops[0]);
            if (corners.Count != 4 || !corners.Zip(corners.Skip(1).Append(corners[0])).All(e => e.First.X == e.Second.X || e.First.Y == e.Second.Y)) return null;
            var (ax, ay, bx, by) = (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
            area += (bx - ax) * (by - ay);
            (x0, y0, x1, y1) = (Math.Min(x0, ax), Math.Min(y0, ay), Math.Max(x1, bx), Math.Max(y1, by));
        }

        if (Math.Abs(area - (x1 - x0) * (y1 - y0)) > 1) return null;
        MapPoint Middle(double height)
        {
            var at = flight.Steps.Where(s => s.FloorHeight == height).SelectMany(s => geometry.Loops(s)[0]).ToList();
            return new MapPoint(at.Average(p => p.X), at.Average(p => p.Y));
        }

        var (low, high) = (Middle(flight.Heights[0]), Middle(flight.Heights[^1]));
        var alongX = Math.Abs(high.X - low.X) >= Math.Abs(high.Y - low.Y);
        return alongX
            ? (new MapPoint(low.X < high.X ? x0 : x1, y0), new MapPoint(low.X < high.X ? x1 : x0, y1), true)
            : (new MapPoint(x0, low.Y < high.Y ? y0 : y1), new MapPoint(x1, low.Y < high.Y ? y1 : y0), false);

        // An outline's corners, without the points that lie along a straight wall.
        static List<MapPoint> Corners(MapPoint[] loop)
        {
            var kept = new List<MapPoint>();
            for (var i = 0; i < loop.Length; i++)
            {
                var (a, b, c) = (loop[(i + loop.Length - 1) % loop.Length], loop[i], loop[(i + 1) % loop.Length]);
                if (Math.Abs((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X)) > 1e-9) kept.Add(b);
            }

            return kept;
        }
    }

    private static bool IsStep(MapGeometry geometry, Sector s)
    {
        if (!s.OnMap || s.CeilingHeight - s.FloorHeight < 56 || Lifts.Recognise(geometry, s) is not null) return false;
        var beside = Beside(geometry, s).ToList();
        return beside.Any(n => s.FloorHeight - n.FloorHeight is > 0 and <= Stairs.MaxRise) && beside.Any(n => n.FloorHeight - s.FloorHeight is > 0 and <= Stairs.MaxRise);
    }

    /// <summary>The areas across the area's two-sided walls that do not block.</summary>
    private static IEnumerable<Sector> Beside(MapGeometry geometry, Sector s)
    {
        if (geometry.WallBoundsAt(s.Slot) is not { } b) yield break;
        foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            if (!l.OnMap || l.Back is null || l.Flag("blocking")) continue;
            if (ReferenceEquals(l.Front.Sector, s) && !ReferenceEquals(l.Back.Sector, s)) yield return l.Back.Sector;
            else if (ReferenceEquals(l.Back.Sector, s) && !ReferenceEquals(l.Front.Sector, s)) yield return l.Front.Sector;
        }
    }
}
