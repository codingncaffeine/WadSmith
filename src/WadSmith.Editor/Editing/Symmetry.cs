using WadSmith.Editor.Geometry;

namespace WadSmith.Editor.Editing;

/// <summary>How what is drawn is repeated about the middle (deathmatch arenas are built so).</summary>
public enum MirrorMode
{
    Off,

    /// <summary>Mirrored across the up-and-down line through the middle.</summary>
    LeftRight,

    /// <summary>Mirrored across the side-to-side line through the middle.</summary>
    TopBottom,

    /// <summary>Mirrored both ways: four copies.</summary>
    FourWays,

    /// <summary>Turned half way round the middle: two copies, each the other seen from the far side.</summary>
    HalfTurn,

    /// <summary>Turned a quarter at a time round the middle: four copies.</summary>
    QuarterTurns,
}

/// <summary>
/// Symmetry mode (§3.4): rooms drawn and objects placed are repeated about <see cref="Centre"/> as the mode says, the
/// copies drawn with the original as one undo step; objects face as their image would.
/// </summary>
public sealed record Mirror(MirrorMode Mode, MapPoint Centre)
{
    public static Mirror Off { get; } = new(MirrorMode.Off, default);

    public bool IsOn => Mode != MirrorMode.Off;

    /// <summary>What becomes of a point in each copy (the original not among them).</summary>
    public IReadOnlyList<MapPoint> Images(MapPoint p)
    {
        var (dx, dy) = (p.X - Centre.X, p.Y - Centre.Y);
        MapPoint At(double x, double y) => new(Centre.X + x, Centre.Y + y);
        return Mode switch
        {
            MirrorMode.LeftRight => [At(-dx, dy)],
            MirrorMode.TopBottom => [At(dx, -dy)],
            MirrorMode.FourWays => [At(-dx, dy), At(dx, -dy), At(-dx, -dy)],
            MirrorMode.HalfTurn => [At(-dx, -dy)],
            MirrorMode.QuarterTurns => [At(-dy, dx), At(-dx, -dy), At(dy, -dx)],
            _ => [],
        };
    }

    /// <summary>How an object facing <paramref name="angle"/> faces in each copy, in the order of <see cref="Images"/>.</summary>
    public IReadOnlyList<int> Facings(int angle)
    {
        static int Norm(int a) => ((a % 360) + 360) % 360;
        return Mode switch
        {
            MirrorMode.LeftRight => [Norm(180 - angle)],
            MirrorMode.TopBottom => [Norm(-angle)],
            MirrorMode.FourWays => [Norm(180 - angle), Norm(-angle), Norm(angle + 180)],
            MirrorMode.HalfTurn => [Norm(angle + 180)],
            MirrorMode.QuarterTurns => [Norm(angle + 90), Norm(angle + 180), Norm(angle + 270)],
            _ => [],
        };
    }

    /// <summary>
    /// The copies of a shape, each a list of corners; a copy that lands on the shape itself or on another copy (a room drawn
    /// across the middle, already symmetric) is left out.
    /// </summary>
    public IReadOnlyList<MapPoint[]> Shapes(IReadOnlyList<MapPoint> shape)
    {
        var copies = new List<MapPoint[]>();
        var seen = new List<HashSet<MapPoint>> { new(shape) };
        for (var i = 0; i < Images(default).Count; i++)
        {
            var copy = shape.Select(p => Images(p)[i]).ToArray();
            var set = new HashSet<MapPoint>(copy);
            if (seen.Any(s => s.SetEquals(set))) continue;
            seen.Add(set);
            copies.Add(copy);
        }

        return copies;
    }

    /// <summary>The lines to show on the plan: the mirror's axes, or for turns the middle (as two short crossing lines).</summary>
    public IReadOnlyList<(MapPoint A, MapPoint B)> Axes(double reach)
    {
        var (x, y) = (Centre.X, Centre.Y);
        return Mode switch
        {
            MirrorMode.LeftRight => [(new(x, y - reach), new(x, y + reach))],
            MirrorMode.TopBottom => [(new(x - reach, y), new(x + reach, y))],
            MirrorMode.FourWays => [(new(x, y - reach), new(x, y + reach)), (new(x - reach, y), new(x + reach, y))],
            MirrorMode.HalfTurn or MirrorMode.QuarterTurns => [(new(x - 24, y), new(x + 24, y)), (new(x, y - 24), new(x, y + 24))],
            _ => [],
        };
    }
}
