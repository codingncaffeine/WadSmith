using System.Globalization;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Translation;

namespace WadSmith.Core.Exporting;

/// <summary>The picture names a port can draw: the base game's walls and floors (and what the export itself carries).</summary>
public sealed record PictureNames(Func<string, bool> Wall, Func<string, bool> Flat);

/// <summary>
/// One map exported for a classic port (Phase 12): translated back through the engine's table with only the types the
/// port understands, written in the classic format with WadSmith's nodes, and then read back the way the engine reads it
/// and compared with the map, so the report names exactly what did not come back — the translation's own word is never
/// trusted alone. What the format holds but the port does not know (an object number, a picture the game lacks) is checked
/// against the port's own lists, and the format's and vanilla's limits against the written lumps.
/// </summary>
public static class MapExport
{
    /// <summary>The fields every line carries for its action, compared with the action and not one by one.</summary>
    private static readonly HashSet<string> ActionFields = new(StringComparer.Ordinal)
    {
        "playercross", "playeruse", "monstercross", "impact", "playerpush", "missilecross", "anycross",
        "repeatspecial", "monsteractivate", "monsteruse", "firstsideonly",
    };

    /// <summary>The map's lumps for <paramref name="port"/>, the map marker first; what is lost goes into <paramref name="report"/>.</summary>
    /// <remarks><paramref name="next"/> is the map the exit leads to (MAPxx), or <see cref="Projects.ProjectMapInfo.EndToTitle"/>; Hexen and Strife name it in the exit itself.</remarks>
    public static IReadOnlyList<(string Name, byte[] Data)> ForPort(MapModel zdoom, ClassicPort port, string family, XlatTranslator? xlat, ExportReport report, PictureNames? pictures = null, bool exMyNames = false, string? next = null)
    {
        if (zdoom.Namespace != "zdoom") throw new ArgumentException("Export takes a map in GZDoom's namespace.", nameof(zdoom));
        var rules = port.For(family) ?? throw new ArgumentException($"{port.Title} does not play {family} maps.", nameof(family));
        var hexen = family == "hexen";
        if (!hexen && xlat is null) throw new ArgumentException("A Doom-format map needs the engine's translator.", nameof(xlat));
        var name = zdoom.Name.ToUpperInvariant();

        // Names the format cannot hold become "-" before anything else, so every later check sees what is written.
        var model = MapTranslation.Copy(zdoom, "zdoom");
        LongNames(model, name, report);
        Respell(model, name, family, xlat, rules, port, report, next);
        var dropped = KnownToPort(model, name, port, rules, report, pictures, exMyNames);
        MapWide(model, name, hexen, report);

        var notClassic = new List<NotClassic>();
        var classicModel = ClassicTranslation.ToClassicNamespace(model, xlat, family, notClassic, rules);
        report.AddGrouped(name, "wall", notClassic.Where(n => n.What == "wall").Select(n => (ActionOf(model.Lines[n.Index].Special), n.Index)),
            k => ($"Wall actions {port.Title} does not have: {k}", false, "written as plain walls"));
        report.AddGrouped(name, "object", notClassic.Where(n => n.What == "thing").Select(n => (ActionOf(model.Things[n.Index].Special), n.Index)),
            k => ($"Object actions {port.Title} does not have: {k}", false, "the objects stay, without the action"));
        report.AddGrouped(name, "room", notClassic.Where(n => n.What == "room").Select(n => ("effect", n.Index)),
            _ => ($"Room effects {port.Title} does not have", false, "written as rooms with no effect"));

        var classic = MapConversion.ToClassic(classicModel, hexen ? MapFormat.Hexen : MapFormat.Doom);
        var lineMask = rules.LineFlags | (hexen ? 0x1C00 : 0);
        for (var i = 0; i < classic.Lines.Count; i++) classic.Lines[i] = classic.Lines[i] with { Flags = (ushort)(classic.Lines[i].Flags & lineMask) };
        for (var i = 0; i < classic.Things.Count; i++) classic.Things[i] = classic.Things[i] with { Flags = (ushort)(classic.Things[i].Flags & rules.ThingFlags) };
        var stats = NodeBuilder.Build(classic);
        Limits(classic, stats, name, port, report);

        var lumps = new List<(string, byte[])> { (name, []) };
        lumps.AddRange(classic.WriteLumps());
        Compare(model, lumps, family, xlat, notClassic, dropped, name, port, report);
        return lumps;
    }

    private static string ActionOf(int special) => ActionCatalog.Get(special)?.Name ?? special.ToString(CultureInfo.InvariantCulture);

    /// <summary>GZDoom's secret bit in a room's action.</summary>
    private const int SecretBit = 1024;

    private static string MonstersTitle(ClassicPort port) => $"Doors, switches and lines monsters could use that only players use in {port.Title}";

    private const int ExitNormal = 243;
    private const int ExitSecret = 244;
    private const int TeleportNewMap = 74;
    private const int TeleportEndGame = 75;
    private const int AcsExecuteWithResult = 84;
    private const int AcsExecuteAlways = 226;

    /// <summary>
    /// Exits in Hexen and Strife, which have no plain exit: the map they lead to goes into the exit itself — Hexen's
    /// Teleport_NewMap with the map's number; Strife's switch exit (11) with the number as its tag and its walk-over exit
    /// (52) with the number times 100, as Strife reads them; an exit to the title is the game's end.
    /// </summary>
    private static void Exits(MapModel m, string map, string family, ExportReport report, string? next)
    {
        if (family is not ("hexen" or "strife")) return;
        var toTitle = next == Projects.ProjectMapInfo.EndToTitle;
        var number = toTitle ? 0 : SlotNumber(next) ?? SlotNumber(map) + 1;
        if (number is null) return;
        var rewired = 0;
        foreach (var l in m.Lines.Where(l => l.Special is ExitNormal or ExitSecret))
        {
            var walks = l.Flag("playercross");
            Array.Clear(l.Args);
            if (toTitle)
            {
                l.Special = TeleportEndGame;
            }
            else if (family == "hexen")
            {
                l.Special = TeleportNewMap;
                l.Args[0] = number.Value;
            }
            else
            {
                // Strife's exits go through GZDoom's own helper script, which the engine's table names.
                l.Special = walks ? AcsExecuteAlways : AcsExecuteWithResult;
                if (walks) (l.Args[2], l.Args[3]) = (52, number.Value * 100);
                else (l.Args[1], l.Args[2]) = (11, number.Value);
                l.SetFlag("repeatspecial", true);
            }

            rewired++;
        }

        if (rewired > 0) report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{map}: {rewired} exits lead to {(toTitle ? "the end of the game" : $"map {number}")} the way {(family == "hexen" ? "Hexen" : "Strife")} names it."));
    }

    /// <summary>The number of a MAPxx slot, or null.</summary>
    private static int? SlotNumber(string? slot) =>
        slot is { Length: 5 } s && s.StartsWith("MAP", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>A teleport destination, the only arrival spot the classic teleporters know.</summary>
    private const int TeleportDestination = 14;

    /// <summary>
    /// GZDoom's spellings of things a classic map can say too, rewritten to the classic spelling so they survive: a
    /// teleporter sending to an arrival spot's id sends to the spot's room by tag (the classic teleporter finds the spot in
    /// the tagged room; the room gets a tag of its own when it has none); a floor hurting by number (5, 10 or 20 every 32
    /// tics) becomes Doom's own hurting floor of that strength; a switch monsters may use as well, which no classic type
    /// has, becomes the players' switch and is reported. A Hexen-format map keeps GZDoom's actions, which are Hexen's.
    /// </summary>
    private static void Respell(MapModel m, string map, string family, XlatTranslator? xlat, PortRules rules, ClassicPort port, ExportReport report, string? next)
    {
        Exits(m, map, family, report, next);
        if (family == "hexen" || xlat is null) return;

        // Teleporters by id.
        var nextTag = Math.Max(m.Sectors.Select(s => s.Id).DefaultIfEmpty(0).Max(), m.Lines.SelectMany(l => l.Args).DefaultIfEmpty(0).Max()) + 1;
        var tags = new Dictionary<int, (int Tag, bool Exact)>();
        var rewired = 0;
        var elsewhere = new List<(string, int)>();
        for (var i = 0; i < m.Lines.Count; i++)
        {
            var l = m.Lines[i];
            var tagArg = l.Special switch { 70 => 1, 71 => 2, _ => -1 };
            if (tagArg < 0 || l.Args[0] <= 0 || l.Args[tagArg] != 0) continue;
            var tid = l.Args[0];
            if (!tags.TryGetValue(tid, out var found)) tags[tid] = found = RoomTagFor(m, tid, ref nextTag);
            if (found.Tag <= 0) continue;
            l.Args[0] = 0;
            l.Args[tagArg] = found.Tag;
            rewired++;
            if (!found.Exact) elsewhere.Add(("teleport-spot", i));
        }

        report.AddGrouped(map, "wall", elsewhere, _ => ("Teleporters whose arrival spot shares its room with another", false, "the classic teleporter arrives at the first spot in the room"));
        foreach (var tid in tags.Where(t => t.Value.Tag > 0).Select(t => t.Key))
        {
            // The id did its work through the tag; a classic object has no id to keep.
            if (!m.Lines.Any(l => l.Special is 70 or 71 && l.Args[0] == tid)) foreach (var t in m.Things.Where(t => t.Id == tid)) t.Id = 0;
        }

        if (rewired > 0) report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{map}: {rewired} teleporter walls send to their arrival spot's room by tag, the classic way."));

        // Floors hurting by number, as Doom's own hurting floors.
        var hurting = 0;
        if (family == "doom")
        {
            foreach (var s in m.Sectors)
            {
                // A secret keeps its secret (GZDoom's 1024 bit; Boom keeps both, vanilla reports the pair).
                if ((s.Special & ~SecretBit) != 0 || !s.Extra.TryGet("damageamount", out var amount)) continue;
                var interval = s.Extra.TryGet("damageinterval", out var i) ? i.AsInt() : 32;
                if (interval != 32 || s.Extra.Any(e => e.Key is "damagetype" or "leakiness" or "damageterraineffect" or "damagehazard")) continue;
                var classic = amount.AsInt() switch { 5 => 7, 10 => 5, 20 => 16, _ => 0 };
                if (classic == 0 || !rules.Sectors.Contains(classic) || !xlat.Sectors.TryGetValue(classic, out var entry)) continue;
                s.Special = entry.NewType | (s.Special & SecretBit);
                s.Extra.Remove("damageamount");
                s.Extra.Remove("damageinterval");
                hurting++;
            }
        }

        if (hurting > 0) report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{map}: {hurting} hurting floors written as Doom's own (5, 10 or 20 health every 32 tics)."));

        // Lines monsters may set off too, where the port's type is the players' alone (switches; Heretic's lifts).
        var playersOnly = new List<(string, int)>();
        for (var i = 0; i < m.Lines.Count; i++)
        {
            var l = m.Lines[i];
            var monsters = l.Flag("monsteruse") || l.Flag("monsteractivate") || (l.Flag("playercross") && l.Flag("monstercross"));
            if (l.Special == 0 || !monsters || !(l.Flag("playeruse") || l.Flag("playercross"))) continue;
            if (ClassicTranslation.TypeOf(l, xlat, rules) is not null) continue;
            if (ClassicTranslation.TypeOf(l.Special, l.Args, f => f is not ("monsteruse" or "monsteractivate" or "monstercross") && l.Flag(f), l.Id, xlat, rules) is null) continue;
            foreach (var f in new[] { "monsteruse", "monsteractivate", "monstercross" }) l.Extra.Remove(f);
            playersOnly.Add(("monsters", i));
        }

        report.AddGrouped(map, "wall", playersOnly, _ => (MonstersTitle(port), false, ""));
    }

    /// <summary>
    /// The tag a classic teleporter needs to reach the one arrival spot with id <paramref name="tid"/>: its room's tag, or a
    /// new tag for a room that has none (0 when there is no single spot, or it is in no room). The classic teleporter goes to
    /// the first arrival spot, in the map's order, standing in a room with the tag: Exact says whether that is this spot.
    /// </summary>
    private static (int Tag, bool Exact) RoomTagFor(MapModel m, int tid, ref int nextTag)
    {
        var spots = m.Things.Where(t => t.Id == tid).ToList();
        if (spots is not [{ Type: TeleportDestination } spot]) return (0, false);
        var room = RoomAt(m, spot.X, spot.Y);
        if (room < 0) return (0, false);
        var sector = m.Sectors[room];
        if (sector.Id == 0) sector.Id = nextTag++;
        var first = m.Things.FirstOrDefault(t => t.Type == TeleportDestination && RoomAt(m, t.X, t.Y) is var r && r >= 0 && m.Sectors[r].Id == sector.Id);
        return (sector.Id, ReferenceEquals(first, spot));
    }

    /// <summary>The room a point lies in (by the even-odd rule over each room's walls, holes included), or -1.</summary>
    private static int RoomAt(MapModel m, double x, double y)
    {
        var crossings = new int[m.Sectors.Count];
        foreach (var l in m.Lines)
        {
            var front = l.Front >= 0 ? m.Sides[l.Front].Sector : -1;
            var back = l.Back >= 0 ? m.Sides[l.Back].Sector : -1;
            if (front == back) continue;
            var (a, b) = (m.Vertices[l.V1], m.Vertices[l.V2]);
            if ((a.Y > y) == (b.Y > y)) continue;
            if (x >= a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) continue;
            if (front >= 0) crossings[front]++;
            if (back >= 0) crossings[back]++;
        }

        for (var i = 0; i < crossings.Length; i++)
        {
            if (crossings[i] % 2 == 1) return i;
        }

        return -1;
    }

    /// <summary>Picture names the WAD format cannot store (more than eight letters) are written "-" and listed.</summary>
    private static void LongNames(MapModel m, string map, ExportReport report)
    {
        static bool Bad(string n)
        {
            try
            {
                LumpName.Validate(n);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sides = new List<int>();
        var rooms = new List<int>();
        for (var i = 0; i < m.Sides.Count; i++)
        {
            var s = m.Sides[i];
            var hit = false;
            if (Bad(s.Top)) { names.Add(s.Top); s.Top = "-"; hit = true; }
            if (Bad(s.Bottom)) { names.Add(s.Bottom); s.Bottom = "-"; hit = true; }
            if (Bad(s.Middle)) { names.Add(s.Middle); s.Middle = "-"; hit = true; }
            if (hit) sides.Add(i);
        }

        for (var i = 0; i < m.Sectors.Count; i++)
        {
            var s = m.Sectors[i];
            var hit = false;
            if (Bad(s.FloorTexture)) { names.Add(s.FloorTexture); s.FloorTexture = "-"; hit = true; }
            if (Bad(s.CeilingTexture)) { names.Add(s.CeilingTexture); s.CeilingTexture = "-"; hit = true; }
            if (hit) rooms.Add(i);
        }

        var detail = string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", …" : "");
        if (sides.Count > 0) report.Add(new ExportLoss(map, "side:long-name", "Pictures with names longer than 8 letters", "side", sides, false, detail));
        if (rooms.Count > 0) report.Add(new ExportLoss(map, "room:long-name", "Floor or ceiling pictures with names longer than 8 letters", "room", rooms, false, detail));
    }

    /// <summary>Objects and pictures the file can hold but the port does not know: vanilla stops with an error over either.</summary>
    private static HashSet<int> KnownToPort(MapModel m, string map, ClassicPort port, PortRules rules, ExportReport report, PictureNames? pictures, bool exMyNames)
    {
        var unknown = m.Things.Select((t, i) => (t, i)).Where(x => x.t.Type != 0 && !rules.Things.Contains(x.t.Type)).ToList();
        var why = port.VanillaLimits ? "the game stops with \"Unknown type\" as the map loads" : "the game leaves them out";
        foreach (var kind in unknown.GroupBy(x => KindOf(x.t.Type)))
        {
            var types = kind.GroupBy(x => x.t.Type).OrderBy(g => g.Key).ToList();
            var numbers = (types.Count == 1 ? "number " : "numbers ") + string.Join(", ", types.Select(g => g.Count() == 1 ? g.Key.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{g.Key} ×{g.Count()}")));
            var title = kind.Key switch
            {
                "lights" => "GZDoom's dynamic lights",
                "brought" => "Objects brought in from other games or made from 3D models",
                _ => $"Objects {port.Title} does not know",
            };
            report.Add(new ExportLoss(map, "object:unknown-" + kind.Key, title, "object", [.. kind.Select(x => x.i)], port.VanillaLimits, $"{numbers}: {why}"));
        }
        if (exMyNames && rules.ExMyLacks is { } lacks)
        {
            var doom2 = m.Things.Select((t, i) => (t, i)).Where(x => lacks.Contains(x.t.Type) && rules.Things.Contains(x.t.Type)).ToList();
            report.AddGrouped(map, "object", doom2.Select(x => ("doom2:" + x.t.Type.ToString(CultureInfo.InvariantCulture), x.i)),
                k => ($"Doom II objects in a Doom map: number {k[6..]}", port.VanillaLimits, "the game has no pictures for them and stops when one comes into view"));
        }

        var dropped = unknown.Select(x => x.i).ToHashSet();
        if (pictures is null) return dropped;
        var walls = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sides = new List<int>();
        for (var i = 0; i < m.Sides.Count; i++)
        {
            var missing = new[] { m.Sides[i].Top, m.Sides[i].Bottom, m.Sides[i].Middle }.Where(n => n != "-" && !pictures.Wall(n)).ToList();
            if (missing.Count == 0) continue;
            sides.Add(i);
            walls.UnionWith(missing);
        }

        var flats = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var rooms = new List<int>();
        for (var i = 0; i < m.Sectors.Count; i++)
        {
            var missing = new[] { m.Sectors[i].FloorTexture, m.Sectors[i].CeilingTexture }.Where(n => n != "-" && !pictures.Flat(n)).ToList();
            if (missing.Count == 0) continue;
            rooms.Add(i);
            flats.UnionWith(missing);
        }

        var seen = port.VanillaLimits ? "the game stops with \"not found\" as the map loads" : "shown as missing";
        if (sides.Count > 0) report.Add(new ExportLoss(map, "side:no-picture", "Wall pictures the game does not have", "side", sides, port.VanillaLimits, $"{string.Join(", ", walls.Take(6))}{(walls.Count > 6 ? ", …" : "")}: {seen}"));
        if (rooms.Count > 0) report.Add(new ExportLoss(map, "room:no-picture", "Floor pictures the game does not have", "room", rooms, port.VanillaLimits, $"{string.Join(", ", flats.Take(6))}{(flats.Count > 6 ? ", …" : "")}: {seen}"));
        return dropped;
    }

    /// <summary>What kind of object a number the port does not know is, for the report: GZDoom's lights, the project's own (20000 on), or other.</summary>
    private static string KindOf(int type) => type switch
    {
        >= 9800 and <= 9899 or 1502 or 1503 => "lights",
        >= 20000 => "brought",
        _ => "other",
    };

    /// <summary>What belongs to the whole map in GZDoom's format: its own settings, and the lumps a classic map has no place for (a Hexen map keeps its scripts).</summary>
    private static void MapWide(MapModel m, string map, bool hexen, ExportReport report)
    {
        var settings = m.Globals.Select(g => g.Key).Concat(m.OtherBlocks.Select(b => b.Type)).Distinct(StringComparer.Ordinal).ToList();
        if (settings.Count > 0) report.Add(new ExportLoss(map, "map:settings", "Map-wide GZDoom settings", "map", [], false, string.Join(", ", settings)));
        var keep = hexen ? new[] { "BEHAVIOR", "SCRIPTS" } : [];
        var dropped = m.OtherLumps.Where(l => !keep.Contains(l.Name, LumpName.Comparer) && !IsNodeLump(l.Name)).Select(l => l.Name).ToList();
        if (dropped.Count > 0)
        {
            var scripts = dropped.Any(n => n is "BEHAVIOR" or "SCRIPTS");
            report.Add(new ExportLoss(map, "map:lumps", scripts ? "The map's scripts" : "Lumps a classic map has no place for", "map", [], false, string.Join(", ", dropped)));
        }

        m.OtherLumps.RemoveAll(l => !keep.Contains(l.Name, LumpName.Comparer));
        foreach (var key in m.Globals.Select(g => g.Key).ToList()) m.Globals.Remove(key);
        m.OtherBlocks.Clear();
        if (hexen)
        {
            // A Hexen-format map is known by its BEHAVIOR lump, which vanilla Hexen reads as Hexen's own script format
            // ("ACS\0"): an empty lump (a crash: Crispy Hexen stopped on one, 2026-10-13) becomes an empty script file, and
            // scripts compiled for GZDoom (ACSE, ACSe) cannot run there.
            var behavior = m.OtherLumps.FindIndex(l => LumpName.Comparer.Equals(l.Name, "BEHAVIOR"));
            var data = behavior >= 0 ? m.OtherLumps[behavior].Data : [];
            if (data.Length < 16 || !data.AsSpan(0, 4).SequenceEqual("ACS\0"u8))
            {
                if (data.Length > 0) report.Add(new ExportLoss(map, "map:scripts", "The map's scripts, compiled for GZDoom", "map", [], false, "vanilla Hexen runs only its own script format"));
                if (behavior >= 0) m.OtherLumps.RemoveAt(behavior);
                m.OtherLumps.Add(("BEHAVIOR", [(byte)'A', (byte)'C', (byte)'S', 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
            }
        }
    }

    private static bool IsNodeLump(string name) => name is "ZNODES" or "SEGS" or "SSECTORS" or "NODES" or "REJECT" or "BLOCKMAP" || name.StartsWith("GL_", StringComparison.Ordinal);

    /// <summary>The format's limits (16-bit indices) and vanilla's (signed indices, a 64 KB blockmap) against what was written.</summary>
    private static void Limits(ClassicMap c, NodeStats stats, string map, ClassicPort port, ExportReport report)
    {
        var most = port.VanillaLimits ? short.MaxValue : ushort.MaxValue - 1;
        void Check(string what, int count, int limit)
        {
            if (count > limit) report.Add(new ExportLoss(map, "limit:" + what, $"Too many {what} for {port.Title}: {count:N0} (at most {limit:N0})", "map", [], true, "the game cannot load the map"));
        }

        Check("corners", c.Vertices.Count, most);
        Check("walls", c.Lines.Count, most);
        Check("wall sides", c.Sides.Count, most);
        Check("rooms", c.Sectors.Count, most);
        Check("wall pieces (segs)", stats.Segs, most);
        Check("subsectors", stats.Subsectors, short.MaxValue);
        Check("nodes", stats.Nodes, short.MaxValue);
        if (port.VanillaLimits && stats.BlockmapBytes > 0x10000) report.Add(new ExportLoss(map, "limit:blockmap", $"The map is too wide for vanilla's blockmap ({stats.BlockmapBytes / 1024:N0} KB, at most 64 KB)", "map", [], true, "objects pass through walls or the game stops"));
    }

    /// <summary>
    /// Reads the written lumps back as the engine does — the classic reader, then GZDoom's own translation forward — and
    /// lists every field of every element that did not come back as it was.
    /// </summary>
    private static void Compare(MapModel before, List<(string Name, byte[] Data)> lumps, string family, XlatTranslator? xlat, List<NotClassic> told, HashSet<int> dropped, string map, ClassicPort port, ExportReport report)
    {
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))));
        var read = MapConversion.Read(MapLocator.Find(wad).Single(), family);
        var after = MapTranslation.ToZDoom(read, family == "hexen" ? null : xlat, []);
        var toldWalls = told.Where(t => t.What == "wall").Select(t => t.Index).ToHashSet();
        var toldRooms = told.Where(t => t.What == "room").Select(t => t.Index).ToHashSet();
        var toldThings = told.Where(t => t.What == "thing").Select(t => t.Index).ToHashSet();
        var lost = new List<(string On, string Key, int Index)>();

        var rounded = 0;
        for (var i = 0; i < before.Vertices.Count; i++)
        {
            if (before.Vertices[i].X != after.Vertices[i].X || before.Vertices[i].Y != after.Vertices[i].Y) rounded++;
        }

        if (rounded > 0) report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{map}: {rounded} corners moved to whole units (the classic format has no fractions)."));

        for (var i = 0; i < before.Lines.Count; i++)
        {
            var (b, a) = (before.Lines[i], after.Lines[i]);
            if (!toldWalls.Contains(i) && b.Special != 0 && !SameAction(b, a)) lost.Add(("wall", SameAction(b, a, monsters: false) ? "monsters" : "action", i));
            if (b.Id > 0 && a.Id != b.Id) lost.Add(("wall", "id", i));
            foreach (var f in Differ(b.Extra, a.Extra, ActionFields)) lost.Add(("wall", f, i));
        }

        for (var i = 0; i < before.Sides.Count; i++)
        {
            var (b, a) = (before.Sides[i], after.Sides[i]);
            if (b.OffsetX != a.OffsetX || b.OffsetY != a.OffsetY) lost.Add(("side", "offset-fraction", i));
            if (!Same(b.Top, a.Top) || !Same(b.Bottom, a.Bottom) || !Same(b.Middle, a.Middle)) lost.Add(("side", "picture", i));
            foreach (var f in Differ(b.Extra, a.Extra, [])) lost.Add(("side", f, i));
        }

        for (var i = 0; i < before.Sectors.Count; i++)
        {
            var (b, a) = (before.Sectors[i], after.Sectors[i]);
            if (b.FloorHeight != a.FloorHeight || b.CeilingHeight != a.CeilingHeight) lost.Add(("room", "height-fraction", i));
            if (!Same(b.FloorTexture, a.FloorTexture) || !Same(b.CeilingTexture, a.CeilingTexture)) lost.Add(("room", "picture", i));
            if (b.Light != a.Light) lost.Add(("room", "light", i));
            if (b.Id != a.Id) lost.Add(("room", "id", i));
            var secretBefore = b.Extra.TryGet("secret", out var s) && s.AsBool();
            if (!toldRooms.Contains(i) && (b.Special | (secretBefore ? 1024 : 0)) != a.Special) lost.Add(("room", "effect", i));
            foreach (var f in Differ(b.Extra, a.Extra, ["secret", "dropactors"])) lost.Add(("room", f, i));
        }

        for (var i = 0; i < before.Things.Count; i++)
        {
            // An object the port leaves out is listed once, as one it does not know.
            if (dropped.Contains(i)) continue;
            var (b, a) = (before.Things[i], after.Things[i]);
            if (b.X != a.X || b.Y != a.Y) lost.Add(("object", "position-fraction", i));
            if (b.Height != a.Height) lost.Add(("object", "height", i));
            if (b.Angle != a.Angle) lost.Add(("object", "angle", i));
            if (b.Type != a.Type) lost.Add(("object", "type", i));
            if (!toldThings.Contains(i) && (b.Special != a.Special || !b.Args.SequenceEqual(a.Args))) lost.Add(("object", "action", i));
            if (b.Id != a.Id && b.Id > 0) lost.Add(("object", "id", i));
            foreach (var f in Differ(b.Extra, a.Extra, [])) lost.Add(("object", f, i));
        }

        foreach (var on in lost.GroupBy(l => l.On))
        {
            report.AddGrouped(map, on.Key, on.Select(l => (Group(on.Key, l.Key), l.Index)), k => Words(on.Key, k, port));
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a line came back with the same action, arguments and activation (the monsters' use of it named either way).</summary>
    private static bool SameAction(MapLine b, MapLine a, bool monsters = true)
    {
        if (b.Special != a.Special || !b.Args.SequenceEqual(a.Args)) return false;
        // Monsters' use named either way, and monsters crossing beside players named as the classic engine reads it.
        IEnumerable<string> Fields(MapLine l)
        {
            var on = l.Extra.Where(e => ActionFields.Contains(e.Key) && e.Value.AsBool()).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            if (on.Remove("monsteruse") | (on.Contains("playercross") && on.Remove("monstercross"))) on.Add("monsteractivate");
            if (!monsters) on.Remove("monsteractivate");
            return on;
        }

        return new HashSet<string>(Fields(b)).SetEquals(Fields(a));
    }

    /// <summary>The fields whose values differ, a false flag counting as absent.</summary>
    private static IEnumerable<string> Differ(UdmfFields b, UdmfFields a, IEnumerable<string> skip)
    {
        var ignore = new HashSet<string>(skip, StringComparer.Ordinal);
        static Dictionary<string, UdmfValue> Norm(UdmfFields f, HashSet<string> ignore) => f
            .Where(e => !ignore.Contains(e.Key) && !(e.Value.Kind == UdmfValueKind.Boolean && !e.Value.AsBool()))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var nb = Norm(b, ignore);
        var na = Norm(a, ignore);
        foreach (var k in nb.Keys.Union(na.Keys).Order(StringComparer.Ordinal))
        {
            if (!nb.TryGetValue(k, out var vb) || !na.TryGetValue(k, out var va) || !vb.Equals(va)) yield return k;
        }
    }

    /// <summary>Fields that read as one thing to a mapper share a key: every part's offsets, every kind of slope.</summary>
    private static string Group(string on, string field) => field switch
    {
        _ when field.StartsWith("offsetx_", StringComparison.Ordinal) || field.StartsWith("offsety_", StringComparison.Ordinal) => "part-offsets",
        _ when field.StartsWith("scalex", StringComparison.Ordinal) || field.StartsWith("scaley", StringComparison.Ordinal) || field == "scale" => "scale",
        _ when field.StartsWith("floorplane_", StringComparison.Ordinal) || field.StartsWith("ceilingplane_", StringComparison.Ordinal) => "slope",
        _ when field.StartsWith("xpanning", StringComparison.Ordinal) || field.StartsWith("ypanning", StringComparison.Ordinal) => "panning",
        _ when field.StartsWith("rotation", StringComparison.Ordinal) => "rotation",
        _ when field.StartsWith("light", StringComparison.Ordinal) && on is "side" or "room" && field != "lightcolor" => "own-light",
        _ when field.StartsWith("color_", StringComparison.Ordinal) || field is "lightcolor" => "colour",
        _ when field is "fadecolor" or "fogdensity" => "fog",
        _ when field.StartsWith("alpha", StringComparison.Ordinal) || field.StartsWith("renderstyle", StringComparison.Ordinal) || field is "translucent" or "transparent" => "see-through",
        _ when field.StartsWith("damage", StringComparison.Ordinal) || field == "leakiness" => "damage",
        _ when field is "skill1" or "skill2" or "skill3" or "skill4" or "skill5" => "difficulty",
        _ when field is "single" or "coop" or "dm" => "modes",
        _ when field.StartsWith("class", StringComparison.Ordinal) => "classes",
        _ => field,
    };

    private static (string Title, bool Breaks, string Detail) Words(string on, string key, ClassicPort port)
    {
        var title = (on, key) switch
        {
            ("wall", "action") => "Wall actions that changed on the way",
            ("wall", "monsters") => MonstersTitle(port),
            ("wall", "id") => "Wall ids the classic format cannot keep",
            ("wall", "passuse") => "Walls whose use passes through to the wall behind",
            ("wall", "midtex3d") => "Middle pictures you can walk on",
            ("wall", "blocklandmonsters") => "Walls that block walking monsters",
            ("wall", "blockplayers") => "Walls that block players",
            ("wall", "blockprojectiles") => "Walls that block shots",
            ("wall", "blockeverything") => "Walls that block everything",
            ("wall", "blockuse") => "Walls that block use",
            ("wall", "blockhitscan") => "Walls that block bullets",
            ("wall", "blocksight") => "Walls that block monsters' sight",
            ("wall", "jumpover") or ("wall", "blockfloaters") => "Strife's railings",
            ("wall", "see-through") => "See-through walls",
            ("wall", "clipmidtex") or ("wall", "wrapmidtex") => "Middle pictures clipped or repeated",
            ("wall", "locknumber") => "Locks set on walls",
            ("wall", "moreids") => "Extra wall ids",
            ("side", "offset-fraction") => "Picture offsets that are not whole numbers (rounded)",
            ("side", "picture") => "Wall pictures that changed on the way",
            ("side", "part-offsets") => "Picture offsets set per part of a wall",
            ("side", "scale") => "Scaled wall pictures",
            ("side", "own-light") => "Walls with their own brightness",
            ("side", "colour") => "Coloured walls",
            ("side", "nofakecontrast") or ("side", "smoothlighting") => "Wall shading settings",
            ("side", "clipmidtex") or ("side", "wrapmidtex") => "Middle pictures clipped or repeated",
            ("room", "height-fraction") => "Floor or ceiling heights that are not whole numbers (rounded)",
            ("room", "picture") => "Floor pictures that changed on the way",
            ("room", "light") => "Brightness outside what the format holds",
            ("room", "id") => "Room tags that changed on the way",
            ("room", "effect") => "Room effects that changed on the way",
            ("room", "slope") => "Sloped floors or ceilings",
            ("room", "panning") => "Shifted floor or ceiling pictures",
            ("room", "rotation") => "Turned floor or ceiling pictures",
            ("room", "scale") => "Scaled floor or ceiling pictures",
            ("room", "own-light") => "Floors or ceilings with their own brightness",
            ("room", "colour") => "Coloured light",
            ("room", "fog") => "Fog",
            ("room", "see-through") => "See-through floors or ceilings",
            ("room", "damage") => "Damage set by number",
            ("room", "gravity") => "Rooms with their own gravity",
            ("room", "hidden") => "Rooms hidden on the automap",
            ("room", "moreids") => "Extra room tags",
            ("object", "position-fraction") => "Object positions that are not whole numbers (rounded)",
            ("object", "height") => "Objects raised off the floor",
            ("object", "angle") => "Object angles the format cannot hold",
            ("object", "type") => "Object numbers that changed on the way",
            ("object", "action") => "Objects with an action",
            ("object", "id") => "Object ids",
            ("object", "difficulty") => "Different objects on the two easiest (or two hardest) difficulties",
            ("object", "modes") => "Objects kept out of single player, co-op or deathmatch",
            ("object", "classes") => "Objects for one player class",
            ("object", "friend") => "Friendly monsters",
            ("object", "dormant") => "Dormant objects",
            ("object", "standing") or ("object", "strifeally") => "Strife's standing and ally settings",
            ("object", "scale") => "Scaled objects",
            ("object", "see-through") => "See-through or invisible objects",
            ("object", "countsecret") => "Objects that count as a secret",
            ("object", "health") => "Objects with their own health",
            ("object", "conversation") => "Conversations",
            _ => $"GZDoom's \"{key}\" setting on {on}s",
        };
        return (title, false, "");
    }
}
