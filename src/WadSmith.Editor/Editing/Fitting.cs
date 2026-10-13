using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Editor.Editing;

/// <summary>
/// One copy of a picture filling a wall or a room's floor (D15's fit to wall): GZDoom's UDMF scale, so the picture is not
/// repeated or cut — a poster, a screen, an animated GIF. A wall's scale is the picture's size over the part's (GZDoom
/// reads a texture pixel at distance × scale + offset, so offsets stay in pixels and are cancelled for the part); a floor's
/// scale and panning put the picture's corners on the room's, its top to the north.
/// </summary>
public static class Fitting
{
    /// <summary>
    /// Fits each part of <paramref name="side"/>'s face that shows a texture (the middle of a solid wall; the upper and lower
    /// parts between rooms) to that texture's size from <paramref name="size"/>. Returns why not, or null when done.
    /// </summary>
    public static string? FitWall(StructureEdit e, Side side, Func<string, (int Width, int Height)?> size)
    {
        if (side.Line is not { } line) return "This side belongs to no wall.";
        var length = Math.Sqrt(Math.Pow(line.End.X - line.Start.X, 2) + Math.Pow(line.End.Y - line.Start.Y, 2));
        var here = side.Sector;
        var other = ReferenceEquals(line.Front, side) ? line.Back?.Sector : line.Front.Sector;
        var parts = new List<(WallPart Part, string Texture, double Height)>();
        if (other is null) parts.Add((WallPart.Middle, side.Middle, here.CeilingHeight - here.FloorHeight));
        else
        {
            if (here.CeilingHeight > other.CeilingHeight) parts.Add((WallPart.Top, side.Top, here.CeilingHeight - other.CeilingHeight));
            if (other.FloorHeight > here.FloorHeight) parts.Add((WallPart.Bottom, side.Bottom, other.FloorHeight - here.FloorHeight));
        }

        parts.RemoveAll(p => p.Texture is "" or "-" || p.Height <= 0 || size(p.Texture) is null);
        if (parts.Count == 0) return other is null ? "This wall has no picture to fit." : "Between these two rooms no part of the wall shows a picture to fit (a grate or fence in an opening keeps its size).";
        if (length <= 0) return "This wall has no length.";
        foreach (var (part, texture, height) in parts)
        {
            var (w, h) = size(texture)!.Value;
            var suffix = part switch { WallPart.Top => "_top", WallPart.Bottom => "_bottom", _ => "_mid" };
            e.Field(side, "scalex" + suffix, Value(w / length, 1));
            e.Field(side, "scaley" + suffix, Value(h / height, 1));
            e.Field(side, "offsetx" + suffix, Value(-side.OffsetX, 0));
            e.Field(side, "offsety" + suffix, Value(-side.OffsetY, 0));
        }

        return null;
    }

    /// <summary>
    /// Fits the floor (or ceiling) picture of <paramref name="room"/> to its walls' extent: one copy corner to corner, its top
    /// toward the north. Returns why not, or null when done.
    /// </summary>
    public static string? FitFlat(StructureEdit e, Sector room, IEnumerable<Line> walls, bool ceiling, Func<string, (int Width, int Height)?> size)
    {
        var texture = ceiling ? room.CeilingTexture : room.FloorTexture;
        if (texture is "" or "-" || size(texture) is not { } picture) return "This room's " + (ceiling ? "ceiling" : "floor") + " has no picture to fit.";
        var points = walls.Where(l => l.Front.Sector == room || l.Back?.Sector == room).SelectMany(l => new[] { l.Start, l.End }).ToList();
        if (points.Count == 0) return "This room has no walls to fit the picture to.";
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        if (maxX <= minX || maxY <= minY) return "This room has no width to fit the picture to.";
        var part = ceiling ? "ceiling" : "floor";
        // texel = scale × ((x, −y) + panning): x from the west edge, y from the north edge down.
        e.Field(room, "xscale" + part, Value(picture.Width / (maxX - minX), 1));
        e.Field(room, "yscale" + part, Value(picture.Height / (maxY - minY), 1));
        e.Field(room, "xpanning" + part, Value(-minX, 0));
        e.Field(room, "ypanning" + part, Value(maxY, 0));
        e.Field(room, "rotation" + part, null);
        return null;
    }

    /// <summary>A UDMF number, or null (the field taken away) when it is GZDoom's default anyway.</summary>
    private static UdmfValue? Value(double v, double otherwise)
    {
        var rounded = Math.Round(v, 6);
        return Math.Abs(rounded - otherwise) < 1e-9 ? (UdmfValue?)null : UdmfValue.Float(rounded);
    }
}
