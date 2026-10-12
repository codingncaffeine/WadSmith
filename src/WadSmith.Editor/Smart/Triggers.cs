using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>How walls set things off, in the Inspector's words.</summary>
public static class Triggers
{
    /// <summary>"a switch", "2 lines walked over", "a wall shot" for the walls given.</summary>
    public static IEnumerable<string> Ways(IReadOnlyCollection<Line> walls)
    {
        static string Count(int n, string one, string many) => n == 1 ? one : $"{n} {many}";
        var used = walls.Count(l => l.Flag("playeruse"));
        var crossed = walls.Count(l => !l.Flag("playeruse") && (l.Flag("playercross") || l.Flag("monstercross")));
        var shot = walls.Count(l => !l.Flag("playeruse") && !l.Flag("playercross") && !l.Flag("monstercross") && l.Flag("impact"));
        var other = walls.Count - used - crossed - shot;
        if (used > 0) yield return Count(used, "a switch", "switches");
        if (crossed > 0) yield return Count(crossed, "a line walked over", "lines walked over");
        if (shot > 0) yield return Count(shot, "a wall shot", "walls shot");
        if (other > 0) yield return Count(other, "another wall", "other walls");
    }
}
