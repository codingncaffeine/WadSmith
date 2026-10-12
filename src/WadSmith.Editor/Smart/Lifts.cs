using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>How a lift behaves, in the Inspector's words.</summary>
/// <param name="Fast">Moves at the fast speed (Doom's "blazing" lifts) instead of the normal one.</param>
/// <param name="Delay">How long it waits down before coming back up, in game tics (35 a second).</param>
public sealed record LiftSettings(bool Fast, int Delay = Lifts.Wait);

/// <summary>
/// A lift found in the map (D21): the area whose floor goes down, waits and comes back up, the walls
/// that set it off, how it behaves and where it goes down to.
/// </summary>
public sealed record Lift(Sector Area, IReadOnlyList<Line> Triggers, LiftSettings Settings, bool Monsters, double GoesDownTo);

/// <summary>
/// Lifts: Doom's lift is an area named by tag (or, without a tag, behind the wall) by
/// Plat_DownWaitUpStay or Plat_DownWaitUpStayLip; its floor drops to the lowest floor beside it, waits,
/// and rises again.
/// </summary>
public static class Lifts
{
    public const int DownWaitUpStay = 62;
    public const int DownWaitUpStayLip = 206;

    /// <summary>The engine's lift speeds (eighths of a unit per tic) and wait (tics): Doom's lifts and its blazing ones.</summary>
    public const int NormalSpeed = 32;
    public const int FastSpeed = 64;
    public const int Wait = 105;

    private static bool IsLift(int special) => special is DownWaitUpStay or DownWaitUpStayLip;

    /// <summary>The lift <paramref name="area"/> is, or null. Looks at the area's walls and the walls naming its tag only.</summary>
    public static Lift? Recognise(MapGeometry geometry, Sector area)
    {
        if (!area.OnMap) return null;
        var walls = WallsOf(geometry, area);
        var triggers = walls.Where(l => ReferenceEquals(l.Back?.Sector, area) && l.Args[0] == 0 && IsLift(l.Special)).ToList();
        if (area.Tag != 0)
        {
            var f = geometry.Frame;
            foreach (var slot in geometry.LinesNaming(area.Tag))
            {
                if (f.Lines[slot].Element is { OnMap: true } l && l.Args[0] == area.Tag && IsLift(l.Special) && !triggers.Contains(l)) triggers.Add(l);
            }
        }

        if (triggers.Count == 0) return null;
        var first = triggers[0];
        var lowest = Neighbours(walls, area).Select(s => s.FloorHeight).DefaultIfEmpty(area.FloorHeight).Min();
        var lip = first.Special == DownWaitUpStayLip ? first.Args[3] : 0;
        var monsters = triggers.Any(l => l.Flag("monsteruse") || l.Flag("monstercross"));
        return new Lift(area, triggers, new LiftSettings(first.Args[1] >= FastSpeed, first.Args[2]), monsters, Math.Min(area.FloorHeight, lowest + lip));
    }

    /// <summary>
    /// Makes a room a lift through <paramref name="edit"/>, as Doom's lifts are made: the room gets a tag
    /// of its own; each wall to a lower room becomes a switch facing that room (a wall is used from its
    /// front), each wall to a room as high or higher lowers it when walked across, by players and
    /// monsters, again and again (Plat_DownWaitUpStayLip, Doom's SR and WR lifts). Walls that already
    /// do something are left alone. Returns why when the room cannot be a lift.
    /// </summary>
    public static string? Make(StructureEdit edit, MapGeometry before, Sector area)
    {
        var walls = WallsOf(before, area);
        if (!walls.Any(l => Other(l, area) is { } o && o.FloorHeight < area.FloorHeight)) return "A lift goes down to a lower floor beside it, and there is none beside this room. Raise its floor first.";
        var tag = area.Tag != 0 ? area.Tag : before.HighestTag + 1;
        edit.Set(area, s => s.Tag, (s, v) => s.Tag = v, tag);
        foreach (var l in walls.Where(l => l.Special == 0 && Other(l, area) is not null).ToList())
        {
            var lower = Other(l, area)!.FloorHeight < area.FloorHeight;
            if (lower && ReferenceEquals(l.Front.Sector, area)) Walls.Turn(edit, l);
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, DownWaitUpStayLip);
            int[] args = [tag, NormalSpeed, Wait, 0, 0];
            for (var i = 0; i < args.Length; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
            }

            edit.Flag(l, lower ? "playeruse" : "playercross", true);
            edit.Flag(l, lower ? "monsteruse" : "monstercross", true);
            edit.Flag(l, "repeatspecial", true);
        }

        return null;
    }

    /// <summary>Changes how a lift behaves: every wall that sets it off gets the same speed and wait; monsters set it off too or not.</summary>
    public static void Change(StructureEdit edit, Lift lift, LiftSettings settings, bool monsters)
    {
        foreach (var l in lift.Triggers)
        {
            var speed = l.Args[1] is not NormalSpeed and not FastSpeed && (l.Args[1] >= FastSpeed) == settings.Fast ? l.Args[1] : settings.Fast ? FastSpeed : NormalSpeed;
            edit.Set(l, x => x.Args[1], (x, v) => x.Args[1] = v, speed);
            edit.Set(l, x => x.Args[2], (x, v) => x.Args[2] = v, settings.Delay);
            edit.Flag(l, l.Flag("playeruse") ? "monsteruse" : "monstercross", monsters);
        }
    }

    private static Sector? Other(Line l, Sector area) => ReferenceEquals(l.Front.Sector, area) ? l.Back?.Sector is { } b && !ReferenceEquals(b, area) ? b : null : l.Back?.Sector == area ? l.Front.Sector : null;

    private static IEnumerable<Sector> Neighbours(List<Line> walls, Sector area) => walls.Select(l => Other(l, area)).OfType<Sector>();

    private static List<Line> WallsOf(MapGeometry geometry, Sector area)
    {
        if (geometry.WallBoundsAt(area.Slot) is not { } b) return [];
        return [.. geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area)))];
    }
}
