using WadSmith.Core.Games;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// A switch found in the map (D21): a wall the player uses that sets off something elsewhere, the areas
/// it names by their tag, and what it does to them.
/// </summary>
/// <param name="Wall">The switch.</param>
/// <param name="Targets">The areas with the tag its action names (none for actions on things or scripts).</param>
/// <param name="Does">What it does, in the Inspector's words: "opens a door", "lowers a lift", or the action's name.</param>
/// <param name="Repeats">Whether it works again and again.</param>
public sealed record Switch(Line Wall, IReadOnlyList<Sector> Targets, string Does, bool Repeats);

/// <summary>
/// Switches: walls with an action the player uses that work on areas elsewhere by tag (not a door's or lift's
/// own walls, which work on the area behind them, nor exits or teleporters, which have their own views). A
/// door or lift gets a switch by a click on a solid wall: a piece of it as wide as the switch picture becomes
/// the switch, and the door or lift gets a tag of its own if it has none.
/// </summary>
public static class Switches
{
    /// <summary>The width of the piece of wall a switch takes (the switch pictures are 64 wide).</summary>
    public const double Width = 64;

    private static readonly string[] AreaActions = ["Door_", "Plat_", "Floor_", "Ceiling_", "FloorAndCeiling_", "Stairs_", "Light_", "Generic_", "Pillar_", "Elevator_", "Sector_"];

    /// <summary>Whether an action works on the areas with the tag in its first argument (doors, lifts, floors, lights…).</summary>
    public static bool NamesAreas(int special) => ActionCatalog.Get(special) is { } a && AreaActions.Any(p => a.Name.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The switch <paramref name="wall"/> is, or null.</summary>
    public static Switch? Recognise(MapGeometry geometry, Line wall)
    {
        if (!wall.OnMap || wall.Special == 0 || !wall.Flag("playeruse") || Exits.IsExit(wall) || Teleports.IsTeleport(wall) || wall.Args[0] == 0) return null;
        var f = geometry.Frame;
        var targets = NamesAreas(wall.Special) ? geometry.SectorsTagged(wall.Args[0]).Select(i => f.Sectors[i].Element).OfType<Sector>().Where(s => s.OnMap).ToList() : [];
        var does = wall.Special switch
        {
            Doors.DoorRaise or Doors.DoorOpen or Doors.DoorLockedRaise => targets.Count > 1 ? "opens doors" : "opens a door",
            Lifts.DownWaitUpStay or Lifts.DownWaitUpStayLip => targets.Count > 1 ? "lowers lifts" : "lowers a lift",
            _ => ActionCatalog.Get(wall.Special)?.Name ?? $"action {wall.Special}",
        };
        return new Switch(wall, targets, does, wall.Flag("repeatspecial"));
    }

    /// <summary>The switches that work on <paramref name="area"/>: walls elsewhere used by the player naming its tag.</summary>
    public static IReadOnlyList<Line> Of(MapGeometry geometry, Sector area)
    {
        if (area.Tag == 0) return [];
        var f = geometry.Frame;
        return [.. geometry.LinesNaming(area.Tag).Select(i => f.Lines[i].Element).OfType<Line>().Where(l => NamesAreas(l.Special) && Recognise(geometry, l) is not null)];
    }

    /// <summary>
    /// The piece of <paramref name="wall"/> a switch takes, cut out through <paramref name="edit"/>: <see cref="Width"/>
    /// wide round <paramref name="at"/> (the wall's middle when null), kept on the wall; the whole wall when it is not
    /// much wider than the switch. The wall's texture runs on across the pieces either side.
    /// </summary>
    public static Line Piece(StructureEdit edit, Line wall, MapPoint? at)
    {
        var length = wall.Length;
        if (length <= Width + 16) return wall;
        var (dx, dy) = ((wall.End.X - wall.Start.X) / length, (wall.End.Y - wall.Start.Y) / length);
        var t = at is { } p ? (p.X - wall.Start.X) * dx + (p.Y - wall.Start.Y) * dy : length / 2;
        var from = Math.Clamp(Math.Round(t - Width / 2), 0, length - Width);
        Vertex Corner(double d) => edit.Add(new Vertex { X = wall.Start.X + dx * d, Y = wall.Start.Y + dy * d });
        if (from + Width < length - 1e-6) Walls.Split(edit, wall, Corner(from + Width));
        return from > 1e-6 ? Walls.Split(edit, wall, Corner(from)) : wall;
    }

    /// <summary>
    /// Makes a switch on <paramref name="wall"/> at <paramref name="at"/> that works <paramref name="target"/> (a door
    /// or a lift) the way its own walls do, through <paramref name="edit"/>: a piece of the wall <see cref="Width"/>
    /// wide round the point (the whole wall when it is not much wider) gets the action by the target's tag, used
    /// by the player again and again, and the game's switch picture. Returns the switch, or null and why.
    /// </summary>
    public static (Line? Switch, string? Why) Make(StructureEdit edit, MapGeometry before, Line wall, MapPoint at, Sector target, Look look)
    {
        if (!wall.OnMap || !target.OnMap) return (null, null);
        if (wall.Back is not null) return (null, "A switch goes on a solid wall: click a wall with a room on one side only.");
        if (wall.Special != 0) return (null, "This wall already does something; click a plain wall for the switch.");
        if (ReferenceEquals(wall.Front.Sector, target)) return (null, "Put the switch on a wall outside it, where the player stands to use it.");

        // What the switch does: what the target's own walls do, by its tag.
        var tag = target.Tag != 0 ? target.Tag : before.HighestTag + 1;
        (int Special, int[] Args) action;
        if (Doors.Recognise(before, target) is { } door) action = Doors.Encode(door.Settings, 0, door.Openers.FirstOrDefault()?.Args[1], tag);
        else if (Lifts.Recognise(before, target) is { } lift) action = (Lifts.DownWaitUpStayLip, [tag, lift.Settings.Fast ? Lifts.FastSpeed : Lifts.NormalSpeed, lift.Settings.Delay, 0, 0]);
        else return (null, "A switch opens a door or lowers a lift: select a door or a lift first.");

        var piece = Piece(edit, wall, at);

        if (target.Tag != tag) edit.Set(target, s => s.Tag, (s, v) => s.Tag = v, tag);
        edit.Set(piece, x => x.Special, (x, v) => x.Special = v, action.Special);
        for (var i = 0; i < 5; i++)
        {
            var k = i;
            edit.Set(piece, x => x.Args[k], (x, v) => x.Args[k] = v, i < action.Args.Length ? action.Args[k] : 0);
        }

        edit.Flag(piece, "playeruse", true);
        edit.Flag(piece, "repeatspecial", true);
        edit.Set(piece.Front, s => s.Middle, (s, v) => s.Middle = v, look.Switch);
        edit.Set(piece.Front, s => s.OffsetX, (s, v) => s.OffsetX = v, 0);
        edit.Set(piece.Front, s => s.OffsetY, (s, v) => s.OffsetY = v, 0);
        return (piece, null);
    }
}
