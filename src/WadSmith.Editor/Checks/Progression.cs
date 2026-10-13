using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Checks;

/// <summary>One step of the way from the start to the exit: a key picked up, a locked door opened, the exit reached.</summary>
/// <param name="Kind">"key", "door" or "exit".</param>
/// <param name="Colour">The key's or the door's colour; null for the exit.</param>
/// <param name="At">Where it is, for Show.</param>
/// <param name="Element">The key, the door's face or the exit's wall.</param>
public sealed record ProgressionStep(string Kind, string? Colour, MapPoint At, Element Element);

/// <summary>What the walk from the start found: the areas reached, the steps in order, and the keys and exits it never reached.</summary>
public sealed record ProgressionResult(IReadOnlySet<int> Reached, IReadOnlyList<ProgressionStep> Steps, IReadOnlyList<(string Colour, IReadOnlyList<Element> Keys)> KeysOutOfReach, bool HasExit, bool ExitReached, IReadOnlyList<(string Colour, MapPoint At, Element Wall)> LockedOnTheWay);

/// <summary>
/// The Progression graph (D29): a walk from the player start through every way between areas (two-sided walls that do not
/// block, teleporters both ways), where a locked door's face waits until a key that opens it is picked up. Generous on
/// purpose (heights, lifts and moving floors are not modelled), so the games' own maps never raise a false alarm while a
/// key behind its own door, or an exit behind a door whose key cannot be had, is found. Reads a frame: a worker runs it.
/// </summary>
public static class Progression
{
    private const double PlayerRadius = 16;

    /// <summary>How far the player reaches to use a wall (the engine's use range).</summary>
    private const double UseRange = 64;

    public static ProgressionResult Walk(MapGeometry g, string family = "doom")
    {
        var f = g.Frame;
        int? AreaAt(double x, double y) => g.SectorAt(new MapPoint(x, y)) is { } s ? s.Slot : null;
        MapPoint Middle(LineFrame l) => new((f.Vertices[l.V1].X + f.Vertices[l.V2].X) / 2, (f.Vertices[l.V1].Y + f.Vertices[l.V2].Y) / 2);
        var exits = f.Lines.Where(l => l.Element is not null && l.Special is Exits.Normal or Exits.Secret).ToList();
        var start = f.Things.LastOrDefault(t => t.Element is not null && t.Type == 1);
        if (start.Element is null || AreaAt(start.X, start.Y) is not { } from) return new(new HashSet<int>(), [], [], exits.Count > 0, false, []);

        // A key is picked up from any area the player reaches within arm's length of it.
        HashSet<int> Reach(double x, double y, double[]? rings = null)
        {
            var near = new HashSet<int>();
            if (AreaAt(x, y) is { } own) near.Add(own);
            for (var i = 0; i < 16; i++)
            {
                var angle = i * Math.PI / 8;
                foreach (var r in rings ?? [20.0, PlayerRadius + 20])
                {
                    if (AreaAt(x + Math.Cos(angle) * r, y + Math.Sin(angle) * r) is { } a) near.Add(a);
                }
            }

            return near;
        }

        var keys = f.Things.Where(t => t.Element is not null && Keys.ColourOf(t.Type, family) is not null && AreaAt(t.X, t.Y) is not null).Select(t => (t.Type, Areas: Reach(t.X, t.Y), Element: (Element)t.Element!, At: new MapPoint(t.X, t.Y))).ToList();

        // In Hexen a key is carried between the maps of a hub: a door whose key is on none of this map's objects opens
        // with one brought from another map.
        var hub = family == "hexen";
        var onMap = keys.Select(k => k.Type).ToHashSet();

        // The ways between areas: walls with two sides that do not block (into a door through a locked face of it, only
        // with the key), and teleporters (both ways).
        var ways = new Dictionary<int, List<(int To, int Lock, int Line)>>();
        void Way(int a, int b, int lockAtoB, int line)
        {
            if (a == b) return;
            if (!ways.TryGetValue(a, out var list)) ways[a] = list = [];
            list.Add((b, lockAtoB, line));
            if (!ways.TryGetValue(b, out var back)) ways[b] = back = [];
            back.Add((a, 0, line));
        }

        for (var i = 0; i < f.Lines.Count; i++)
        {
            var l = f.Lines[i];
            if (l.Element is null) continue;
            if (l.Back >= 0 && !Problems.Flag(l.Extra, "blocking") && !Problems.Flag(l.Extra, "blockplayers") && !Problems.Flag(l.Extra, "blockeverything"))
            {
                // A door's face: used from its front, it opens the door behind it, locked or not.
                var locked = l.Special == Doors.DoorLockedRaise && l.Args[0] == 0 && Keys.Opening(l.Args[3], family).Count > 0 ? l.Args[3] : 0;
                if (hub && locked != 0 && !Keys.Opening(locked, family).Any(onMap.Contains)) locked = 0;
                Way(f.Sides[l.Front].Sector, f.Sides[l.Back].Sector, locked, i);
            }

            if (l.Special is Teleports.Teleport or Teleports.TeleportNoFog)
            {
                var (tid, tag) = l.Special == Teleports.TeleportNoFog ? (l.Args[0], l.Args[2]) : (l.Args[0], l.Args[1]);
                var to = tid != 0 ? g.ThingsWithTid(tid).Select(t => AreaAt(f.Things[t].X, f.Things[t].Y)).OfType<int>() : g.SectorsTagged(tag);
                foreach (var a in to) Way(f.Sides[l.Front].Sector, a, 0, i);
            }
        }

        // The walk: a way through a locked face waits until a key that opens it is picked up.
        var reached = new HashSet<int> { from };
        var held = new HashSet<int>();
        var colours = new HashSet<string>();
        var steps = new List<ProgressionStep>();
        var waiting = new List<(int To, int Lock, int Line)>();
        var queue = new Queue<int>();
        void Arrive(int area)
        {
            foreach (var k in keys.Where(k => k.Areas.Contains(area) && held.Add(k.Type)))
            {
                if (Keys.ColourOf(k.Type, family) is { } colour && colours.Add(colour)) steps.Add(new("key", colour, k.At, k.Element));
            }

            queue.Enqueue(area);
        }

        Arrive(from);
        while (queue.Count > 0)
        {
            var area = queue.Dequeue();
            foreach (var (next, lockNumber, line) in ways.GetValueOrDefault(area) ?? [])
            {
                if (reached.Contains(next)) continue;
                if (lockNumber != 0 && !Keys.Opening(lockNumber, family).Any(held.Contains))
                {
                    waiting.Add((next, lockNumber, line));
                    continue;
                }

                reached.Add(next);
                if (lockNumber != 0) steps.Add(new("door", Keys.ColourOfLock(lockNumber, family), Middle(f.Lines[line]), f.Lines[line].Element!));
                Arrive(next);
            }

            if (queue.Count > 0) continue;
            foreach (var (door, lockNumber, line) in waiting.Where(w => !reached.Contains(w.To) && Keys.Opening(w.Lock, family).Any(held.Contains)).ToList())
            {
                if (!reached.Add(door)) continue;
                steps.Add(new("door", Keys.ColourOfLock(lockNumber, family), Middle(f.Lines[line]), f.Lines[line].Element!));
                Arrive(door);
            }
        }

        // An exit switch is used from wherever the player stands within reach of it, even across a fence (an impassable line).
        var exitReached = exits.FirstOrDefault(l => reached.Contains(f.Sides[l.Front].Sector) || l.Back >= 0 && reached.Contains(f.Sides[l.Back].Sector)
            || Reach(Middle(l).X, Middle(l).Y, [UseRange / 2, UseRange - 8]).Overlaps(reached));
        if (exitReached.Element is not null) steps.Add(new("exit", null, Middle(exitReached), exitReached.Element));

        var outOfReach = new List<(string, IReadOnlyList<Element>)>();
        foreach (var (colour, kinds, _) in Keys.Of(family))
        {
            var these = keys.Where(k => kinds.Contains(k.Type)).ToList();
            if (these.Count > 0 && !these.Any(k => k.Areas.Overlaps(reached))) outOfReach.Add((colour, [.. these.Select(k => k.Element)]));
        }

        // The locked doors still shut at the edge of where the player got: what stands between them and the rest.
        var shut = waiting.Where(w => !reached.Contains(w.To)).GroupBy(w => w.Lock).Select(g => (Keys.ColourOfLock(g.Key, family) ?? "locked", Middle(f.Lines[g.First().Line]), (Element)f.Lines[g.First().Line].Element!)).ToList();
        return new(reached, steps, outOfReach, exits.Count > 0, exitReached.Element is not null, shut);
    }
}
