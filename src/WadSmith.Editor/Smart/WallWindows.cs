using System.Globalization;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// A window found in the map (D21): a thin area between two rooms, below both their ceilings, that the
/// player sees through but cannot step into (its sill too high, its opening too low, or its walls blocking).
/// </summary>
/// <param name="Area">The window's own area: its floor is the sill, its ceiling the top.</param>
/// <param name="Rooms">The two rooms it looks between.</param>
/// <param name="Faces">Its walls to the two rooms.</param>
/// <param name="Blocks">Whether its walls stop players and monsters (a low window does not need to).</param>
public sealed record WallWindow(Sector Area, IReadOnlyList<Sector> Rooms, IReadOnlyList<Line> Faces, bool Blocks)
{
    public double Sill => Area.FloorHeight;

    public double Top => Area.CeilingHeight;
}

/// <summary>
/// Windows, as Doom's maps make them: an area in the thickness of a wall with its floor raised to a sill and
/// its ceiling lowered to the top, the wall's texture on the parts above and below.
/// </summary>
public static class WallWindows
{
    /// <summary>The thickest wall a window goes through, and the depth of one made in a wall two rooms share.</summary>
    public const double MaxDepth = 128;
    public const double ThinDepth = 16;

    /// <summary>The width of a window made by a click, and the narrowest one.</summary>
    public const double DefaultWidth = 64;
    public const double MinWidth = 8;

    /// <summary>What the player can step up (the engine's rule) and the space the player needs to stand.</summary>
    private const double StepUp = 24;
    private const double PlayerHeight = 56;

    /// <summary>The window <paramref name="area"/> is, or null.</summary>
    public static WallWindow? Recognise(MapGeometry geometry, Sector area)
    {
        if (!area.OnMap || area.CeilingHeight <= area.FloorHeight || area.Special != 0) return null;
        var walls = WallsOf(geometry, area);
        if (walls.Count == 0 || walls.Any(l => l.Special != 0)) return null;

        // An area an action names by its tag moves (a door, a lift): not a window, whatever its shape now.
        if (area.Tag != 0 && geometry.LinesNaming(area.Tag).Any(i => geometry.Frame.Lines[i].Element is { OnMap: true, Special: not 0 })) return null;

        // The rooms it opens onto (closed areas beside it, the rest of a wall made with it, do not count).
        var faces = walls.Where(l => l.Back is not null && Other(l, area) is { } r && r.CeilingHeight > r.FloorHeight).ToList();
        var rooms = faces.Select(l => Other(l, area)!).Distinct().ToList();
        if (rooms.Count != 2) return null;

        // Thin across its longest wall to a room.
        var along = faces.OrderByDescending(l => l.Length).First();
        var (nx, ny) = ((along.End.Y - along.Start.Y) / along.Length, -(along.End.X - along.Start.X) / along.Length);
        var across = walls.SelectMany(l => new[] { l.Start, l.End }).Select(v => (v.X - along.Start.X) * nx + (v.Y - along.Start.Y) * ny).ToList();
        if (across.Max() - across.Min() > MaxDepth) return null;

        // A top below both rooms' ceilings, an opening both rooms see through, and no way in for the player.
        if (rooms.Any(r => area.CeilingHeight >= r.CeilingHeight || Math.Min(area.CeilingHeight, r.CeilingHeight) <= Math.Max(area.FloorHeight, r.FloorHeight))) return null;
        var blocks = faces.All(l => l.Flag("blocking"));
        var climbs = rooms.Any(r => area.FloorHeight - r.FloorHeight <= StepUp);
        if (!blocks && climbs && area.CeilingHeight - area.FloorHeight >= PlayerHeight) return null;
        return new WallWindow(area, rooms, faces, blocks);
    }

    /// <summary>The sill and top a new window between two rooms gets: about a third of the way up and a fifth down from the top, on whole eighths.</summary>
    public static (double Sill, double Top)? DefaultHeights(Sector a, Sector b)
    {
        var floor = Math.Max(a.FloorHeight, b.FloorHeight);
        var ceiling = Math.Min(a.CeilingHeight, b.CeilingHeight);
        var room = ceiling - floor;
        if (room < 32) return null;
        var sill = floor + Math.Clamp(Math.Round(room * 0.3 / 8) * 8, 8, 48);
        var top = ceiling - Math.Clamp(Math.Round(room * 0.2 / 8) * 8, 8, 32);
        return (sill, top);
    }

    /// <summary>
    /// Makes a window in <paramref name="wall"/> from <paramref name="from"/> to <paramref name="to"/> (distances
    /// along it from its start) through <paramref name="edit"/>. A wall with a room on one side gets an opening
    /// through to the room behind it (up to <see cref="MaxDepth"/> away); a wall two rooms share becomes a wall
    /// <see cref="ThinDepth"/> thick, closed but for the window. The sill and top are <see cref="DefaultHeights"/>;
    /// the parts above and below show the wall's texture. Returns the window's area, or null and why.
    /// </summary>
    public static (Sector? Window, string? Why) Make(StructureEdit edit, MapGeometry before, Line wall, double from, double to, Look look)
    {
        var length = wall.Length;
        var (a, b) = (Math.Clamp(Math.Min(from, to), 0, length), Math.Clamp(Math.Max(from, to), 0, length));
        if (b - a < MinWidth)
        {
            var middle = (a + b) / 2;
            var half = Math.Min(DefaultWidth, length) / 2;
            (a, b) = (Math.Clamp(middle - half, 0, length - 2 * half), 0);
            b = a + 2 * half;
        }

        var (dx, dy) = ((wall.End.X - wall.Start.X) / length, (wall.End.Y - wall.Start.Y) / length);
        MapPoint At(double t, double out_, (double X, double Y) n) => new(wall.Start.X + dx * t + n.X * out_, wall.Start.Y + dy * t + n.Y * out_);
        var texture = Texture(wall, look);
        return wall.Back is null ? Through(edit, before, wall, a, b, (dx, dy), At, texture, look) : InShared(edit, before, wall, a, b, (dx, dy), At, texture, look);
    }

    /// <summary>A window through a wall with a room on one side, to the room behind it.</summary>
    private static (Sector?, string?) Through(StructureEdit edit, MapGeometry before, Line wall, double a, double b, (double X, double Y) dir, Func<double, double, (double, double), MapPoint> at, string texture, Look look)
    {
        var room = wall.Front.Sector;
        var outward = (-dir.Y, dir.X);
        var (p, q) = (at(a, 0, outward), at(b, 0, outward));
        var (r, s) = (at(a, MaxDepth, outward), at(b, MaxDepth, outward));
        var (x0, y0, x1, y1) = (new[] { p.X, q.X, r.X, s.X }.Min(), new[] { p.Y, q.Y, r.Y, s.Y }.Min(), new[] { p.X, q.X, r.X, s.X }.Max(), new[] { p.Y, q.Y, r.Y, s.Y }.Max());

        // The nearest wall behind, along this one, facing away from it: the room behind is on its far side.
        var behind = new List<(Line Line, double Depth, double T0, double T1)>();
        foreach (var l in before.LinesIn(x0, y0, x1, y1))
        {
            if (ReferenceEquals(l, wall) || !l.OnMap || !Doors.Parallel(l, wall)) continue;
            var depth = (l.Start.X - wall.Start.X) * outward.Item1 + (l.Start.Y - wall.Start.Y) * outward.Item2;
            var (t0, t1) = ((l.Start.X - wall.Start.X) * dir.X + (l.Start.Y - wall.Start.Y) * dir.Y, (l.End.X - wall.Start.X) * dir.X + (l.End.Y - wall.Start.Y) * dir.Y);
            if (depth <= 0.5 || depth > MaxDepth || Math.Max(t0, t1) <= a + 1e-6 || Math.Min(t0, t1) >= b - 1e-6) continue;
            behind.Add((l, depth, Math.Min(t0, t1), Math.Max(t0, t1)));
        }

        if (behind.Count == 0) return (null, string.Create(CultureInfo.InvariantCulture, $"A window looks into another room, and there is none within {MaxDepth:0} behind this part of the wall."));
        var nearest = behind.Min(w => w.Depth);
        var far = behind.Where(w => Math.Abs(w.Depth - nearest) < 1e-6).OrderBy(w => w.T0).ToList();

        // The far side must be another room's wall all along the window, with nothing but empty space between.
        var reach = a;
        foreach (var w in far)
        {
            if (w.T0 > reach + 1e-6) break;
            reach = Math.Max(reach, w.T1);
        }

        var facesAway = far.All(w => w.Line.Back is null && (w.Line.End.Y - w.Line.Start.Y) * outward.Item1 - (w.Line.End.X - w.Line.Start.X) * outward.Item2 > 0);
        if (reach < b - 1e-6 || !facesAway) return (null, "Behind this part of the wall is not one room's wall all along: make the window narrower, or where the room behind runs alongside.");
        var strip = new[] { p, q, at(b, nearest, outward), at(a, nearest, outward) };
        if (!Doors.Clear(before, wall, strip) || before.SectorAt(new MapPoint((p.X + strip[2].X) / 2, (p.Y + strip[2].Y) / 2)) is not null) return (null, "Something is in the way between this wall and the room behind it.");

        var there = far[0].Line.Front.Sector;
        if (DefaultHeights(room, there) is not { } heights) return (null, "These rooms are too low for a window between them: there must be 32 between the higher floor and the lower ceiling.");
        var made = ShapeDrawing.Draw(edit, before, strip, look);
        if (made.Count != 1) throw new InvalidOperationException("The window's area did not come out as one area.");
        var window = made[0];
        edit.Set(window, x => x.FloorTexture, (x, v) => x.FloorTexture = v, room.FloorTexture);
        edit.Set(window, x => x.CeilingTexture, (x, v) => x.CeilingTexture = v, room.CeilingTexture);
        edit.Set(window, x => x.Light, (x, v) => x.Light = v, room.Light);
        Finish(edit, [window], window, heights, texture, room);
        return (window, null);
    }

    /// <summary>A window in a wall two rooms share: the wall becomes a thin one, closed but for the window.</summary>
    private static (Sector?, string?) InShared(StructureEdit edit, MapGeometry before, Line wall, double a, double b, (double X, double Y) dir, Func<double, double, (double, double), MapPoint> at, string texture, Look look)
    {
        var (front, back) = (wall.Front.Sector, wall.Back!.Sector);
        if (ReferenceEquals(front, back)) return (null, "A window goes in a wall between two rooms; this wall has the same room on both sides.");
        if (Recognise(before, front) is not null || Recognise(before, back) is not null) return (null, "This wall is already a window's.");
        if (DefaultHeights(front, back) is not { } heights) return (null, "These rooms are too low for a window between them: there must be 32 between the higher floor and the lower ceiling.");

        // The thin wall goes into the bigger room, or the other where that one has no room for it.
        double Size(Sector s) => before.WallBoundsAt(s.Slot) is { } w ? (w.MaxX - w.MinX) * (w.MaxY - w.MinY) : 0;
        foreach (var into in new[] { front, back }.OrderByDescending(Size))
        {
            var n = ReferenceEquals(into, front) ? (dir.Y, -dir.X) : (-dir.Y, dir.X);
            var length = wall.Length;
            var strip = new[] { at(0, 0, n), at(length, 0, n), at(length, ThinDepth, n), at(0, ThinDepth, n) };
            if (!Doors.Clear(before, wall, strip)) continue;
            var cuts = new List<(MapPoint, MapPoint)>();
            if (a > 1e-6) cuts.Add((at(a, 0, n), at(a, ThinDepth, n)));
            if (b < length - 1e-6) cuts.Add((at(b, 0, n), at(b, ThinDepth, n)));
            var made = ShapeDrawing.Draw(edit, before, strip, look, cuts, texture);

            // The window is the piece between the cuts; the rest of the wall is closed.
            var walls = Touched(edit);
            Sector? window = null;
            foreach (var piece in made)
            {
                var points = walls.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, piece) || ReferenceEquals(l.Back?.Sector, piece))).SelectMany(l => new[] { l.Start, l.End }).ToList();
                var t = points.Average(v => (v.X - wall.Start.X) * dir.X + (v.Y - wall.Start.Y) * dir.Y);
                if (t > a && t < b) window = piece;
            }

            if (window is null) throw new InvalidOperationException("The window's piece of the wall was not found.");
            Finish(edit, made, window, heights, texture, ReferenceEquals(into, front) ? back : front);
            return (window, null);
        }

        return (null, string.Create(CultureInfo.InvariantCulture, $"There is not room for a wall {ThinDepth:0} thick on either side of this one."));
    }

    /// <summary>
    /// Heights and textures: the window at its sill and top, the rest of a thin wall closed, the wall's texture
    /// above and below. The parts above and below are pegged to the rooms' ceilings (upper and lower unpegged)
    /// and the window's own sides lowered to match, so the texture runs on from <paramref name="room"/>'s walls
    /// round the opening as if cut through them.
    /// </summary>
    private static void Finish(StructureEdit edit, IReadOnlyList<Sector> made, Sector window, (double Sill, double Top) heights, string texture, Sector room)
    {
        foreach (var piece in made)
        {
            var (floor, ceiling) = ReferenceEquals(piece, window) ? heights : (heights.Sill, heights.Sill);
            edit.Set(piece, x => x.FloorHeight, (x, v) => x.FloorHeight = v, floor);
            edit.Set(piece, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, ceiling);
        }

        var pieces = made.ToHashSet(ReferenceEqualityComparer.Instance);
        var lower = room.CeilingHeight - heights.Top;
        foreach (var l in Touched(edit).Where(l => l.OnMap && (pieces.Contains(l.Front.Sector) || (l.Back is { } back && pieces.Contains(back.Sector)))))
        {
            // The window's sides (solid, or between it and the closed rest of a thin wall) show the room's texture rows.
            var side = l.Back is null || (pieces.Contains(l.Front.Sector) && pieces.Contains(l.Back.Sector));
            foreach (var face in new[] { l.Front, l.Back }.OfType<Side>())
            {
                if (side && ReferenceEquals(face.Sector, window)) edit.Set(face, x => x.OffsetY, (x, v) => x.OffsetY = v, lower);
                if (l.Back is null)
                {
                    edit.Set(face, x => x.Middle, (x, v) => x.Middle = v, texture);
                    continue;
                }

                edit.Set(face, x => x.Top, (x, v) => x.Top = v, texture);
                edit.Set(face, x => x.Bottom, (x, v) => x.Bottom = v, texture);
                edit.Set(face, x => x.Middle, (x, v) => x.Middle = v, "-");
            }

            if (l.Back is null) continue;
            edit.Flag(l, "dontpegtop", true);
            edit.Flag(l, "dontpegbottom", true);
        }
    }

    /// <summary>The texture the wall shows, for the parts above and below its window.</summary>
    private static string Texture(Line wall, Look look) =>
        new[] { wall.Front.Middle, wall.Front.Top, wall.Front.Bottom, wall.Back?.Middle, wall.Back?.Top, wall.Back?.Bottom }.FirstOrDefault(t => t is not (null or "" or "-")) ?? look.Wall;

    private static List<Line> Touched(StructureEdit edit) => [.. edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct()];

    private static Sector? Other(Line l, Sector area) => ReferenceEquals(l.Front.Sector, area) ? l.Back?.Sector : l.Front.Sector;

    private static List<Line> WallsOf(MapGeometry geometry, Sector area)
    {
        if (geometry.WallBoundsAt(area.Slot) is not { } b) return [];
        return [.. geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area)))];
    }
}
