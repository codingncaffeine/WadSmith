using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Lining a wall's texture up with the walls along it (§3.12 "textures and alignment"): the texture runs on across the
/// corners as if the walls were one, its columns continuing by each wall's length and its rows holding their height.
/// </summary>
public static class Aligning
{
    /// <summary>The texture <paramref name="face"/> carries on <paramref name="part"/>.</summary>
    public static string TextureOf(Side face, WallPart part) => part switch { WallPart.Top => face.Top, WallPart.Bottom => face.Bottom, _ => face.Middle };

    /// <summary>The part of a face that carries a texture, the middle first; the middle when none does.</summary>
    public static WallPart PartOf(Side face) => face.Middle is not ("-" or "") ? WallPart.Middle : face.Top is not ("-" or "") ? WallPart.Top : face.Bottom is not ("-" or "") ? WallPart.Bottom : WallPart.Middle;

    /// <summary>A face's corners as one walks along it: a front face runs from its wall's start to its end, a back face the other way.</summary>
    public static (Vertex From, Vertex To) Ends(Side face)
    {
        var line = face.Line ?? throw new InvalidOperationException("The face belongs to no wall.");
        return ReferenceEquals(face, line.Front) ? (line.Start, line.End) : (line.End, line.Start);
    }

    /// <summary>
    /// The faces that continue <paramref name="face"/> with the same texture on <paramref name="part"/>: those before it
    /// (nearest first) and those after it, walking corner to corner until the texture changes or the walk comes round.
    /// </summary>
    public static (IReadOnlyList<Side> Before, IReadOnlyList<Side> After) Chain(MapGeometry geometry, Side face, WallPart part)
    {
        var texture = TextureOf(face, part);
        var seen = new HashSet<Side>(ReferenceEqualityComparer.Instance) { face };
        if (texture is "-" or "" || face.Line is not { OnMap: true }) return ([], []);
        var after = Walk(geometry, face, part, texture, forward: true, seen);
        var before = Walk(geometry, face, part, texture, forward: false, seen);
        return (before, after);
    }

    private static List<Side> Walk(MapGeometry geometry, Side face, WallPart part, string texture, bool forward, HashSet<Side> seen)
    {
        var list = new List<Side>();
        var current = face;
        while (Next(geometry, current, part, texture, forward) is { } next && seen.Add(next))
        {
            list.Add(next);
            current = next;
        }

        return list;
    }

    /// <summary>
    /// The face that carries on from <paramref name="face"/> at its far corner (or, backwards, arrives at its near one):
    /// a face of a wall meeting that corner that runs the same way round with the same texture on the part; the one that
    /// turns least, and of two alike the one in the same room.
    /// </summary>
    private static Side? Next(MapGeometry geometry, Side face, WallPart part, string texture, bool forward)
    {
        if (face.Line is not { OnMap: true } line) return null;
        var (from, to) = Ends(face);
        var corner = forward ? to : from;
        var (dx, dy) = forward ? (to.X - from.X, to.Y - from.Y) : (from.X - to.X, from.Y - to.Y);
        Side? best = null;
        var bestScore = (double.MaxValue, 1);
        foreach (var other in geometry.LinesIn(corner.X - 0.5, corner.Y - 0.5, corner.X + 0.5, corner.Y + 0.5))
        {
            if (ReferenceEquals(other, line) || !other.OnMap) continue;
            foreach (var candidate in new[] { other.Front, other.Back })
            {
                if (candidate is null || !string.Equals(TextureOf(candidate, part), texture, StringComparison.OrdinalIgnoreCase)) continue;
                var (cf, ct) = Ends(candidate);
                if (!ReferenceEquals(forward ? cf : ct, corner)) continue;
                var (cx, cy) = forward ? (ct.X - cf.X, ct.Y - cf.Y) : (cf.X - ct.X, cf.Y - ct.Y);
                var turn = Math.Round(Math.Abs(Math.Atan2(dx * cy - dy * cx, dx * cx + dy * cy)), 6);
                var score = (turn, ReferenceEquals(candidate.Sector, face.Sector) ? 0 : 1);
                if (score.CompareTo(bestScore) < 0)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Sets the offsets of the walls along <paramref name="face"/> so its texture runs on across them: across, by each
    /// wall's length (wrapped to the texture's width when <paramref name="width"/> knows it); up, the face's own, moved by
    /// the ceiling difference on one-sided walls whose middle hangs from the ceiling, so the rows meet too. Returns how
    /// many faces were lined up.
    /// </summary>
    public static int Align(StructureEdit e, MapGeometry geometry, Side face, WallPart part, Func<string, int?> width)
    {
        var (before, after) = Chain(geometry, face, part);
        var w = width(TextureOf(face, part)) is { } tw and > 0 ? tw : 0;
        double Wrap(double x) => w > 0 ? ((x % w) + w) % w : x;
        var x = face.OffsetX;
        var previous = face;
        foreach (var s in after)
        {
            x = Wrap(x + previous.Line!.Length);
            Set(e, s, x, RowsFor(face, s, part));
            previous = s;
        }

        x = face.OffsetX;
        foreach (var s in before)
        {
            x = Wrap(x - s.Line!.Length);
            Set(e, s, x, RowsFor(face, s, part));
        }

        return before.Count + after.Count;
    }

    private static void Set(StructureEdit e, Side s, double x, double y)
    {
        e.Set(s, f => f.OffsetX, (f, v) => f.OffsetX = v, x);
        e.Set(s, f => f.OffsetY, (f, v) => f.OffsetY = v, y);
    }

    /// <summary>The up offset that puts <paramref name="other"/>'s rows level with <paramref name="face"/>'s: the same, unless both are one-sided middles hung from ceilings of different heights.</summary>
    private static double RowsFor(Side face, Side other, WallPart part)
    {
        if (part == WallPart.Middle && face.Line is { Back: null } a && other.Line is { Back: null } b && !a.Flag("dontpegbottom") && !b.Flag("dontpegbottom"))
        {
            return face.OffsetY + (face.Sector.CeilingHeight - other.Sector.CeilingHeight);
        }

        return face.OffsetY;
    }
}
