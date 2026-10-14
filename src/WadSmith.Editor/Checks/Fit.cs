using System.Globalization;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Checks;

/// <summary>
/// Where the player stands in a map's frame (D20): the box the game moves (32 by 32, square to the map's axes),
/// at the pointer, and where a passage is too narrow or too low for it.
/// </summary>
/// <param name="Inside">Whether the point is in an area at all (the ghost is shown only there).</param>
/// <param name="Fits">Whether the player could stand there.</param>
/// <param name="Floor">The floor the player would stand on: the highest under the box.</param>
/// <param name="Ceiling">The lowest ceiling over the box.</param>
/// <param name="Why">Why the player fits or not, in a sentence.</param>
public readonly record struct GhostFit(bool Inside, bool Fits, double Floor, double Ceiling, string Why);

/// <summary>
/// A gap between two walls of one area that the player cannot pass, though there is room to walk on both sides of it.
/// </summary>
/// <param name="Area">The area's slot.</param>
/// <param name="WallA">One wall's slot.</param>
/// <param name="WallB">The other wall's slot.</param>
/// <param name="Width">How wide the gap is.</param>
/// <param name="Needed">How wide it must be for the player's square box at this slant (32 square to the axes, 45 on a diagonal).</param>
/// <param name="Band">The gap's outline, four corners, for marking it on the plan.</param>
public sealed record FitGap(int Area, int WallA, int WallB, double Width, double Needed, MapPoint[] Band)
{
    /// <summary>A doorway (an opening in one line between two areas), a corridor (walls beside each other along at least the player's width) or a pinch (a corner or a thin post beside a wall: bars are made so).</summary>
    public GapKind Kind => WallA == WallB ? GapKind.Doorway : Band[0].DistanceTo(Band[1]) >= 2 * Fit.Radius ? GapKind.Corridor : GapKind.Pinch;
}

public enum GapKind
{
    Doorway,
    Corridor,
    Pinch,
}

/// <summary>A wall between two areas the player stands in, with an opening under the player's height.</summary>
public sealed record LowPassage(int Wall, double Opening);

/// <summary>
/// The gaps and low openings a map had before it was changed — a remix's original — so the checks speak only of the ones
/// the user made: the original's are its author's (bars of posts, slits to look through), found the same way.
/// </summary>
public sealed class FitBaseline
{
    private readonly HashSet<(long, long, long)> _gaps = [];
    private readonly HashSet<(long, long, long, long, long)> _low = [];

    private FitBaseline(bool pending) => IsPending = pending;

    /// <summary>No original: every gap and low opening is the user's (a map made in WadSmith).</summary>
    public static FitBaseline None { get; } = new(false);

    /// <summary>The original is still being read: the checks wait rather than speak of the original's.</summary>
    public static FitBaseline Pending { get; } = new(true);

    public bool IsPending { get; }

    /// <summary>What <paramref name="original"/> (the map as it started) has.</summary>
    public static FitBaseline Of(MapGeometry original)
    {
        var b = new FitBaseline(false);
        foreach (var gap in Fit.Gaps(original)) b._gaps.Add(Key(gap));
        foreach (var low in Fit.LowPassages(original)) b._low.Add(Key(low, original.Frame));
        return b;
    }

    /// <summary>Whether the original had this gap (the same place, as wide).</summary>
    public bool Had(FitGap gap) => _gaps.Contains(Key(gap));

    /// <summary>Whether the original had this low opening (the same wall's ends, as high).</summary>
    public bool Had(LowPassage low, MapFrame frame) => _low.Contains(Key(low, frame));

    private static (long, long, long) Key(FitGap g) => ((long)Math.Round(g.Band.Average(c => c.X)), (long)Math.Round(g.Band.Average(c => c.Y)), (long)Math.Round(g.Width * 4));

    private static (long, long, long, long, long) Key(LowPassage low, MapFrame f)
    {
        var l = f.Lines[low.Wall];
        var (a, b) = ((Math.Round(f.Vertices[l.V1].X), Math.Round(f.Vertices[l.V1].Y)), (Math.Round(f.Vertices[l.V2].X), Math.Round(f.Vertices[l.V2].Y)));
        if (a.CompareTo(b) > 0) (a, b) = (b, a);
        return ((long)a.Item1, (long)a.Item2, (long)b.Item1, (long)b.Item2, (long)Math.Round(low.Opening));
    }
}

public static class Fit
{
    /// <summary>Half the player's box (the game's player radius).</summary>
    public const double Radius = 16;

    /// <summary>The player's height: an opening must be at least this tall.</summary>
    public const double Height = 56;

    /// <summary>The widest gap the box can need: across a diagonal.</summary>
    private static readonly double MostNeeded = 2 * Radius * Math.Sqrt(2);

    /// <summary>
    /// How wide a gap in direction (<paramref name="ux"/>, <paramref name="uy"/>) (a unit vector across it) must be: the box
    /// is square to the map's axes, so a slanted passage needs more (32 straight across, 45 on a diagonal).
    /// </summary>
    public static double Needed(double ux, double uy) => 2 * Radius * (Math.Abs(ux) + Math.Abs(uy));

    /// <summary>
    /// Whether the player, walking in the area at <paramref name="p"/>, fits with the box there: no solid wall through it and
    /// no floor in it more than a step (24) above the area's, and room enough over the highest floor under it.
    /// </summary>
    public static GhostFit At(MapGeometry g, MapPoint p)
    {
        var f = g.Frame;
        if (g.SectorAt(p) is not { } s || s.Slot >= f.Sectors.Count) return new GhostFit(false, false, 0, 0, "");
        var area = f.Sectors[s.Slot];
        var (floor, ceiling) = (area.FloorHeight, area.CeilingHeight);
        var (x0, y0, x1, y1) = (p.X - Radius, p.Y - Radius, p.X + Radius, p.Y + Radius);
        var wall = false;
        var ledge = 0.0;
        foreach (var i in g.LineSlotsIn(x0, y0, x1, y1))
        {
            var l = f.Lines[i];
            if (l.Element is null) continue;
            var (a, b) = Ends(f, l);
            if (!CrossesBox(a, b, x0, y0, x1, y1)) continue;
            if (l.Back < 0 || Blocking(l.Extra))
            {
                wall = true;
                continue;
            }

            foreach (var side in (int[])[l.Front, l.Back])
            {
                if (f.Sides[side].Sector is var sec and >= 0 && sec < f.Sectors.Count)
                {
                    // A closed area beside (a wall between two rooms, its ceiling on its floor) is a wall, not a low room.
                    if (f.Sectors[sec].CeilingHeight <= f.Sectors[sec].FloorHeight && sec != s.Slot)
                    {
                        wall = true;
                        continue;
                    }

                    var rise = f.Sectors[sec].FloorHeight - area.FloorHeight;
                    if (rise > Stairs.MaxRise) ledge = Math.Max(ledge, rise);
                    else floor = Math.Max(floor, f.Sectors[sec].FloorHeight);
                    ceiling = Math.Min(ceiling, f.Sectors[sec].CeilingHeight);
                }
            }
        }

        var room = ceiling - floor;
        if (wall) return new GhostFit(true, false, floor, ceiling, "The player does not fit here: a wall is in the way (the player is 32 across).");
        if (ledge > 0) return new GhostFit(true, false, floor, ceiling, string.Create(CultureInfo.InvariantCulture, $"The player does not fit here: a ledge {ledge:0} up is in the way (the player steps up 24 at most, and is 32 across)."));
        if (room < Height) return new GhostFit(true, false, floor, ceiling, string.Create(CultureInfo.InvariantCulture, $"The player does not fit here: {Math.Max(0, room):0} high, and the player is 56 tall."));
        return new GhostFit(true, true, floor, ceiling, string.Create(CultureInfo.InvariantCulture, $"The player fits here: {room:0} high ≈ {room / Height:0.#} players."));
    }

    /// <summary>
    /// Gaps narrower than the player between two walls of an area the player stands in, each with room to walk at both ends:
    /// between a pillar and a wall, or a corridor drawn too thin (dead ends, a slot capped by a wall, are left out); and
    /// doorways too narrow, where the player fits on both sides but not through.
    /// </summary>
    public static IReadOnlyList<FitGap> Gaps(MapGeometry g)
    {
        var f = g.Frame;
        var doors = DoorAreas(f);
        var found = Doorways(g, f);
        for (var i = 0; i < f.Lines.Count; i++)
        {
            var l = f.Lines[i];
            if (l.Element is null) continue;
            var (a0, a1) = Ends(f, l);
            foreach (var s in Sides(f, l))
            {
                if (!Standable(f, s) || !BlocksFrom(f, l, s, doors)) continue;
                var (minX, minY, maxX, maxY) = (Math.Min(a0.X, a1.X) - MostNeeded, Math.Min(a0.Y, a1.Y) - MostNeeded, Math.Max(a0.X, a1.X) + MostNeeded, Math.Max(a0.Y, a1.Y) + MostNeeded);
                foreach (var j in g.LineSlotsIn(minX, minY, maxX, maxY))
                {
                    if (j <= i) continue;
                    var m = f.Lines[j];
                    if (m.Element is null || m.V1 == l.V1 || m.V1 == l.V2 || m.V2 == l.V1 || m.V2 == l.V2) continue;
                    if (!Sides(f, m).Contains(s) || !BlocksFrom(f, m, s, doors)) continue;
                    var (b0, b1) = Ends(f, m);
                    if (b0 == a0 || b0 == a1 || b1 == a0 || b1 == a1) continue;
                    var (p, q, d) = Closest(a0, a1, b0, b1);
                    if (d < 0.5) continue;
                    var (ux, uy) = ((q.X - p.X) / d, (q.Y - p.Y) / d);
                    var needed = Needed(ux, uy);
                    if (d >= needed - 1e-6) continue;

                    // Two walls in one line (with an opening between them) are a doorway, not a gap: see Doorways.
                    if (InLine(a0, a1, ux, uy) && InLine(b0, b1, ux, uy)) continue;
                    // Along the gap: where both walls run beside each other; its middle is checked (inside the area, no
                    // wall across), and there must be room to walk past each end.
                    var near = new MapPoint((p.X + q.X) / 2, (p.Y + q.Y) / 2);
                    var (tx, ty) = (-uy, ux);
                    double Along(MapPoint v) => (v.X - near.X) * tx + (v.Y - near.Y) * ty;
                    var lo = Math.Max(Math.Min(Along(a0), Along(a1)), Math.Min(Along(b0), Along(b1)));
                    var hi = Math.Min(Math.Max(Along(a0), Along(a1)), Math.Max(Along(b0), Along(b1)));
                    if (lo > hi) (lo, hi) = (0, 0);
                    MapPoint At(double t, double across) => new(near.X + tx * t + ux * across, near.Y + ty * t + uy * across);
                    var mid = At((lo + hi) / 2, 0);

                    // Outside the area (a hole between its walls) the walks below fail too; this is the cheaper test, so it goes first.
                    if (!g.InOrOnArea(s, mid) || Divided(g, f, At((lo + hi) / 2, -d / 2), At((lo + hi) / 2, d / 2), ux, uy, i, j, s, doors)) continue;
                    if (!Open(g, f, mid, At(lo - Radius - 4, 0), doors) || !Open(g, f, mid, At(hi + Radius + 4, 0), doors)) continue;
                    if (hi - lo < 8) (lo, hi) = ((lo + hi) / 2 - 4, (lo + hi) / 2 + 4);
                    found.Add(new FitGap(s, i, j, d, needed, [At(lo, -d / 2), At(hi, -d / 2), At(hi, d / 2), At(lo, d / 2)]));
                }
            }
        }

        // One mark for one narrow place: a corridor's mouths, or a pillar's corner beside its side, lie in the corridor's or
        // the side's own mark (the longest marks are kept).
        var kept = new List<FitGap>();
        foreach (var gap in found.OrderByDescending(x => Length(x.Band)))
        {
            var centre = new MapPoint(gap.Band.Average(c => c.X), gap.Band.Average(c => c.Y));
            if (!kept.Any(k => Within(k.Band, centre))) kept.Add(gap);
        }

        return kept;

        static double Length(MapPoint[] band) => band[0].DistanceTo(band[1]);
        static bool InLine(MapPoint a, MapPoint b, double ux, double uy) => Math.Abs((b.X - a.X) * uy - (b.Y - a.Y) * ux) < 0.05 * a.DistanceTo(b);
    }

    /// <summary>
    /// Openings between two areas (a run of walls in one line between the same two areas, the player passing at least one
    /// way) shorter than the player's box across them, where the box fits just in front of the opening on both sides but not
    /// in it: a doorway drawn too narrow. A corridor's mouth is not one (the box does not fit in the corridor: the corridor
    /// is the gap), nor a slot's.
    /// </summary>
    private static List<FitGap> Doorways(MapGeometry g, MapFrame f)
    {
        var doors = DoorAreas(f);
        var at = new Dictionary<int, List<int>>();
        for (var i = 0; i < f.Lines.Count; i++)
        {
            var l = f.Lines[i];
            if (l.Element is null || l.Back < 0) continue;
            foreach (var v in (int[])[l.V1, l.V2])
            {
                if (!at.TryGetValue(v, out var list)) at[v] = list = [];
                list.Add(i);
            }
        }

        var found = new List<FitGap>();
        var done = new HashSet<int>();
        for (var i = 0; i < f.Lines.Count; i++)
        {
            var l = f.Lines[i];
            if (l.Element is null || l.Back < 0 || done.Contains(i) || Blocking(l.Extra)) continue;
            var (fa, ba) = (f.Sides[l.Front].Sector, f.Sides[l.Back].Sector);
            if (fa < 0 || ba < 0 || fa == ba || !Standable(f, fa) || !Standable(f, ba)) continue;
            if (BlocksFrom(f, l, fa, doors) && BlocksFrom(f, l, ba, doors)) continue;

            // The whole opening: the walls in line with this one between the same two areas.
            var (a, b) = Ends(f, l);
            var (dx, dy, len) = (b.X - a.X, b.Y - a.Y, a.DistanceTo(b));
            if (len == 0) continue;
            var (vx, vy) = (dx / len, dy / len);
            var (lo, hi) = (0.0, len);
            var queue = new Queue<int>([i]);
            done.Add(i);
            while (queue.Count > 0)
            {
                var k = f.Lines[queue.Dequeue()];
                foreach (var v in (int[])[k.V1, k.V2])
                {
                    foreach (var n in at[v])
                    {
                        if (done.Contains(n)) continue;
                        var m = f.Lines[n];
                        var (ma, mb) = (f.Sides[m.Front].Sector, f.Sides[m.Back].Sector);
                        if (!((ma == fa && mb == ba) || (ma == ba && mb == fa)) || Blocking(m.Extra)) continue;
                        var (c, e) = Ends(f, m);
                        if (Math.Abs((c.X - a.X) * vy - (c.Y - a.Y) * vx) > 0.05 || Math.Abs((e.X - a.X) * vy - (e.Y - a.Y) * vx) > 0.05) continue;
                        done.Add(n);
                        queue.Enqueue(n);
                        foreach (var p in (MapPoint[])[c, e])
                        {
                            var t = (p.X - a.X) * vx + (p.Y - a.Y) * vy;
                            (lo, hi) = (Math.Min(lo, t), Math.Max(hi, t));
                        }
                    }
                }
            }

            var width = hi - lo;
            var needed = Needed(vx, vy);
            if (width >= needed - 1e-6) continue;
            var mid = new MapPoint(a.X + vx * (lo + hi) / 2, a.Y + vy * (lo + hi) / 2);
            var (nx, ny) = (vy, -vx);
            var reach = Radius + 1;
            if (!WallInBox(g, f, mid) || WallInBox(g, f, new MapPoint(mid.X + nx * reach, mid.Y + ny * reach)) || WallInBox(g, f, new MapPoint(mid.X - nx * reach, mid.Y - ny * reach))) continue;
            MapPoint P(double t, double across) => new(a.X + vx * t + nx * across, a.Y + vy * t + ny * across);
            found.Add(new FitGap(fa, i, i, width, needed, [P(lo, -4), P(hi, -4), P(hi, 4), P(lo, 4)]));
        }

        return found;
    }

    /// <summary>Whether a solid wall (one-sided, or marked to block) passes through the player's box at <paramref name="p"/>.</summary>
    private static bool WallInBox(MapGeometry g, MapFrame f, MapPoint p)
    {
        var (x0, y0, x1, y1) = (p.X - Radius, p.Y - Radius, p.X + Radius, p.Y + Radius);
        foreach (var i in g.LineSlotsIn(x0, y0, x1, y1))
        {
            var l = f.Lines[i];
            if (l.Element is null || (l.Back >= 0 && !Blocking(l.Extra))) continue;
            var (a, b) = Ends(f, l);
            if (CrossesBox(a, b, x0, y0, x1, y1)) return true;
        }

        return false;
    }

    /// <summary>Whether a point lies in a band's rectangle or within 1 of it.</summary>
    private static bool Within(MapPoint[] band, MapPoint p)
    {
        var (o, along, across) = (band[0], band[1], band[3]);
        var (ax, ay, la) = (along.X - o.X, along.Y - o.Y, o.DistanceTo(along));
        var (cx, cy, lc) = (across.X - o.X, across.Y - o.Y, o.DistanceTo(across));
        if (la == 0 || lc == 0) return false;
        var s = ((p.X - o.X) * ax + (p.Y - o.Y) * ay) / la;
        var t = ((p.X - o.X) * cx + (p.Y - o.Y) * cy) / lc;
        return s >= -1 && s <= la + 1 && t >= -1 && t <= lc + 1;
    }

    /// <summary>
    /// Walls between two areas the player stands in (each at least 56 tall) whose opening — the lower ceiling over the higher
    /// floor — is under 56 but not shut: the player stops there, though both sides look open. Areas that move (doors, lifts,
    /// anything an action names) are left out: their heights change in play.
    /// </summary>
    public static IReadOnlyList<LowPassage> LowPassages(MapGeometry g)
    {
        var f = g.Frame;
        var moving = DoorAreas(f);
        var named = new HashSet<int>();
        foreach (var l in f.Lines)
        {
            if (l.Element is not null && l.Special != 0 && l.Args[0] != 0 && Switches.NamesAreas(l.Special)) named.Add(l.Args[0]);
        }

        var found = new List<LowPassage>();
        for (var i = 0; i < f.Lines.Count; i++)
        {
            var l = f.Lines[i];
            if (l.Element is null || l.Back < 0 || Blocking(l.Extra)) continue;
            var (fa, ba) = (f.Sides[l.Front].Sector, f.Sides[l.Back].Sector);
            if (fa < 0 || ba < 0 || fa == ba || !Standable(f, fa) || !Standable(f, ba)) continue;
            if (Moves(fa) || Moves(ba)) continue;
            var (x, y) = (f.Sectors[fa], f.Sectors[ba]);
            var opening = Math.Min(x.CeilingHeight, y.CeilingHeight) - Math.Max(x.FloorHeight, y.FloorHeight);
            if (opening > 0 && opening < Height) found.Add(new LowPassage(i, opening));
        }

        return found;

        bool Moves(int area) => moving.Contains(area) || (f.Sectors[area].Tag != 0 && named.Contains(f.Sectors[area].Tag));
    }

    private static (MapPoint A, MapPoint B) Ends(MapFrame f, LineFrame l) => (new MapPoint(f.Vertices[l.V1].X, f.Vertices[l.V1].Y), new MapPoint(f.Vertices[l.V2].X, f.Vertices[l.V2].Y));

    private static bool Blocking(KeyValuePair<string, UdmfValue>[] extra) => Problems.Flag(extra, "blocking") || Problems.Flag(extra, "blockplayers") || Problems.Flag(extra, "blockeverything");

    private static bool Standable(MapFrame f, int area) => area >= 0 && area < f.Sectors.Count && f.Sectors[area] is { Element: not null } s && s.CeilingHeight - s.FloorHeight >= Height;

    /// <summary>The areas on the wall's faces (one for a one-sided wall, none twice).</summary>
    private static IEnumerable<int> Sides(MapFrame f, LineFrame l)
    {
        var front = f.Sides[l.Front].Sector;
        if (front >= 0) yield return front;
        if (l.Back >= 0 && f.Sides[l.Back].Sector is var back and >= 0 && back != front) yield return back;
    }

    /// <summary>The areas doors open: the back of a wall that opens its own door, and the areas a door action names.</summary>
    private static HashSet<int> DoorAreas(MapFrame f)
    {
        var doors = new HashSet<int>();
        var tags = new HashSet<int>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || !Doors.IsDoorAction(l.Special)) continue;
            if (l.Args[0] != 0) tags.Add(l.Args[0]);
            else if (l.Back >= 0 && f.Sides[l.Back].Sector >= 0) doors.Add(f.Sides[l.Back].Sector);
        }

        for (var s = 0; s < f.Sectors.Count; s++)
        {
            if (f.Sectors[s] is { Element: not null, Tag: not 0 } area && tags.Contains(area.Tag)) doors.Add(s);
        }

        return doors;
    }

    /// <summary>Whether the wall stops the player in <paramref name="area"/> from crossing it (a door counts as open).</summary>
    private static bool BlocksFrom(MapFrame f, LineFrame l, int area, HashSet<int> doors)
    {
        if (l.Back < 0 || Blocking(l.Extra)) return true;
        var (front, back) = (f.Sides[l.Front].Sector, f.Sides[l.Back].Sector);
        var other = front == area ? back : front;
        if (other < 0 || other >= f.Sectors.Count || f.Sectors[other].Element is null) return true;
        if (doors.Contains(other)) return false;
        var (from, to) = (f.Sectors[area], f.Sectors[other]);
        return to.FloorHeight - from.FloorHeight > Stairs.MaxRise || Math.Min(from.CeilingHeight, to.CeilingHeight) - Math.Max(from.FloorHeight, to.FloorHeight) < Height;
    }

    /// <summary>Whether a solid wall (other than the gap's own two) lies across the gap from <paramref name="p"/> to <paramref name="q"/>.</summary>
    private static bool Divided(MapGeometry g, MapFrame f, MapPoint p, MapPoint q, double ux, double uy, int wallA, int wallB, int area, HashSet<int> doors)
    {
        var (a, b) = (new MapPoint(p.X + ux * 0.5, p.Y + uy * 0.5), new MapPoint(q.X - ux * 0.5, q.Y - uy * 0.5));
        foreach (var i in g.LineSlotsIn(Math.Min(a.X, b.X) - 0.5, Math.Min(a.Y, b.Y) - 0.5, Math.Max(a.X, b.X) + 0.5, Math.Max(a.Y, b.Y) + 0.5))
        {
            var l = f.Lines[i];
            if (i == wallA || i == wallB || l.Element is null || !BlocksFrom(f, l, Sides(f, l).Contains(area) ? area : f.Sides[l.Front].Sector, doors)) continue;
            var (c, d) = Ends(f, l);
            if (Closest(a, b, c, d).Distance < 0.25) return true;
        }

        return false;
    }

    /// <summary>Whether the player walks from <paramref name="from"/> to <paramref name="to"/> in a straight line and can stand there.</summary>
    private static bool Open(MapGeometry g, MapFrame f, MapPoint from, MapPoint to, HashSet<int> doors)
    {
        if (g.SectorAt(to) is not { } end || !(Standable(f, end.Slot) || doors.Contains(end.Slot))) return false;
        foreach (var l in g.WallsCrossing(from, to))
        {
            if (l.Back < 0 || Blocking(l.Extra)) return false;
            var (a, b) = Ends(f, l);
            var right = (b.X - a.X) * (from.Y - a.Y) - (b.Y - a.Y) * (from.X - a.X) < 0;
            var near = right ? f.Sides[l.Front].Sector : f.Sides[l.Back].Sector;
            if (near >= 0 && BlocksFrom(f, l, near, doors)) return false;
        }

        return true;
    }

    /// <summary>Whether the segment passes through the inside of the box (touching its edge does not count).</summary>
    private static bool CrossesBox(MapPoint a, MapPoint b, double x0, double y0, double x1, double y1)
    {
        const double e = 1e-6;
        var (t0, t1) = (0.0, 1.0);
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        foreach (var (pp, qq) in ((double, double)[])[(-dx, a.X - (x0 + e)), (dx, (x1 - e) - a.X), (-dy, a.Y - (y0 + e)), (dy, (y1 - e) - a.Y)])
        {
            if (pp == 0)
            {
                if (qq < 0) return false;
                continue;
            }

            var r = qq / pp;
            if (pp < 0) t0 = Math.Max(t0, r);
            else t1 = Math.Min(t1, r);
            if (t0 > t1) return false;
        }

        return true;
    }

    /// <summary>The closest points of two segments that do not cross, and how far apart they are.</summary>
    private static (MapPoint P, MapPoint Q, double Distance) Closest(MapPoint a0, MapPoint a1, MapPoint b0, MapPoint b1)
    {
        (MapPoint, MapPoint, double) best = (a0, b0, double.MaxValue);
        void Try(MapPoint p, MapPoint s0, MapPoint s1, bool flip)
        {
            var c = Nearest(p, s0, s1);
            var d = p.DistanceTo(c);
            if (d < best.Item3) best = flip ? (c, p, d) : (p, c, d);
        }

        Try(a0, b0, b1, false);
        Try(a1, b0, b1, false);
        Try(b0, a0, a1, true);
        Try(b1, a0, a1, true);
        return best;
    }

    private static MapPoint Nearest(MapPoint p, MapPoint a, MapPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 == 0) return a;
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return new MapPoint(a.X + t * dx, a.Y + t * dy);
    }
}
