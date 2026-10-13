using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
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

/// <summary>
/// Walk-over triggers: a line between two areas that opens a door elsewhere the first time the player crosses it (Doom's
/// W1 door: Door_Open by the door's tag) — how a monster closet opens behind the player who walks into its room.
/// </summary>
public static class WalkOvers
{
    /// <summary>
    /// Makes <paramref name="line"/> (a two-sided line doing nothing yet) open <paramref name="door"/> the first time the player
    /// walks over it, through <paramref name="edit"/>; the door gets a tag of its own if it has none (a line that opens doors
    /// already opens this one too, the door taking their tag). Returns why when it cannot.
    /// </summary>
    public static string? Make(StructureEdit edit, MapGeometry before, Line line, Sector door)
    {
        if (line.Back is null) return "A trigger is a line the player walks over: pick a line between two rooms.";
        if (Doors.Recognise(before, door) is null) return "A trigger opens a door: point at a door.";

        // A line that already opens doors when walked over opens this one too: the door joins them by their tag.
        if (line.Special == Doors.DoorOpen && line.Flag("playercross") && line.Args[0] != 0)
        {
            if (door.Tag != 0 && door.Tag != line.Args[0]) return "This line opens other doors, and this door has a tag of its own; pick another line.";
            edit.Set(door, s => s.Tag, (s, v) => s.Tag = v, line.Args[0]);
            return null;
        }

        if (line.Special != 0) return "This line already does something; pick a plain one between two rooms.";
        var tag = door.Tag != 0 ? door.Tag : before.HighestTag + 1;
        if (door.Tag != tag) edit.Set(door, s => s.Tag, (s, v) => s.Tag = v, tag);
        edit.Set(line, x => x.Special, (x, v) => x.Special = v, Doors.DoorOpen);
        int[] args = [tag, Doors.NormalSpeed, 0, 0, 0];
        for (var i = 0; i < args.Length; i++)
        {
            var k = i;
            edit.Set(line, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
        }

        edit.Flag(line, "playercross", true);
        return null;
    }
}
