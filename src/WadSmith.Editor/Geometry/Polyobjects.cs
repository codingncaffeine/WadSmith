using WadSmith.Editor.Model;

namespace WadSmith.Editor.Geometry;

/// <summary>How a polyobject's start spot treats what stands in its way: pushes it, crushes it, or also hurts on touch.</summary>
public enum PolySpot
{
    Pushes,
    Crushes,
    Hurts,
}

/// <summary>
/// A polyobject as the map holds it (Hexen's moving walls, D62), by slot in the map's frame: its number, its walls (a loop of
/// one-sided walls drawn in a pen outside the playing area) with the wall that starts them, its anchor and start spot (objects
/// whose angle is the number; the walls stand in the map moved by the spot less the anchor), how the spot treats what is in the
/// way, the polyobject that moves with it the other way (its mirror, 0 for none) and its sound sequence.
/// </summary>
public sealed record PolyobjectFrame(int Number, IReadOnlyList<int> Lines, int StartLine, int Anchor, int Spot, PolySpot Kind, int Mirror, int Sound, double Dx, double Dy);

/// <summary>
/// Finds a map's polyobjects as GZDoom does when it loads a map (maploader/polyobjects.cpp): each start spot's number names a
/// polyobject, whose walls are those of the wall with Polyobj_StartLine of that number and every wall joined to it end to start
/// (or the walls with Polyobj_ExplicitLine of that number, in their order), carried by the spot less the anchor of that number.
/// </summary>
public static class Polyobjects
{
    /// <summary>GZDoom's line actions that make a polyobject: Polyobj_StartLine (po, mirror, sound) and Polyobj_ExplicitLine (po, order, mirror, sound).</summary>
    public const int StartLine = 1;
    public const int ExplicitLine = 5;

    /// <summary>The anchor and the start spots every game has (9300–9303), and Hexen's own numbers for them (3000–3002).</summary>
    public const int Anchor = 9300;
    public const int Spot = 9301;
    public const int SpotCrushing = 9302;
    public const int SpotHurting = 9303;

    /// <summary>Whether an object is a polyobject's anchor in a game family.</summary>
    public static bool IsAnchor(int type, string family) => type == Anchor || (type == 3000 && family == "hexen");

    /// <summary>How a polyobject's start spot treats what is in its way, or null when the object is no start spot in a game family.</summary>
    public static PolySpot? SpotKind(int type, string family) => type switch
    {
        Spot => PolySpot.Pushes,
        SpotCrushing => PolySpot.Crushes,
        SpotHurting => PolySpot.Hurts,
        3001 when family == "hexen" => PolySpot.Pushes,
        3002 when family == "hexen" => PolySpot.Crushes,
        _ => null,
    };

    /// <summary>The polyobject a wall is one of, or null.</summary>
    public static PolyobjectFrame? OfWall(MapGeometry geometry, Line wall, string family) =>
        wall.OnMap ? geometry.Polyobjects(family).FirstOrDefault(p => p.Lines.Any(slot => ReferenceEquals(geometry.Frame.Lines[slot].Element, wall))) : null;

    /// <summary>The polyobjects drawn in an area (their pen: the area their walls face), none for any other area.</summary>
    public static IReadOnlyList<PolyobjectFrame> InPen(MapGeometry geometry, Sector area, string family)
    {
        var f = geometry.Frame;
        return [.. geometry.Polyobjects(family).Where(p => p.Lines.Any(slot => f.Lines[slot].Front >= 0 && ReferenceEquals(f.Sectors[f.Sides[f.Lines[slot].Front].Sector].Element, area)))];
    }

    /// <summary>The map's polyobjects in <paramref name="family"/>'s game, those with walls, a spot and an anchor, by number.</summary>
    public static IReadOnlyList<PolyobjectFrame> Find(MapFrame f, string family)
    {
        // Most maps have none: only maps with a polyobject's wall action go further.
        var makers = new List<int>();
        for (var i = 0; i < f.Lines.Count; i++)
        {
            if (f.Lines[i].Element is not null && f.Lines[i].Special is StartLine or ExplicitLine) makers.Add(i);
        }

        if (makers.Count == 0) return [];
        var spots = new Dictionary<int, int>();
        var anchors = new Dictionary<int, int>();
        for (var t = 0; t < f.Things.Count; t++)
        {
            var thing = f.Things[t];
            if (thing.Element is null) continue;
            if (SpotKind(thing.Type, family) is not null) spots.TryAdd(thing.Angle, t);
            else if (IsAnchor(thing.Type, family)) anchors.TryAdd(thing.Angle, t);
        }

        // The faces that leave each corner: a wall's front leaves its first corner, its back its second (the engine's own walk).
        Dictionary<int, List<(int Line, int To)>>? leaving = null;
        var found = new List<PolyobjectFrame>();
        foreach (var (number, spot) in spots.OrderBy(p => p.Key))
        {
            if (!anchors.TryGetValue(number, out var anchor)) continue;
            List<int> lines;
            int start, mirror, sound;
            if (makers.FirstOrDefault(i => f.Lines[i].Special == StartLine && f.Lines[i].Args[0] == number, -1) is >= 0 and var first)
            {
                if (leaving is null)
                {
                    leaving = [];
                    for (var i = 0; i < f.Lines.Count; i++)
                    {
                        var l = f.Lines[i];
                        if (l.Element is null) continue;
                        Leave(l.V1, i, l.V2);
                        if (l.Back >= 0) Leave(l.V2, i, l.V1);
                    }
                }

                lines = Walk(f.Lines[first].V1, leaving);
                (start, mirror, sound) = (first, f.Lines[first].Args[1], f.Lines[first].Args[2]);
            }
            else
            {
                lines = [.. makers.Where(i => f.Lines[i].Special == ExplicitLine && f.Lines[i].Args[0] == number).OrderBy(i => f.Lines[i].Args[1])];
                if (lines.Count == 0) continue;
                (start, mirror, sound) = (lines[0], f.Lines[lines[0]].Args[2], f.Lines[lines[0]].Args[3]);
            }

            var (a, s) = (f.Things[anchor], f.Things[spot]);
            found.Add(new PolyobjectFrame(number, lines, start, anchor, spot, SpotKind(s.Type, family)!.Value, mirror, sound, s.X - a.X, s.Y - a.Y));
        }

        return found;

        void Leave(int from, int line, int to)
        {
            if (!leaving!.TryGetValue(from, out var list)) leaving[from] = list = [];
            list.Add((line, to));
        }

        // From the start wall's first corner, every face leaving a corner reached so far, as GZDoom's IterFindPolySides walks.
        static List<int> Walk(int from, Dictionary<int, List<(int Line, int To)>> leaving)
        {
            var lines = new List<int>();
            var seenLines = new HashSet<int>();
            var corners = new List<int> { from };
            var seen = new HashSet<int> { from };
            for (var at = 0; at < corners.Count; at++)
            {
                if (!leaving.TryGetValue(corners[at], out var faces)) continue;
                foreach (var (line, to) in faces)
                {
                    if (seenLines.Add(line)) lines.Add(line);
                    if (seen.Add(to)) corners.Add(to);
                }
            }

            return lines;
        }
    }
}
