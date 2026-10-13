using System.Globalization;
using WadSmith.Core.Games;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
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

/// <summary>The rules a classic target adds (6b): the game's classic namespace, the engine's table for it, and the target's name.</summary>
public sealed record ClassicRules(string Namespace, XlatTranslator Xlat, string Target);

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
    /// <param name="fit">The gaps and low openings the map started with (a remix's), left out of the passage checks; null when it started empty.</param>
    /// <param name="classic">A classic target's rules (what has no classic type is listed); null for GZDoom.</param>
    public static IReadOnlyList<Problem> Check(MapGeometry geometry, GameResources? resources, Look look, string engine, FitBaseline? fit = null, ClassicRules? classic = null)
    {
        var f = geometry.Frame;
        var found = new List<Problem>();
        if (classic is not null) NotClassic(f, classic, found);
        NoStart(geometry, engine, found);
        ManyStarts(f, found);
        NoExit(f, found);
        Unclosed(f, found);
        MissingTextures(f, look, found);
        if (resources is not null) UnknownTextures(f, resources, found);
        if (resources is not null) CannotShow(f, resources, found);
        TwoSidedFlag(f, found);
        ThingsOutside(geometry, resources, found);
        StuckInWalls(geometry, resources, found);
        TeleportsNowhere(geometry, found);
        TagsNowhere(geometry, found);
        LowDoors(geometry, found);
        DoorTracks(geometry, found);
        if (fit?.IsPending != true)
        {
            NarrowPassages(geometry, fit ?? FitBaseline.None, found);
            LowPassages(geometry, fit ?? FitBaseline.None, found);
        }

        var walk = Progression.Walk(geometry);
        ExitOutOfReach(geometry, walk, found);
        if (look == Look.For("doom"))
        {
            MissingKeys(f, found);
            KeysOutOfReach(geometry, walk, found);
        }

        return found;
    }

    private static string Count(int n, string one, string many) => n == 1 ? one : string.Create(CultureInfo.InvariantCulture, $"{n} {many}");

    internal static bool Flag(KeyValuePair<string, UdmfValue>[] extra, string name) => extra.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value.Kind == UdmfValueKind.Boolean && kv.Value.AsBool());

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

    /// <summary>
    /// What a classic target cannot hold (6b): a wall whose action with its settings, or a room whose action, has no type
    /// in the classic format, read backwards from the engine's own table. Written plain in the file, so the map would
    /// play in GZDoom only.
    /// </summary>
    private static void NotClassic(MapFrame f, ClassicRules rules, List<Problem> found)
    {
        var target = Targets.TitleOf(rules.Target);
        static bool On(KeyValuePair<string, UdmfValue>[] extra, string name) => extra.Any(e => e.Key == name && e.Value.AsBool());
        var walls = f.Lines.Where(l => l.Element is not null && l.Special != 0 && ClassicTranslation.TypeOf(l.Special, l.Args, name => On(l.Extra, name), l.LineId, rules.Xlat) is null).ToList();
        foreach (var group in walls.GroupBy(l => l.Special))
        {
            var action = ActionCatalog.Get(group.Key)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"action {group.Key}");
            var elements = group.Select(l => l.Element!).ToList();
            found.Add(new Problem("not-classic", Severity.Warning, $"{(elements.Count == 1 ? "A wall's" : string.Create(CultureInfo.InvariantCulture, $"{elements.Count} walls'"))} action, {action} with these settings, has no type in {target}: the map would play in GZDoom only. Make the object again, or change what the project plays in under Map settings.", elements));
        }

        var rooms = f.Sectors.Where(s => s.Element is not null && ClassicTranslation.SectorTypeOf(s.Special, On(s.Extra, "secret"), rules.Xlat) is null).Select(s => s.Element!).ToList();
        if (rooms.Count > 0) found.Add(new Problem("not-classic", Severity.Warning, $"{(rooms.Count == 1 ? "A room's" : string.Create(CultureInfo.InvariantCulture, $"{rooms.Count} rooms'"))} action has no type in {target} (a secret with another action, or an action the classic engine lacks): the map would play in GZDoom only.", rooms));
    }

    /// <summary>
    /// Objects the game's own data cannot show: another game's (Doom II's super shotgun in Doom), or none the game knows.
    /// The game shows an error for each; the fix takes them out.
    /// </summary>
    private static void CannotShow(MapFrame f, GameResources resources, List<Problem> found)
    {
        foreach (var group in f.Things.Where(t => t.Element is not null && !resources.CanShow(t.Type)).GroupBy(t => t.Type).OrderBy(g => g.Key))
        {
            var things = group.Select(t => t.Element!).ToList();
            var known = resources.Things.Find(group.Key);
            var name = known?.Name ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"object {group.Key}");
            var what = things.Count == 1 ? name : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{things.Count} × {name}");
            var why = known is null ? $"is not an object {resources.Game.Title} knows" : $"is not in {resources.Game.Title}: the game has no picture for it";
            var them = things.Count == 1 ? "it" : "them";
            found.Add(new Problem("cannot-show", Severity.Warning, $"{what} {why}, so the game shows an error there. Take {them} out, or make the map for the game {(things.Count == 1 ? "it belongs" : "they belong")} to.", things,
                things.Count == 1 ? "Take it out" : "Take them out", e =>
                {
                    foreach (var t in things) e.Remove(t);
                }));
        }
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
        var seen = new HashSet<Element>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            foreach (var s in l.Back >= 0 ? new[] { f.Sides[l.Front], f.Sides[l.Back] } : [f.Sides[l.Front]])
            {
                foreach (var t in new[] { s.Top, s.Middle, s.Bottom }.Where(t => !Known(t)))
                {
                    names.Add(t);
                    if (seen.Add(l.Element)) where.Add(l.Element);
                }
            }
        }

        foreach (var s in f.Sectors)
        {
            if (s.Element is null) continue;
            foreach (var t in new[] { s.FloorTexture, s.CeilingTexture }.Where(t => !Known(t)))
            {
                names.Add(t);
                if (seen.Add(s.Element)) where.Add(s.Element);
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

    private const double PlayerHeight = 56;

    /// <summary>The areas beside an area (across its two-sided walls), from the frame.</summary>
    private static IEnumerable<int> Beside(MapGeometry g, int area)
    {
        var f = g.Frame;
        if (g.WallBoundsAt(area) is not { } b) yield break;
        foreach (var i in g.LineSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            var l = f.Lines[i];
            if (l.Element is null || l.Back < 0) continue;
            var (front, back) = (f.Sides[l.Front].Sector, f.Sides[l.Back].Sector);
            if (front == area && back != area) yield return back;
            else if (back == area && front != area) yield return front;
        }
    }

    /// <summary>
    /// Doors that open less than the player's height: the game opens a door to 4 below the lowest ceiling beside it.
    /// Only doors opened from their own walls (doors opened by tag are as often machinery as a way through), at least as
    /// wide as the player (32), that start shut and that the player walks up to (from a room the player stands in, its
    /// floor within 24), and the ceilings of open areas beside that no action moves (a shut door beside is opened first
    /// in play, a ceiling an action raises is raised).
    /// </summary>
    private static void LowDoors(MapGeometry g, List<Problem> found)
    {
        var f = g.Frame;
        var doors = new HashSet<int>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || l.Special is not (Doors.DoorOpen or Doors.DoorRaise or Doors.DoorLockedRaise)) continue;
            if (l.Args[0] == 0 && l.Back >= 0) doors.Add(f.Sides[l.Back].Sector);
        }

        var moved = new HashSet<int>();
        foreach (var l in f.Lines)
        {
            if (l.Element is not null && l.Special != 0 && l.Args[0] != 0 && Switches.NamesAreas(l.Special)) moved.Add(l.Args[0]);
        }

        var low = new List<(Element Door, double Opens)>();
        foreach (var d in doors)
        {
            var door = f.Sectors[d];
            if (door.Element is null || door.CeilingHeight > door.FloorHeight || g.WallBoundsAt(d) is not { } box || Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY) < 2 * PlayerRadius) continue;
            var open = Beside(g, d).Select(s => f.Sectors[s]).Where(s => s.CeilingHeight > s.FloorHeight && (s.Tag == 0 || !moved.Contains(s.Tag))).ToList();
            if (!open.Any(s => door.FloorHeight - s.FloorHeight <= 24 && s.CeilingHeight - s.FloorHeight >= PlayerHeight)) continue;
            var ceilings = open.Select(s => s.CeilingHeight).ToList();
            var opens = ceilings.Min() - 4 - door.FloorHeight;
            if (opens < PlayerHeight) low.Add((door.Element, opens));
        }

        if (low.Count == 0) return;
        var least = low.Min(x => x.Opens);
        found.Add(new Problem("door-too-low", Severity.Warning, string.Create(CultureInfo.InvariantCulture, $"{Count(low.Count, "A door opens", "doors open")} only {(low.Count == 1 ? $"{least:0}" : $"as little as {least:0}")} high, and the player is 56 tall: a door opens to 4 below the lowest ceiling beside it, so raise that ceiling."), [.. low.Select(x => x.Door)]));
    }

    /// <summary>
    /// Doorways and corridors the player cannot get through (D20): narrower than the player's box across them (32, more on a
    /// slant). A post beside a wall is left to the plan's marks: posts set close together are how bars are made.
    /// </summary>
    private static void NarrowPassages(MapGeometry g, FitBaseline baseline, List<Problem> found)
    {
        var f = g.Frame;
        var narrow = Fit.Gaps(g).Where(x => x.Kind != GapKind.Pinch && !baseline.Had(x)).ToList();
        if (narrow.Count == 0) return;
        var least = narrow.MinBy(x => x.Width)!;
        var walls = narrow.SelectMany(x => (int[])[x.WallA, x.WallB]).Distinct().Select(i => f.Lines[i].Element).OfType<Element>().ToList();
        var sentence = narrow.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"A way through is only {least.Width:0} across, and the player needs {least.Needed:0} to get through{(least.Needed > 2 * Fit.Radius + 0.5 ? " on this slant (the player's box is square)" : "")}.")
            : string.Create(CultureInfo.InvariantCulture, $"{narrow.Count} ways through are too narrow for the player: as little as {least.Width:0} across, where the player needs 32 (more on a slant). The plan marks them in red.");
        found.Add(new Problem("passage-too-narrow", Severity.Warning, sentence, walls));
    }

    /// <summary>Openings between two rooms the player stands in that are lower than the player, with the fix: raise the lower ceiling.</summary>
    private static void LowPassages(MapGeometry g, FitBaseline baseline, List<Problem> found)
    {
        var f = g.Frame;
        var low = Fit.LowPassages(g).Where(x => !baseline.Had(x, f)).ToList();
        if (low.Count == 0) return;
        var walls = low.Select(x => f.Lines[x.Wall].Element).OfType<Line>().ToList();
        var least = low.Min(x => x.Opening);
        var sentence = low.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"An opening between two rooms is only {least:0} high, and the player is 56 tall: the player stops there.")
            : string.Create(CultureInfo.InvariantCulture, $"{low.Count} openings between rooms are lower than the player (as little as {least:0} high; the player is 56 tall).");
        found.Add(new Problem("passage-too-low", Severity.Warning, sentence, walls, "Raise the ceilings", e =>
        {
            foreach (var l in walls.Where(l => l.OnMap && l.Back is not null))
            {
                var (a, b) = (l.Front.Sector, l.Back!.Sector);
                var lower = a.CeilingHeight <= b.CeilingHeight ? a : b;
                var needed = Math.Max(a.FloorHeight, b.FloorHeight) + Fit.Height;
                if (lower.CeilingHeight < needed) e.Set(lower, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, needed);
            }
        }));
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

    /// <summary>
    /// Keys the player cannot reach from the start (the last player 1 start: the others are voodoo dolls). The walk
    /// is generous, so what it reports is certain: heights, lifts and switches are taken as no obstacle and
    /// teleporters go both ways; only walls that block, and a door's face locked with a key not yet picked up,
    /// stop it (a door locked on one face opens freely from the other). A key is picked up from any area within the
    /// player's reach of it (16 + the key's 20), as the game does across a railing.
    /// </summary>
    private static void KeysOutOfReach(MapGeometry g, ProgressionResult walk, List<Problem> found)
    {
        foreach (var (colour, keys) in walk.KeysOutOfReach)
        {
            var doors = g.Frame.Lines.Where(l => l.Element is not null && l.Special == Doors.DoorLockedRaise && Keys.ColourOfLock(l.Args[3]) == colour).Select(l => (Element)l.Element!).ToList();
            found.Add(new Problem("key-out-of-reach-" + colour, Severity.Error, $"The {colour} key can't be reached from the player start: every way to it goes through a locked door the player can't open first.", [.. keys, .. doors]));
        }
    }

    /// <summary>An exit the walk from the start never reaches (D29): behind a locked door whose key can't be had, or with no way to it at all.</summary>
    private static void ExitOutOfReach(MapGeometry g, ProgressionResult walk, List<Problem> found)
    {
        if (!walk.HasExit || walk.ExitReached || walk.Reached.Count == 0) return;
        var exits = g.Frame.Lines.Where(l => l.Element is not null && l.Special is Exits.Normal or Exits.Secret).Select(l => (Element)l.Element!).ToList();
        var shut = walk.LockedOnTheWay;
        var why = shut.Count == 0
            ? "no way from the start leads to it"
            : $"every way to it goes through the {string.Join(" or the ", shut.Select(s => s.Colour).Distinct())} door, and the player can't get its key first";
        found.Add(new Problem("exit-out-of-reach", Severity.Error, $"The exit can't be reached from the player start: {why}.", [.. exits, .. shut.Select(s => s.Wall)]));
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
