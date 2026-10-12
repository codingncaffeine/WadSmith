using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// A teleporter found in the map (D21): a wall that sends whoever crosses it from its front (or uses
/// it) to an arrival spot, named by the spot's id or by the tag of the area it stands in.
/// </summary>
/// <param name="Wall">The wall that teleports.</param>
/// <param name="Arrivals">Where it sends: every arrival spot it names (the game picks the first).</param>
/// <param name="Monsters">Whether monsters are sent too.</param>
/// <param name="Repeats">Whether it works again and again.</param>
/// <param name="ByUse">Set off by using the wall instead of crossing it.</param>
public sealed record Teleporter(Line Wall, IReadOnlyList<Thing> Arrivals, bool Monsters, bool Repeats, bool ByUse);

/// <summary>
/// Teleporters: walls with Teleport or Teleport_NoFog, and the arrival spots they send to
/// (TeleportDest objects: 14, 9043, 9044 in every game). A teleport wall works from its front only, so
/// stepping back off the arrival pad does not send the player back.
/// </summary>
public static class Teleports
{
    public const int Teleport = 70;
    public const int TeleportNoFog = 71;

    /// <summary>The arrival spot's number in every game (TeleportDest).</summary>
    public const int ArrivalType = 14;

    private static readonly HashSet<int> ArrivalTypes = [14, 9043, 9044];

    public static bool IsTeleport(Line l) => l.Special is Teleport or TeleportNoFog;

    public static bool IsArrival(Thing t) => ArrivalTypes.Contains(t.Type);

    /// <summary>The id and the area tag a teleport wall names (Teleport_NoFog keeps the tag third).</summary>
    public static (int Tid, int Tag) Target(Line l) => l.Special == TeleportNoFog ? (l.Args[0], l.Args[2]) : (l.Args[0], l.Args[1]);

    /// <summary>The teleporter <paramref name="wall"/> is, or null.</summary>
    public static Teleporter? Recognise(MapGeometry geometry, Line wall)
    {
        if (!wall.OnMap || !IsTeleport(wall)) return null;
        var (tid, tag) = Target(wall);
        return new Teleporter(wall, ArrivalsFor(geometry, tid, tag), wall.Flag("monstercross") || wall.Flag("monsteruse"), wall.Flag("repeatspecial"), wall.Flag("playeruse"));
    }

    /// <summary>The arrival spots an id and a tag name: the spot with the id (in a tagged area when both are given), else every spot in the tagged areas.</summary>
    public static IReadOnlyList<Thing> ArrivalsFor(MapGeometry geometry, int tid, int tag)
    {
        var f = geometry.Frame;
        var tagged = geometry.SectorsTagged(tag);
        bool InTagged(Thing t) => tag == 0 || tagged.Any(s => geometry.InOrOnArea(s, new MapPoint(t.X, t.Y)));
        if (tid != 0) return [.. geometry.ThingsWithTid(tid).Select(i => f.Things[i].Element).OfType<Thing>().Where(t => t.OnMap && IsArrival(t) && InTagged(t))];
        var found = new List<Thing>();
        foreach (var s in geometry.SectorsTagged(tag))
        {
            if (geometry.WallBoundsAt(s) is not { } b) continue;
            foreach (var i in geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (f.Things[i].Element is { OnMap: true } t && IsArrival(t) && geometry.InOrOnArea(s, new MapPoint(t.X, t.Y)) && !found.Contains(t)) found.Add(t);
            }
        }

        return found;
    }

    /// <summary>The walls that send to an arrival spot: by its id, or by the tag of the area it stands in.</summary>
    public static IReadOnlyList<Line> SendersTo(MapGeometry geometry, Thing arrival)
    {
        var f = geometry.Frame;
        var senders = new List<Line>();
        void Add(IEnumerable<int> slots)
        {
            foreach (var i in slots)
            {
                if (f.Lines[i].Element is { OnMap: true } l && IsTeleport(l) && !senders.Contains(l) && ArrivalsFor(geometry, Target(l).Tid, Target(l).Tag).Contains(arrival)) senders.Add(l);
            }
        }

        Add(geometry.LinesWithArg(0, arrival.Tid));

        // Every tagged area it stands in or on the edge of.
        var at = new MapPoint(arrival.X, arrival.Y);
        foreach (var s in geometry.SectorSlotsIn(at.X - 1, at.Y - 1, at.X + 1, at.Y + 1).Concat(geometry.SectorsWithoutOutline()))
        {
            if (f.Sectors[s] is not { Element: not null, Tag: not 0 } area || !geometry.InOrOnArea(s, at)) continue;
            Add(geometry.LinesWithArg(1, area.Tag));
            Add(geometry.LinesWithArg(2, area.Tag));
        }

        return senders;
    }

    /// <summary>
    /// Makes a room a teleport pad through <paramref name="edit"/>: an arrival spot at
    /// <paramref name="at"/> with an id of its own, facing <paramref name="angle"/>; every wall between the
    /// pad and another room sends whoever steps onto the pad there (Teleport by its id, players and
    /// monsters, again and again), each turned to face out of the pad so it works as the player steps on.
    /// Returns the arrival spot, or null and why.
    /// </summary>
    public static (Thing? Arrival, string? Why) Make(StructureEdit edit, MapGeometry before, Sector pad, MapPoint at, int angle, string engine)
    {
        if (WaysOn(before, pad) is not { Count: > 0 } walls) return (null, "A teleport pad needs a way onto it: a wall it shares with another room.");
        if (ReferenceEquals(before.SectorAt(at), pad)) return (null, "The arrival spot must be somewhere else than the pad itself.");
        if (before.SectorAt(at) is null) return (null, "The arrival spot must be inside a room.");
        var tid = before.HighestTid + 1;
        var arrival = new Thing { X = at.X, Y = at.Y, Type = ArrivalType, Angle = angle, Tid = tid };
        foreach (var flag in Things.Everywhere(engine)) arrival.SetFlag(flag, true);
        edit.Add(arrival);
        foreach (var l in walls)
        {
            if (ReferenceEquals(l.Front.Sector, pad)) Walls.Turn(edit, l);
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, Teleport);
            int[] args = [tid, 0, 0, 0, 0];
            for (var i = 0; i < args.Length; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
            }

            edit.Flag(l, "playercross", true);
            edit.Flag(l, "monstercross", true);
            edit.Flag(l, "repeatspecial", true);
        }

        return (arrival, null);

        // The pad's walls to other rooms that do nothing yet.
        static List<Line> WaysOn(MapGeometry g, Sector room)
        {
            if (g.WallBoundsAt(room.Slot) is not { } b) return [];
            return [.. g.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && l.Special == 0 && l.Back is not null && (ReferenceEquals(l.Front.Sector, room) ^ ReferenceEquals(l.Back.Sector, room)))];
        }
    }
}
