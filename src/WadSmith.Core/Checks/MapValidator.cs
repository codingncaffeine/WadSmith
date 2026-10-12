using WadSmith.Core.Maps;

namespace WadSmith.Core.Checks;

public enum Severity
{
    /// <summary>The engine will refuse the map or crash on it.</summary>
    Error,
    /// <summary>The map loads but something is probably not what was meant.</summary>
    Warning,
}

/// <summary>One thing wrong with a map, in a sentence a player can act on.</summary>
public sealed record MapProblem(Severity Severity, string Message, string? Element = null, int Index = -1);

/// <summary>
/// The structural checks: every reference points at something that exists, every line has a front
/// side and a length. These are the ones an engine cannot recover from; the Problems panel adds the
/// playability checks on top of them.
/// </summary>
public static class MapValidator
{
    public static IReadOnlyList<MapProblem> Check(MapModel m)
    {
        var problems = new List<MapProblem>();
        for (var i = 0; i < m.Lines.Count; i++)
        {
            var l = m.Lines[i];
            if (l.V1 < 0 || l.V1 >= m.Vertices.Count || l.V2 < 0 || l.V2 >= m.Vertices.Count)
            {
                problems.Add(new(Severity.Error, $"Line {i} ends at a corner that does not exist.", "line", i));
                continue;
            }

            if (l.Front < 0 || l.Front >= m.Sides.Count) problems.Add(new(Severity.Error, $"Line {i} has no front side, so the engine cannot draw it.", "line", i));
            if (l.Back >= m.Sides.Count) problems.Add(new(Severity.Error, $"Line {i}'s back side does not exist.", "line", i));
            var a = m.Vertices[l.V1];
            var b = m.Vertices[l.V2];
            if (a.X == b.X && a.Y == b.Y) problems.Add(new(Severity.Warning, $"Line {i} has no length (both ends are on the same spot).", "line", i));
            if (l.V1 == l.V2) continue;
            if (l.Back >= 0 && l.Back < m.Sides.Count && !l.Flag("twosided"))
                problems.Add(new(Severity.Warning, $"Line {i} has rooms on both sides but is not marked two-sided, so its middle texture is drawn solid.", "line", i));
        }

        for (var i = 0; i < m.Sides.Count; i++)
        {
            var s = m.Sides[i].Sector;
            if (s < 0 || s >= m.Sectors.Count) problems.Add(new(Severity.Error, $"Side {i} belongs to a room that does not exist.", "side", i));
        }

        return problems;
    }
}
