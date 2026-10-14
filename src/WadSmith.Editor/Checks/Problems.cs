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
        if (classic is not null)
        {
            NotClassic(f, classic, found);
            FormatLimits(f, classic, found);
            if (Core.Exporting.ClassicPorts.Get(classic.Target)?.VanillaLimits == true) VanillaViews.Check(f, found);
        }
        NoStart(geometry, engine, found);
        ManyStarts(f, found);
        OnNoDifficulty(f, found);
        if (classic?.Target == Targets.Vanilla)
        {
            DeathmatchStarts(f, found);
            if (resources is not null) VanillaPictures(f, resources, found);
        }

        NoExit(f, found);
        Unclosed(f, found);
        MissingTextures(f, look, found);
        if (resources is not null) UnknownTextures(f, resources, found);
        if (resources is not null) CannotShow(f, resources, found);
        TwoSidedFlag(f, found);
        if (resources is not null) StillSwitches(geometry, resources, found);
        ThingsOutside(geometry, resources, found);
        Slivers(geometry, found);
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

        var walk = Progression.Walk(geometry, engine);
        ExitOutOfReach(geometry, walk, found);
        if (Keys.Of(engine).Count > 0)
        {
            // A Hexen key is carried between the maps of a hub, so a door whose key is on another map is no fault.
            if (engine != "hexen") MissingKeys(f, engine, found);
            KeysOutOfReach(geometry, walk, engine, found);
        }

        return found;
    }

    private static string Count(int n, string one, string many) => n == 1 ? one : string.Create(CultureInfo.InvariantCulture, $"{n} {many}");

    public static bool Flag(KeyValuePair<string, UdmfValue>[] extra, string name) => extra.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value.Kind == UdmfValueKind.Boolean && kv.Value.AsBool());

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

    private static readonly string[] Skills = ["skill1", "skill2", "skill3", "skill4", "skill5"];

    // Objects that are not put on the map per difficulty: player and deathmatch starts, Hexen's extra starts and polyobject spots.
    private static bool Unspawned(int type) => type is >= 1 and <= 4 or 11 or (>= 9100 and <= 9103) or (>= 9300 and <= 9303);

    /// <summary>Objects on no difficulty at all (Eureka's "unspawnable things"): the game never puts them on the map. The fix puts them on every one.</summary>
    private static void OnNoDifficulty(MapFrame f, List<Problem> found)
    {
        var never = f.Things.Where(t => t.Element is not null && !Unspawned(t.Type) && !Skills.Any(s => Flag(t.Extra, s))).Select(t => (Element)t.Element!).ToList();
        if (never.Count == 0) return;
        found.Add(new Problem("no-difficulty", Severity.Warning, $"{Count(never.Count, "An object is", "objects are")} on no difficulty, so the game never puts {(never.Count == 1 ? "it" : "them")} on the map.", never, "Put them on every difficulty", e =>
        {
            foreach (var t in never.OfType<Thing>().Where(t => t.OnMap))
            {
                foreach (var s in Skills) e.Flag(t, s, true);
            }
        }));
    }

    /// <summary>Vanilla Doom stops with an error when deathmatch starts on a map with fewer than four deathmatch starts (G_DeathMatchSpawnPlayer); none is fine, it is then not a deathmatch map.</summary>
    private static void DeathmatchStarts(MapFrame f, List<Problem> found)
    {
        var starts = f.Things.Where(t => t.Element is not null && t.Type == 11).Select(t => (Element)t.Element!).ToList();
        if (starts.Count is 0 or >= 4) return;
        found.Add(new Problem("few-deathmatch-starts", Severity.Warning, string.Create(CultureInfo.InvariantCulture, $"The map has {starts.Count} deathmatch start{(starts.Count == 1 ? "" : "s")}; vanilla Doom refuses to start a deathmatch with fewer than 4. Add more, or take them out."), starts));
    }

    /// <summary>
    /// Pictures vanilla Doom cannot draw where they are (Eureka's texture checks; Boom and GZDoom fixed both): a see-through
    /// middle built from overlapping patches (the Medusa effect), and on a solid wall a picture with see-through parts, or
    /// shorter than 128 with more of the wall showing than the picture has (tutti-frutti).
    /// </summary>
    private static void VanillaPictures(MapFrame f, GameResources resources, List<Problem> found)
    {
        var textures = resources.Textures;
        var medusa = new List<Element>();
        var noise = new List<Element>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || l.Front < 0) continue;
            if (l.Back >= 0)
            {
                if (new[] { l.Front, l.Back }.Any(s => f.Sides[s].Middle is var m && m != "-" && textures.StacksPatches(m))) medusa.Add(l.Element);
                continue;
            }

            var face = f.Sides[l.Front];
            if (face.Middle == "-" || face.Sector < 0) continue;
            if (textures.HasHoles(face.Middle))
            {
                noise.Add(l.Element);
                continue;
            }

            if (textures.FindWall(face.Middle) is not { Height: < 128 } info) continue;
            var room = f.Sectors[face.Sector];
            var open = room.CeilingHeight - room.FloorHeight;
            var y = face.OffsetY;
            var outside = Flag(l.Extra, "dontpegbottom") ? y > 0 || y < info.Height - open : y < 0 || y > info.Height - open;
            if (open > info.Height || outside) noise.Add(l.Element);
        }

        if (medusa.Count > 0)
        {
            found.Add(new Problem("medusa", Severity.Warning, $"{Count(medusa.Count, "An opening has", "openings have")} a middle picture made of overlapping patches, which vanilla Doom draws as streaks of colour (the Medusa effect) and can crash on.", medusa, "Take the middle pictures off", e =>
            {
                foreach (var l in medusa.OfType<Line>().Where(l => l.OnMap))
                {
                    foreach (var side in new[] { l.Front, l.Back! }.Where(s => s.Middle != "-" && textures.StacksPatches(s.Middle))) e.Set(side, x => x.Middle, (x, v) => x.Middle = v, "-");
                }
            }));
        }

        if (noise.Count > 0)
        {
            found.Add(new Problem("tutti-frutti", Severity.Warning, $"{Count(noise.Count, "A wall shows", "walls show")} more of {(noise.Count == 1 ? "its" : "their")} picture than it has, or see-through parts of it, which vanilla Doom fills with coloured noise (tutti-frutti). Use a picture at least as tall as the wall, or play the map in GZDoom.", noise));
        }
    }

    private static void NoExit(MapFrame f, List<Problem> found)
    {
        if (f.Lines.Any(l => l.Element is not null && l.Special is Exits.Normal or Exits.Secret or 74 or 75)) return;
        found.Add(new Problem("no-exit", Severity.Warning, "There's no way to finish the map yet. Select a wall and choose Make it the exit switch.", []));
    }

    /// <summary>The thinnest a room can be before it counts as a sliver: frames of doorways are 8 deep, so less than that.</summary>
    private const double Sliver = 6;

    /// <summary>
    /// Slivers: open rooms only a few units across, left where two rooms' walls nearly met (2026-10-12, a user's map: a strip
    /// 2–4 units deep between a room and the room north of it, with a door opening into it). The player cannot stand in one,
    /// and on the plan its walls lie on top of the walls beside it, so clicks land on the sliver. The fix joins it to the
    /// open room it shares the most wall with, at the same heights.
    /// </summary>
    private static void Slivers(MapGeometry geometry, List<Problem> found)
    {
        var f = geometry.Frame;
        foreach (var s in f.Sectors)
        {
            if (s.Element is not { } room || s.CeilingHeight <= s.FloorHeight || geometry.WallBoundsAt(room.Slot) is not { } b) continue;
            var thin = Math.Min(b.MaxX - b.MinX, b.MaxY - b.MinY);
            if (thin >= Sliver) continue;

            // The room beside it to join: open, at the same heights, sharing the most wall.
            var shared = new Dictionary<Sector, double>(ReferenceEqualityComparer.Instance);
            foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (!l.OnMap || l.Back is null) continue;
                var other = ReferenceEquals(l.Front.Sector, room) ? l.Back.Sector : ReferenceEquals(l.Back.Sector, room) ? l.Front.Sector : null;
                if (other is null || ReferenceEquals(other, room) || other.FloorHeight != room.FloorHeight || other.CeilingHeight != room.CeilingHeight) continue;
                shared[other] = shared.GetValueOrDefault(other) + l.Length;
            }

            var across = string.Create(CultureInfo.InvariantCulture, $"{thin:0.#}");
            if (shared.Count == 0)
            {
                found.Add(new Problem("sliver", Severity.Warning, $"A room only {across} units across lies between other rooms, where walls nearly met: the player cannot stand in it, and on the plan clicks land on it instead of the walls beside it. Move a corner so the rooms share their wall.", [room]));
                continue;
            }

            var into = shared.MaxBy(kv => kv.Value).Key;
            found.Add(new Problem("sliver", Severity.Warning, $"A room only {across} units across lies between other rooms, where walls nearly met: the player cannot stand in it, and on the plan clicks land on it instead of the walls beside it.", [room],
                "Join it to the room beside it", e =>
                {
                    // Joined against the map as it was checked; after another change the check runs again first.
                    if (geometry.Version != e.Map.Version || !room.OnMap || !into.OnMap) return;
                    Editing.Rooms.Join(e, geometry, [into, room]);
                }));
        }
    }

    /// <summary>
    /// What a classic target cannot hold (6b): a wall whose action with its settings, or a room whose action, has no type
    /// in the classic format, read backwards from the engine's own table. Written plain in the file, so the map would
    /// play in GZDoom only.
    /// </summary>
    private static void NotClassic(MapFrame f, ClassicRules rules, List<Problem> found)
    {
        var target = Targets.TitleOf(rules.Target);
        var port = Core.Exporting.ClassicPorts.Get(rules.Target)?.For(rules.Namespace);
        static bool On(KeyValuePair<string, UdmfValue>[] extra, string name) => extra.Any(e => e.Key == name && e.Value.AsBool());
        // A line the save rewrites to its classic spelling (a teleporter by id, a switch monsters may press too) is classic.
        var walls = f.Lines.Where(l => l.Element is not null && l.Special != 0 && ClassicTranslation.TypeOf(l.Special, l.Args, name => On(l.Extra, name), l.LineId, rules.Xlat, port) is null
            && !Core.Exporting.MapExport.RespelledToClassic(l.Special, l.Args, name => On(l.Extra, name), l.LineId, rules.Xlat, port)).ToList();
        foreach (var group in walls.GroupBy(l => l.Special))
        {
            var action = ActionCatalog.Get(group.Key)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"action {group.Key}");
            var elements = group.Select(l => l.Element!).ToList();
            found.Add(new Problem("not-classic", Severity.Warning, $"{(elements.Count == 1 ? "A wall's" : string.Create(CultureInfo.InvariantCulture, $"{elements.Count} walls'"))} action, {action} with these settings, has no type in {target}: the map would play in GZDoom only. Make the object again, or change what the project plays in under Map settings.", elements));
        }

        var rooms = f.Sectors.Where(s => s.Element is not null && ClassicTranslation.SectorTypeOf(s.Special, On(s.Extra, "secret"), rules.Xlat, port) is null).Select(s => s.Element!).ToList();
        if (rooms.Count > 0) found.Add(new Problem("not-classic", Severity.Warning, $"{(rooms.Count == 1 ? "A room's" : string.Create(CultureInfo.InvariantCulture, $"{rooms.Count} rooms'"))} action has no type in {target} (a secret with another action, or an action the classic engine lacks): the map would play in GZDoom only.", rooms));
    }

    /// <summary>
    /// The classic format's own limits (6b.4), for vanilla and limit-removing alike: texture and flat names of more than
    /// eight letters, which the format cannot hold, and more walls, sides or rooms than its 16-bit numbers reach.
    /// </summary>
    private static void FormatLimits(MapFrame f, ClassicRules rules, List<Problem> found)
    {
        var target = Targets.TitleOf(rules.Target);
        var longWalls = f.Sides.Where(s => s.Element is { Line: not null } && new[] { s.Top, s.Middle, s.Bottom }.Any(t => t.Length > 8)).Select(s => (Element)s.Element!.Line!).Distinct().ToList();
        var longRooms = f.Sectors.Where(s => s.Element is not null && (s.FloorTexture.Length > 8 || s.CeilingTexture.Length > 8)).Select(s => (Element)s.Element!).ToList();
        if (longWalls.Count + longRooms.Count > 0)
        {
            found.Add(new Problem("long-names", Severity.Error, $"{longWalls.Count + longRooms.Count} wall{(longWalls.Count + longRooms.Count == 1 ? "" : "s")} or floor{(longWalls.Count + longRooms.Count == 1 ? "" : "s")} use a picture whose name is longer than eight letters, which {target}'s format cannot hold. Choose one of the game's own pictures.", [.. longWalls, .. longRooms]));
        }

        const int Most = 32767;
        var counts = new (string What, int Count)[]
        {
            ("walls", f.Lines.Count(l => l.Element is not null)),
            ("wall sides", f.Sides.Count(s => s.Element is not null)),
            ("rooms", f.Sectors.Count(s => s.Element is not null)),
        };
        foreach (var (what, count) in counts.Where(c => c.Count > Most))
        {
            found.Add(new Problem("format-limit", Severity.Error, string.Create(CultureInfo.InvariantCulture, $"The map has {count:N0} {what}; {target}'s format numbers them up to {Most:N0}. Split the map, or play it in GZDoom."), []));
        }
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

    /// <summary>
    /// Switches whose picture is none of the game's switch pictures (Eureka's "non-animating switch textures"): the player
    /// uses the wall and sees nothing change. A switch is a wall the player uses whose action works elsewhere or ends the
    /// map. A door's or lift's own walls are not switches: an action on the area beside the wall itself (tag 0, or the
    /// tag of a room on either side), as id's maps open doors and lower lifts from their own edges.
    /// </summary>
    private static void StillSwitches(MapGeometry g, GameResources resources, List<Problem> found)
    {
        var f = g.Frame;
        var pictures = resources.SwitchPictures;
        if (pictures.Count == 0) return;
        var still = new List<Element>();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || l.Front < 0 || l.Special == 0 || !Flag(l.Extra, "playeruse")) continue;
            if (Switches.NamesAreas(l.Special) && (l.Args[0] == 0 || g.SectorsTagged(l.Args[0]).Any(s => s == f.Sides[l.Front].Sector || (l.Back >= 0 && s == f.Sides[l.Back].Sector)))) continue;
            var face = f.Sides[l.Front];
            // An unseen wall (nothing drawn on its face) has no picture to change.
            if (face.Middle == "-" && face.Top == "-" && face.Bottom == "-") continue;
            if (pictures.Contains(face.Middle) || pictures.Contains(face.Top) || pictures.Contains(face.Bottom)) continue;
            still.Add(l.Element);
        }

        if (still.Count == 0) return;
        found.Add(new Problem("still-switch", Severity.Warning, $"{Count(still.Count, "A switch shows", "switches show")} no switch picture, so nothing changes on the wall when the player uses {(still.Count == 1 ? "it" : "them")}. Give {(still.Count == 1 ? "it" : "them")} one of the game's switch pictures (SW1… in Doom).", still));
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
            if (l.Element is null || !Doors.IsDoorAction(l.Special)) continue;
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
            if (l.Element is not null && l.Back >= 0 && l.Args[0] == 0 && Doors.IsDoorAction(l.Special)) doors.Add(f.Sides[l.Back].Sector);
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
    private static void KeysOutOfReach(MapGeometry g, ProgressionResult walk, string engine, List<Problem> found)
    {
        foreach (var (colour, keys) in walk.KeysOutOfReach)
        {
            var doors = g.Frame.Lines.Where(l => l.Element is not null && Keys.ColourOfLock(Doors.LockOf(l.Special, l.Args), engine) == colour).Select(l => (Element)l.Element!).ToList();
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

    private static void MissingKeys(MapFrame f, string engine, List<Problem> found)
    {
        // The game's keys by colour; a lock takes the keys LOCKDEFS gives it (Doom's 129–131 either of a colour).
        var present = f.Things.Where(t => t.Element is not null).Select(t => t.Type).ToHashSet();
        foreach (var l in f.Lines)
        {
            if (l.Element is null || Doors.LockOf(l.Special, l.Args) is not (> 0 and var lockNumber)) continue;
            var needed = Keys.Opening(lockNumber, engine);
            if (needed.Count == 0 || needed.Any(present.Contains)) continue;
            var colour = Keys.ColourOf(needed[0], engine)!;
            if (found.Any(p => p.Rule == "missing-key-" + colour)) continue;
            var doorsOfColour = f.Lines.Where(x => x.Element is not null && Doors.LockOf(x.Special, x.Args) == lockNumber).Select(x => (Element)x.Element!).ToList();
            found.Add(new Problem("missing-key-" + colour, Severity.Error, $"The {colour} key door can never open: there's no {colour} key in the map.", doorsOfColour));
        }
    }
}
