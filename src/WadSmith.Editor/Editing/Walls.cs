using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>Changes to walls that several tools make: splitting a wall at a corner, giving one a second side.</summary>
public static class Walls
{
    /// <summary>
    /// Splits a wall at a corner lying on it: the wall keeps its first piece and a copy takes the rest,
    /// with each face's texture running on across the split as if it were still one wall. Returns the
    /// second piece.
    /// </summary>
    public static Line Split(StructureEdit edit, Line line, Vertex at)
    {
        var end = line.End;
        var first = Distance(line.Start, at);
        var rest = Distance(at, end);
        var piece = new Line(at, end, edit.Add(CopyOf(line.Front, line.Front.OffsetX + first)))
        {
            Special = line.Special,
            LineId = line.LineId,
        };
        line.Args.CopyTo(piece.Args, 0);
        foreach (var (k, v) in line.Extra) piece.Extra[k] = v;
        if (line.Back is { } back)
        {
            piece.Back = edit.Add(CopyOf(back, back.OffsetX));
            edit.Set(back, s => s.OffsetX, (s, x) => s.OffsetX = x, back.OffsetX + rest);
        }

        edit.Add(piece);
        edit.Set(line, l => l.End, (l, v) => l.End = v, at);
        return piece;
    }

    /// <summary>
    /// Gives a wall with a room on one side a face on the other, in <paramref name="sector"/>: it stops
    /// blocking, and its texture moves to the upper and lower parts, where height differences show it.
    /// The new face shows <paramref name="like"/>'s texture when given (a wall it was joined with),
    /// else the front's.
    /// </summary>
    public static Side OpenUp(StructureEdit edit, Line line, Sector sector, string fallback, Side? like = null)
    {
        var front = line.Front;
        var wall = Solid(front, fallback);
        if (front.Top is "-" or "") edit.Set(front, s => s.Top, (s, v) => s.Top = v, wall);
        if (front.Bottom is "-" or "") edit.Set(front, s => s.Bottom, (s, v) => s.Bottom = v, wall);
        edit.Set(front, s => s.Middle, (s, v) => s.Middle = v, "-");
        var other = like is null ? wall : Solid(like, wall);
        var back = edit.Add(new Side(sector) { Top = other, Bottom = other });
        if (like is not null) (back.OffsetX, back.OffsetY) = (like.OffsetX, like.OffsetY);
        edit.Set(line, l => l.Back, (l, v) => l.Back = v, back);
        edit.Flag(line, "twosided", true);
        edit.Flag(line, "blocking", false);
        return back;
    }

    /// <summary>Turns a wall round: its ends and its faces swap, so the face that was on its back is its front.</summary>
    public static void Turn(StructureEdit edit, Line line)
    {
        var (start, end, front, back) = (line.Start, line.End, line.Front, line.Back);
        edit.Set(line, x => x.Start, (x, v) => x.Start = v, end);
        edit.Set(line, x => x.End, (x, v) => x.End = v, start);
        if (back is null) return;
        edit.Set(line, x => x.Back, (x, v) => x.Back = v, null);
        edit.Set(line, x => x.Front, (x, v) => x.Front = v, back);
        edit.Set(line, x => x.Back, (x, v) => x.Back = v, front);
    }

    /// <summary>The texture a face shows as a solid wall: its middle, else its upper or lower, else the fallback.</summary>
    private static string Solid(Side side, string fallback) => side.Middle is not ("-" or "") ? side.Middle : side.Top is not ("-" or "") ? side.Top : side.Bottom is not ("-" or "") ? side.Bottom : fallback;

    internal static Side CopyOf(Side side, double offsetX)
    {
        var s = new Side(side.Sector) { OffsetX = offsetX, OffsetY = side.OffsetY, Top = side.Top, Middle = side.Middle, Bottom = side.Bottom };
        foreach (var (k, v) in side.Extra) s.Extra[k] = v;
        return s;
    }

    private static double Distance(Vertex a, Vertex b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
