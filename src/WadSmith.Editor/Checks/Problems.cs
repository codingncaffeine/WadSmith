using System.Globalization;
using WadSmith.Core.Games;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Checks;

public enum Severity
{
    /// <summary>The game will go wrong.</summary>
    Error,

    /// <summary>The game will show or play something the author probably did not mean.</summary>
    Warning,
}

/// <summary>
/// Something in the map that would break or spoil it, in a plain sentence (D20): what to show, and a
/// fix when the fix is unambiguous. A rule that finds many of the same counts them in one problem.
/// </summary>
/// <param name="Rule">A short id for the rule, for tests and settings.</param>
/// <param name="Severity">Whether the game will go wrong or only show something unmeant.</param>
/// <param name="Sentence">What is wrong, in the user's words (the glossary's microcopy rules).</param>
/// <param name="Show">The elements to select and bring into view.</param>
/// <param name="FixLabel">The fix's button, a verb ("Anchor them"); null when there is no single right fix.</param>
/// <param name="Fix">Makes the fix through the edit given (on the UI thread, as one undo step).</param>
public sealed record Problem(string Rule, Severity Severity, string Sentence, IReadOnlyList<Element> Show, string? FixLabel = null, Action<StructureEdit>? Fix = null);

/// <summary>
/// The live checks: every rule reads only a geometry's frame (and the game's texture and object lists),
/// so they run on a worker after each change. The fixes they offer change the map's own elements and
/// run on the UI thread.
/// </summary>
public static class Problems
{
    private const double PlayerRadius = 16;

    /// <param name="geometry">The map's shapes, as of one version.</param>
    /// <param name="resources">The game, for texture and object names; null when it was not found (those rules are skipped).</param>
    /// <param name="look">The game's look, for fixes that need a texture.</param>
    /// <param name="engine">The game family ("doom", "hexen"…), for the flags of objects a fix places.</param>
    public static IReadOnlyList<Problem> Check(MapGeometry geometry, GameResources? resources, Look look, string engine)
    {
        var f = geometry.Frame;
        var found = new List<Problem>();
        NoStart(geometry, engine, found);
        ManyStarts(f, found);
        NoExit(f, found);
        Unclosed(f, found);
        MissingTextures(f, look, found);
        if (resources is not null) UnknownTextures(f, resources, found);
        TwoSidedFlag(f, found);
        ThingsOutside(geometry, resources, found);
        StuckInWalls(geometry, resources, found);
        TeleportsNowhere(geometry, found);
        TagsNowhere(geometry, found);
        DoorTracks(geometry, found);
        if (look == Look.For("doom")) MissingKeys(f, found);
        return found;
    }

    private static string Count(int n, string one, string many) => n == 1 ? one : string.Create(CultureInfo.InvariantCulture, $"{n} {many}");

    private static bool Flag(KeyValuePair<string, UdmfValue>[] extra, string name) => extra.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value.Kind == UdmfValueKind.Boolean && kv.Value.AsBool());

    private static void NoStart(MapGeometry g, string engine, List<Problem> found)
    {
        var f = g.Frame;
        if (f.Things.Any(t => t.Element is not null && t.Type == 1)) return;

        // A spot inside the biggest room: a little way in from the middle of one of its walls.
        MapPoint? spot = null;
        var biggest = -1.0;
        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            var room = f.Sides[l.Front].Sector;
            if (room < 0 || g.WallBoundsAt(room) is not { } b) continue;
            var size = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
            if (size <= biggest) continue;
            var (v1, v2) = (f.Vertices[l.V1], f.Vertices[l.V2]);
            var length = Math.Sqrt((v2.X - v1.X) * (v2.X - v1.X) + (v2.Y - v1.Y) * (v2.Y - v1.Y));
            if (length < 64) continue;
            var p = new MapPoint((v1.X + v2.X) / 2 + (v2.Y - v1.Y) / length * 32, (v1.Y + v2.Y) / 2 - (v2.X - v1.X) / length * 32);
            if (!ReferenceEquals(g.SectorAt(p), f.Sectors[room].Element)) continue;
            (spot, biggest) = (new MapPoint(Math.Round(p.X), Math.Round(p.Y)), size);
        }

        found.Add(new Problem("no-start", Severity.Error, "There's no player start, so the game has nowhere to put the player.", [],
            spot is null ? null : "Add one",
            spot is { } here ? e =>
            {
                var start = new Thing { X = here.X, Y = here.Y, Type = 1, Angle = 90 };
                foreach (var flag in Things.Everywhere(engine)) start.SetFlag(flag, true);
                e.Add(start);
            } : null));
    }

    private static void ManyStarts(MapFrame f, List<Problem> found)
    {
        var starts = f.Things.Where(t => t.Element is not null && t.Type == 1).Select(t => (Element)t.Element!).ToList();
        if (starts.Count < 2) return;
        found.Add(new Problem("many-starts", Severity.Warning, string.Create(CultureInfo.InvariantCulture, $"There are {starts.Count} player starts. The game starts the player at the last one, and the others become stand-ins that move with the player (voodoo dolls), which some maps use on purpose."), starts));
    }

    private static void NoExit(MapFrame f, List<Problem> found)
    {
        if (f.Lines.Any(l => l.Element is not null && l.Special is Exits.Normal or Exits.Secret or 74 or 75)) return;
        found.Add(new Problem("no-exit", Severity.Warning, "There's no way to finish the map yet. Select a wall and choose Make it the exit switch.", []));
    }

    private static void Unclosed(MapFrame f, List<Problem> found)
    {
        // An area is closed when, at every corner, as many of its walls arrive as leave.
        var balance = new Dictionary<(int Sector, int Vertex), int>();
        void Tally(int sector, int from, int to)
        {
            if (sector < 0) return;
            balance[(sector, from)] = balance.GetValueOrDefault((sector, from)) + 1;
            balance[(sector, to)] = balance.GetValueOrDefault((sector, to)) - 1;
        }

        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            Tally(f.Sides[l.Front].Sector, l.V1, l.V2);
            if (l.Back >= 0) Tally(f.Sides[l.Back].Sector, l.V2, l.V1);
        }

        var open = balance.Where(kv => kv.Value != 0).Select(kv => kv.Key.Sector).Distinct().Order().Select(s => (Element)f.Sectors[s].Element!).Where(e => e is not null).ToList();
        if (open.Count == 0) return;
        found.Add(new Problem("unclosed", Severity.Warning, $"{Count(open.Count, "A room isn't", "rooms aren't")} closed: a gap in its walls lets the game draw it wrongly or leak it into its neighbours.", open));
    }

    /// <summary>The parts of each face the game draws, and whether each has no texture.</summary>
    private static IEnumerable<(LineFrame Line, SideFrame Side, bool Top, bool Middle, bool Bottom)> Untextured(MapFrame f)
    {
        static bool None(string t) => t is "-" or "";
        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            var front = f.Sides[l.Front];
            if (l.Back < 0)
            {
                if (front.Element is not null && None(front.Middle)) yield return (l, front, false, true, false);
                continue;
            }

            var back = f.Sides[l.Back];
            if (front.Sector < 0 || back.Sector < 0) continue;
            foreach (var (side, here, there) in new[] { (front, f.Sectors[front.Sector], f.Sectors[back.Sector]), (back, f.Sectors[back.Sector], f.Sectors[front.Sector]) })
            {
                var top = here.CeilingHeight > there.CeilingHeight && !(MapScene.IsSky(here.CeilingTexture) && MapScene.IsSky(there.CeilingTexture)) && None(side.Top);
                var bottom = there.FloorHeight > here.FloorHeight && None(side.Bottom);
                if ((top || bottom) && side.Element is not null) yield return (l, side, top, false, bottom);
            }
        }
    }

    private static void MissingTextures(MapFrame f, Look look, List<Problem> found)
    {
        var missing = Untextured(f).ToList();
        if (missing.Count == 0) return;
        var faces = missing.Select(m => (m.Side.Element!, m.Top, m.Middle, m.Bottom)).ToList();
        found.Add(new Problem("missing-texture", Severity.Warning, $"{Count(missing.Select(m => m.Line.Element).Distinct().Count(), "A wall has", "walls have")} a part the game shows with no texture on it, drawn as a stretch of whatever is behind it.",
            [.. missing.Select(m => (Element)m.Line.Element!).Distinct()], "Use the room's texture", e =>
            {
                foreach (var (side, top, middle, bottom) in faces)
                {
                    if (!side.OnMap) continue;
                    var texture = new[] { side.Middle, side.Top, side.Bottom }.FirstOrDefault(t => t is not ("-" or "")) ?? look.Wall;
                    if (top) e.Set(side, s => s.Top, (s, v) => s.Top = v, texture);
                    if (middle) e.Set(side, s => s.Middle, (s, v) => s.Middle = v, texture);
                    if (bottom) e.Set(side, s => s.Bottom, (s, v) => s.Bottom = v, texture);
                }
            }));
    }

    private static void UnknownTextures(MapFrame f, GameResources r, List<Problem> found)
    {
        bool Known(string t) => t is "-" or "" || r.Textures.FindWall(t) is not null || r.Textures.FindFlat(t) is not null;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var where = new List<Element>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            foreach (var s in l.Back >= 0 ? new[] { f.Sides[l.Front], f.Sides[l.Back] } : [f.Sides[l.Front]])
            {
                foreach (var t in new[] { s.Top, s.Middle, s.Bottom }.Where(t => !Known(t)))
                {
                    names.Add(t);
                    if (!where.Contains(l.Element)) where.Add(l.Element);
                }
            }
        }

        foreach (var s in f.Sectors)
        {
            if (s.Element is null) continue;
            foreach (var t in new[] { s.FloorTexture, s.CeilingTexture }.Where(t => !Known(t)))
            {
                names.Add(t);
                if (!where.Contains(s.Element)) where.Add(s.Element);
            }
        }

        if (names.Count == 0) return;
        var list = string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", …" : "");
        found.Add(new Problem("unknown-texture", Severity.Error, $"{Count(where.Count, "A wall or floor uses", "walls and floors use")} a picture the game does not have ({list}); the game shows a checkerboard there.", where));
    }

    private static void TwoSidedFlag(MapFrame f, List<Problem> found)
    {
        var lines = f.Lines.Where(l => l.Element is not null && l.Back >= 0 && !Flag(l.Extra, "twosided")).Select(l => l.Element!).ToList();
        if (lines.Count == 0) return;
        found.Add(new Problem("two-sided", Severity.Warning, $"{Count(lines.Count, "A wall has", "walls have")} rooms on both sides but {(lines.Count == 1 ? "isn't" : "aren't")} marked as open, so the game draws {(lines.Count == 1 ? "its" : "their")} middle texture solid.",
            [.. lines], "Mark them open", e =>
            {
                foreach (var l in lines.Where(l => l.OnMap)) e.Flag(l, "twosided", true);
            }));
    }

    /// <summary>Object types that belong outside the rooms or in walls: polyobject anchors and start spots (Hexen).</summary>
    private static bool PlacedAnywhere(int type) => type is >= 9300 and <= 9303;

    private static void ThingsOutside(MapGeometry g, GameResources? r, List<Problem> found)
    {
        var f = g.Frame;
        var outside = f.Things.Where(t => t.Element is not null && !PlacedAnywhere(t.Type) && r?.Things.Find(t.Type)?.Category is not ThingCategory.Marker && g.SectorAt(new MapPoint(t.X, t.Y)) is null).Select(t => (Element)t.Element!).ToList();
        if (outside.Count == 0) return;
        found.Add(new Problem("outside", Severity.Warning, $"{Count(outside.Count, "A thing stands", "things stand")} outside every room, where the game cannot use {(outside.Count == 1 ? "it" : "them")}.", outside));
    }

    private static void StuckInWalls(MapGeometry g, GameResources? r, List<Problem> found)
    {
        var f = g.Frame;
        var stuck = new List<(Thing Thing, double Dx, double Dy)>();
        foreach (var t in f.Things)
        {
            if (t.Element is null) continue;
            var info = r?.Things.Find(t.Type);
            var mover = t.Type is >= 1 and <= 4 || info?.Category == ThingCategory.Monster;
            if (!mover) continue;
            var radius = t.Type is >= 1 and <= 4 ? PlayerRadius : info!.Radius;
            var p = new MapPoint(t.X, t.Y);

            // The deepest a solid wall reaches into the thing, and which way pushes it out.
            var (best, nx, ny) = (0.0, 0.0, 0.0);
            foreach (var slot in g.LineSlotsIn(t.X - radius, t.Y - radius, t.X + radius, t.Y + radius))
            {
                var l = f.Lines[slot];
                if (l.Back >= 0 && !Flag(l.Extra, "blocking")) continue;
                var (a, b) = (new MapPoint(f.Vertices[l.V1].X, f.Vertices[l.V1].Y), new MapPoint(f.Vertices[l.V2].X, f.Vertices[l.V2].Y));
                var d = MapGeometry.DistanceToSegment(p, a, b);
                if (radius - d <= 1 || radius - d <= best) continue;
                var length = a.DistanceTo(b);
                if (length <= 0) continue;

                // Out toward the side of the wall its room is on (front: right of a to b).
                var (rx, ry) = ((b.Y - a.Y) / length, -(b.X - a.X) / length);
                var side = (p.X - a.X) * rx + (p.Y - a.Y) * ry >= 0 ? 1 : -1;
                (best, nx, ny) = (radius - d, rx * side, ry * side);
            }

            if (best > 0) stuck.Add((t.Element, nx * (best + 1), ny * (best + 1)));
        }

        if (stuck.Count == 0) return;
        found.Add(new Problem("stuck", Severity.Error, $"{Count(stuck.Count, "A monster or player start stands", "monsters or player starts stand")} partly inside a wall and cannot move.",
            [.. stuck.Select(s => (Element)s.Thing)], "Nudge them out", e =>
            {
                foreach (var (thing, dx, dy) in stuck.Where(s => s.Thing.OnMap))
                {
                    e.Set(thing, x => x.X, (x, v) => x.X = v, Math.Round(thing.X + dx));
                    e.Set(thing, x => x.Y, (x, v) => x.Y = v, Math.Round(thing.Y + dy));
                }
            }));
    }

    private static void TeleportsNowhere(MapGeometry g, List<Problem> found)
    {
        var f = g.Frame;
        var nowhere = new List<Element>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || l.Special is not (Teleports.Teleport or Teleports.TeleportNoFog)) continue;
            var (tid, tag) = l.Special == Teleports.TeleportNoFog ? (l.Args[0], l.Args[2]) : (l.Args[0], l.Args[1]);
            if (!ArrivalExists(g, tid, tag)) nowhere.Add(l.Element);
        }

        if (nowhere.Count == 0) return;
        found.Add(new Problem("teleport-nowhere", Severity.Warning, $"{Count(nowhere.Count, "A teleporter sends", "teleporters send")} nowhere: no arrival spot has {(nowhere.Count == 1 ? "its" : "their")} id or stands in an area with {(nowhere.Count == 1 ? "its" : "their")} tag.", nowhere));
    }

    /// <summary>Walls whose action works on the areas with a tag (a switch, a line walked over) when no area has that tag.</summary>
    private static void TagsNowhere(MapGeometry g, List<Problem> found)
    {
        var nowhere = g.Frame.Lines.Where(l => l.Element is not null && l.Special != 0 && l.Args[0] != 0 && Switches.NamesAreas(l.Special) && g.SectorsTagged(l.Args[0]).Count == 0).ToList();
        if (nowhere.Count == 0) return;
        var tags = nowhere.Select(l => l.Args[0]).Distinct().Order().ToList();
        var named = tags.Count == 1 ? string.Create(CultureInfo.InvariantCulture, $"tag {tags[0]}") : $"tags {string.Join(", ", tags.Take(5))}{(tags.Count > 5 ? "…" : "")}";
        found.Add(new Problem("tag-nowhere", Severity.Warning, $"{Count(nowhere.Count, "A wall's action works on", "walls' actions work on")} {named}, and no area has {(tags.Count == 1 ? "it" : "them")}: {(nowhere.Count == 1 ? "it does" : "they do")} nothing. Give the area the tag in the Inspector, or add the switch again from the door or lift.", [.. nowhere.Select(l => l.Element!)]));
    }

    private static bool ArrivalExists(MapGeometry g, int tid, int tag)
    {
        var f = g.Frame;
        static bool Arrival(int type) => type is Teleports.ArrivalType or 9043 or 9044;
        bool InTagged(ThingFrame t) => tag == 0 || g.SectorsTagged(tag).Any(s => g.InOrOnArea(s, new MapPoint(t.X, t.Y)));
        if (tid != 0) return g.ThingsWithTid(tid).Any(i => f.Things[i] is { Element: not null } t && Arrival(t.Type) && InTagged(t));
        foreach (var s in g.SectorsTagged(tag))
        {
            if (g.WallBoundsAt(s) is not { } b) continue;
            if (g.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Any(i => Arrival(f.Things[i].Type) && g.InOrOnArea(s, new MapPoint(f.Things[i].X, f.Things[i].Y)))) return true;
        }

        return false;
    }

    private static void DoorTracks(MapGeometry g, List<Problem> found)
    {
        var f = g.Frame;

        // Doors opened from their own walls: the areas behind walls with a door action naming no tag.
        var doors = new HashSet<int>();
        foreach (var l in f.Lines)
        {
            if (l.Element is not null && l.Back >= 0 && l.Args[0] == 0 && l.Special is Doors.DoorOpen or Doors.DoorRaise or Doors.DoorLockedRaise) doors.Add(f.Sides[l.Back].Sector);
        }

        var sliding = f.Lines.Where(l => l.Element is not null && l.Back < 0 && doors.Contains(f.Sides[l.Front].Sector) && f.Sides[l.Front].Middle is not ("-" or "") && !Flag(l.Extra, "dontpegbottom")).Select(l => l.Element!).ToList();
        if (sliding.Count == 0) return;
        found.Add(new Problem("door-tracks", Severity.Warning, $"{Count(sliding.Count, "A door's side wall slides", "doors' side walls slide")} up with the door as it opens, because {(sliding.Count == 1 ? "it isn't" : "they aren't")} anchored at the bottom.",
            [.. sliding], "Anchor them", e =>
            {
                foreach (var l in sliding.Where(l => l.OnMap)) e.Flag(l, "dontpegbottom", true);
            }));
    }

    private static void MissingKeys(MapFrame f, List<Problem> found)
    {
        // Doom's keys by colour: card and skull. Locks 1–6 need that one; 129–131 either of a colour.
        int[][] keys = [[13, 38], [5, 40], [6, 39]];
        string[] colours = ["red", "blue", "yellow"];
        var present = f.Things.Where(t => t.Element is not null).Select(t => t.Type).ToHashSet();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || l.Special != Doors.DoorLockedRaise) continue;
            var lockNumber = l.Args[3];
            int[] needed = lockNumber switch
            {
                1 => [13], 2 => [5], 3 => [6], 4 => [38], 5 => [40], 6 => [39],
                129 => keys[0], 130 => keys[1], 131 => keys[2],
                _ => [],
            };
            if (needed.Length == 0 || needed.Any(present.Contains)) continue;
            var colour = colours[Array.FindIndex(keys, k => k.Contains(needed[0]))];
            if (found.Any(p => p.Rule == "missing-key-" + colour)) continue;
            var doorsOfColour = f.Lines.Where(x => x.Element is not null && x.Special == Doors.DoorLockedRaise && x.Args[3] == lockNumber).Select(x => (Element)x.Element!).ToList();
            found.Add(new Problem("missing-key-" + colour, Severity.Error, $"The {colour} key door can never open: there's no {colour} key in the map.", doorsOfColour));
        }
    }
}
