using System.Globalization;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>One polyobject of a sliding or swinging door (one half of a double door): its number, walls, anchor, spot and pen.</summary>
public sealed record PolyHalf(int Number, IReadOnlyList<Line> Lines, Thing Anchor, Thing Spot, Sector? Pen);

/// <summary>
/// A door that is a polyobject (D54, D62), as Hexen's are: walls drawn in a pen outside the map and carried by the game onto the
/// doorway as the map starts, which slide aside into the wall or swing on a hinge when used. One polyobject, or two mirrored
/// halves of a double door that part.
/// </summary>
/// <param name="Doorway">The open area the door stands in when closed.</param>
/// <param name="Halves">Its polyobject, or the two halves of a double door (the first moves the second by its mirror).</param>
/// <param name="Kind">Whether it slides or swings.</param>
/// <param name="Openers">The walls whose action moves it: its own faces, then any wall elsewhere naming it (a switch).</param>
/// <param name="Settings">How it behaves: speed, closing again or staying open, its lock (the walls' lock number).</param>
/// <param name="Monsters">Whether monsters open it too.</param>
public sealed record PolyDoor(Sector Doorway, IReadOnlyList<PolyHalf> Halves, DoorKind Kind, IReadOnlyList<Line> Openers, DoorSettings Settings, bool Monsters)
{
    /// <summary>Its own walls that open it when used.</summary>
    public IEnumerable<Line> Own => Openers.Where(l => Halves.Any(h => h.Lines.Contains(l)));

    /// <summary>The walls elsewhere that open it (switches).</summary>
    public IEnumerable<Line> Remote => Openers.Where(l => !Halves.Any(h => h.Lines.Contains(l)));

    /// <summary>Whether it is a double door: two halves that part.</summary>
    public bool Double => Halves.Count == 2;

    /// <summary>The number the walls that open it name: the first half's, which moves the other by its mirror.</summary>
    public int Number => Halves[0].Number;
}

/// <summary>
/// Sliding and swinging doors made of polyobjects (D62): found in any map from the walls whose action moves a polyobject
/// standing in a doorway as a door (Polyobj_DoorSlide, Polyobj_DoorSwing), made from a door of WadSmith's or the game's own (a
/// rising door's doorway, opened, with the polyobject built in a pen below the map), changed, and turned back into a rising
/// door. GZDoom's rules (po_man.cpp, p_lnspec.cpp, maploader/polyobjects.cpp, 4.14.2): a slide moves its distance along its
/// byte angle at speed/8 a tic; a swing turns its angle (64 = a quarter turn) at speed×90/64/8 degrees a tic about its spot,
/// counter-clockwise, and its mirror the other way; a mirror slides the opposite way; a negative wait keeps it open.
/// </summary>
public static class PolyDoors
{
    /// <summary>GZDoom's polyobject door actions: Polyobj_DoorSwing (po, speed, angle, delay), Polyobj_DoorSlide (po, speed, angle, distance, delay).</summary>
    public const int DoorSwing = 7;
    public const int DoorSlide = 8;

    /// <summary>How thick a door WadSmith makes is, in map units: a door 8 thick in the middle of the 16-deep doorway.</summary>
    public const double Thickness = 8;

    /// <summary>The room round a door in its pen.</summary>
    public const double PenMargin = 32;

    /// <summary>A swing of a quarter turn, in byte angles.</summary>
    public const int QuarterTurn = 64;

    /// <summary>The field that marks a pen WadSmith drew for a door's polyobjects.</summary>
    public const string PenField = "user_wadsmith_pen";

    /// <summary>The lock field GZDoom reads on any wall: its action needs that key.</summary>
    public const string LockField = "locknumber";

    /// <summary>Whether an action moves a polyobject as a door.</summary>
    public static bool IsDoorAction(int special) => special is DoorSwing or DoorSlide;

    /// <summary>
    /// The sliding or swinging door standing in <paramref name="area"/> when the map starts, or null: the polyobjects moved by a
    /// door action whose middle (where they stand) lies in it, with their mirror.
    /// </summary>
    public static PolyDoor? Recognise(MapGeometry geometry, Sector area, string family)
    {
        if (!area.OnMap) return null;
        var f = geometry.Frame;
        var polys = geometry.Polyobjects(family);
        if (polys.Count == 0) return null;
        foreach (var poly in polys)
        {
            if (geometry.SectorAt(Middle(f, poly)) is not { } standing || !ReferenceEquals(standing, area)) continue;
            if (Of(geometry, poly, family) is { } door) return door;
        }

        return null;
    }

    /// <summary>The sliding or swinging door a wall belongs to (one of its polyobject's walls) or opens (a switch), or null.</summary>
    public static PolyDoor? OfWall(MapGeometry geometry, Line wall, string family)
    {
        var polys = geometry.Polyobjects(family);
        if (polys.Count == 0 || !wall.OnMap) return null;
        var f = geometry.Frame;
        foreach (var poly in polys)
        {
            if (poly.Lines.Any(slot => ReferenceEquals(f.Lines[slot].Element, wall)) || (IsDoorAction(wall.Special) && wall.Args[0] == poly.Number))
            {
                return Of(geometry, poly, family);
            }
        }

        return null;
    }

    /// <summary>The door a polyobject is a part of: it, its mirror (or the polyobject it mirrors), and the walls naming either.</summary>
    private static PolyDoor? Of(MapGeometry geometry, PolyobjectFrame poly, string family)
    {
        var f = geometry.Frame;
        var polys = geometry.Polyobjects(family);

        // The other half: the polyobject it mirrors, or the one mirroring it. The first half is the one whose mirror moves the
        // other; when each names the other (as Hexen's do), the one the door actions name.
        var partner = (poly.Mirror != 0 && poly.Mirror != poly.Number ? polys.FirstOrDefault(p => p.Number == poly.Mirror) : null)
            ?? polys.FirstOrDefault(p => p.Mirror == poly.Number && p.Number != poly.Number);
        bool Named(PolyobjectFrame p) => geometry.LinesWithArg(0, p.Number).Any(slot => IsDoorAction(f.Lines[slot].Special));
        var first = poly;
        if (partner is not null)
        {
            var (moves, movedBy) = (poly.Mirror == partner.Number, partner.Mirror == poly.Number);
            if (moves && movedBy) first = Named(poly) || !Named(partner) ? poly : partner;
            else if (movedBy) first = partner;
        }

        var halves = new List<PolyobjectFrame> { first };
        if (partner is not null) halves.Add(ReferenceEquals(first, poly) ? partner : poly);
        var numbers = halves.Select(h => h.Number).ToHashSet();
        var openers = new List<Line>();
        foreach (var half in halves)
        {
            foreach (var slot in half.Lines)
            {
                if (f.Lines[slot].Element is { OnMap: true } l && IsDoorAction(l.Special) && numbers.Contains(l.Args[0])) openers.Add(l);
            }
        }

        foreach (var number in numbers)
        {
            foreach (var slot in geometry.LinesWithArg(0, number))
            {
                if (f.Lines[slot].Element is { OnMap: true } l && IsDoorAction(l.Special) && !openers.Contains(l)) openers.Add(l);
            }
        }

        if (openers.Count == 0 || geometry.SectorAt(Middle(f, first)) is not { } doorway) return null;
        var o = openers[0];
        var kind = o.Special == DoorSlide ? DoorKind.Slides : DoorKind.Swings;
        var delay = kind == DoorKind.Slides ? o.Args[4] : o.Args[3];
        var settings = new DoorSettings(o.Args[1] >= Doors.FastSpeed, delay < 0, LockOf(o), delay < 0 ? Doors.Wait : delay);
        var made = new List<PolyHalf>();
        foreach (var h in halves)
        {
            if (f.Things[h.Anchor].Element is not { } anchor || f.Things[h.Spot].Element is not { } spot) return null;
            var lines = h.Lines.Select(slot => f.Lines[slot].Element).OfType<Line>().ToList();
            var pen = lines.Count > 0 ? lines[0].Front.Sector : null;
            made.Add(new PolyHalf(h.Number, lines, anchor, spot, pen is { OnMap: true } ? pen : null));
        }

        return new PolyDoor(doorway, made, kind, openers, settings, o.Flag("monsteruse") || o.Flag("monsteractivate"));
    }

    /// <summary>Where a polyobject stands as the map starts: the middle of its walls, carried.</summary>
    private static MapPoint Middle(MapFrame f, PolyobjectFrame poly)
    {
        var (x, y) = (0.0, 0.0);
        foreach (var slot in poly.Lines)
        {
            var (a, b) = (f.Vertices[f.Lines[slot].V1], f.Vertices[f.Lines[slot].V2]);
            x += (a.X + b.X) / 2;
            y += (a.Y + b.Y) / 2;
        }

        return new MapPoint(x / poly.Lines.Count + poly.Dx, y / poly.Lines.Count + poly.Dy);
    }

    /// <summary>The lock a wall's action needs (GZDoom's lock number field), or 0.</summary>
    public static int LockOf(Line wall) => wall.Extra.TryGet(LockField, out var v) && v.Kind == UdmfValueKind.Integer ? (int)v.AsInt() : 0;

    /// <summary>
    /// The action and arguments that move door <paramref name="number"/> as <paramref name="settings"/> say: a slide of
    /// <paramref name="distance"/> along <paramref name="angle"/> (a byte angle), or a quarter-turn swing; a negative wait keeps it open.
    /// </summary>
    public static (int Special, int[] Args) Encode(DoorKind kind, DoorSettings settings, int number, int angle, int distance)
    {
        var speed = settings.Fast ? Doors.FastSpeed : Doors.NormalSpeed;
        var wait = settings.StaysOpen ? -1 : settings.Delay;
        return kind == DoorKind.Slides
            ? (DoorSlide, [number, speed, angle & 255, distance, wait])
            : (DoorSwing, [number, speed, QuarterTurn, wait, 0]);
    }

    /// <summary>
    /// Changes how a sliding or swinging door behaves through <paramref name="edit"/>: every wall that opens it gets the same
    /// action with the new speed, wait and lock, keeping where it slides to, and whether monsters open it.
    /// </summary>
    public static void Change(StructureEdit edit, PolyDoor door, DoorSettings settings, bool monsters)
    {
        foreach (var l in door.Openers)
        {
            var (special, args) = Encode(door.Kind, settings, l.Args[0], l.Args[2], l.Args[3]);
            SetAction(edit, l, special, args);
            edit.Field(l, LockField, settings.Lock != 0 ? UdmfValue.Int(settings.Lock) : (UdmfValue?)null);
            edit.Flag(l, "monsteruse", monsters && settings.Lock == 0);
        }
    }

    private static void SetAction(StructureEdit edit, Line l, int special, int[] args)
    {
        edit.Set(l, x => x.Special, (x, v) => x.Special = v, special);
        for (var i = 0; i < 5; i++)
        {
            var k = i;
            edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, i < args.Length ? args[k] : 0);
        }
    }

    /// <summary>Where a door stands as the map starts: the middle of its polyobjects' walls, carried.</summary>
    public static MapPoint StandsAt(PolyDoor door)
    {
        var points = door.Halves.SelectMany(h => h.Lines.Select(l => new MapPoint(((l.Start.X + l.End.X) / 2) + h.Spot.X - h.Anchor.X, ((l.Start.Y + l.End.Y) / 2) + h.Spot.Y - h.Anchor.Y))).ToList();
        return points.Count == 0 ? default : new MapPoint(points.Average(p => p.X), points.Average(p => p.Y));
    }

    /// <summary>
    /// After a paste (D62): a polyobject's anchor, spot or start line copied would name the same polyobject twice, which the
    /// game cannot place, so they go; a door whose doorway was copied with its spot is made again in the copy, its own
    /// polyobjects in a pen of their own, as the door it was copied from (<paramref name="originals"/>, by number) opens.
    /// <paramref name="moved"/> is how far the copy was placed from where it was copied.
    /// </summary>
    public static void AfterPaste(StructureEdit edit, IReadOnlyList<Element> pasted, IReadOnlyDictionary<int, PolyDoor> originals, MapPoint moved, Look look, string family, Func<string, int?> heightOf)
    {
        var doors = new List<PolyDoor>();
        foreach (var t in pasted.OfType<Thing>().Where(t => t.OnMap).ToList())
        {
            if (Polyobjects.SpotKind(t.Type, family) is not null && originals.TryGetValue(t.Angle, out var door) && !doors.Contains(door)) doors.Add(door);
            if (Polyobjects.SpotKind(t.Type, family) is not null || Polyobjects.IsAnchor(t.Type, family)) edit.Remove(t);
        }

        foreach (var l in pasted.OfType<Line>().Where(l => l.OnMap && l.Special is Polyobjects.StartLine or Polyobjects.ExplicitLine)) SetAction(edit, l, 0, []);
        foreach (var door in doors)
        {
            var at = StandsAt(door);
            if (LiveSectorAt(edit.Map, new MapPoint(at.X + moved.X, at.Y + moved.Y)) is not { } doorway || ReferenceEquals(doorway, door.Doorway)) continue;
            var picture = door.Own.FirstOrDefault()?.Front.Middle;
            var copy = new PolyDoor(doorway, [], door.Kind, [], door.Settings, door.Monsters);
            if (ToRising(edit, copy, look, heightOf, picture is "-" or "" ? null : picture) is not null) continue;
            var layout = new Layout(door.Double, door.Kind == DoorKind.Slides ? SlideDirection(door) : null, door.Kind == DoorKind.Swings ? SwingDirection(door) : null);
            FromRising(edit, doorway, door.Kind, look, family, layout, picture is "-" or "" ? null : picture);
        }
    }

    /// <summary>Which way a sliding door slides open: its action's angle, as a unit direction on the map.</summary>
    public static MapPoint SlideDirection(PolyDoor door)
    {
        var a = door.Openers[0].Args[2] * Math.PI / 128;
        return new MapPoint(Math.Round(Math.Cos(a), 6), Math.Round(Math.Sin(a), 6));
    }

    /// <summary>
    /// Which side a swinging door swings into, as a unit direction across its doorway: where its first half's free end comes to
    /// rest, a quarter turn counter-clockwise about its spot.
    /// </summary>
    public static MapPoint SwingDirection(PolyDoor door)
    {
        var half = door.Halves[0];
        var (sx, sy) = (half.Spot.X, half.Spot.Y);
        var (dx, dy) = (half.Spot.X - half.Anchor.X, half.Spot.Y - half.Anchor.Y);
        var free = half.Lines.SelectMany(l => new[] { l.Start, l.End }).Select(v => new MapPoint(v.X + dx, v.Y + dy)).OrderByDescending(p => ((p.X - sx) * (p.X - sx)) + ((p.Y - sy) * (p.Y - sy))).First();
        var (rx, ry) = (-(free.Y - sy), free.X - sx);
        var length = Math.Sqrt((rx * rx) + (ry * ry));
        return length <= 0 ? new MapPoint(0, 0) : new MapPoint(rx / length, ry / length);
    }

    /// <summary>A direction in a compass word: north, north-east, east …, as the plan has north up.</summary>
    public static string Compass(MapPoint direction)
    {
        var degrees = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        var sector = (int)Math.Round((degrees < 0 ? degrees + 360 : degrees) / 45) % 8;
        return ((string[])["east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east"])[sector];
    }

    /// <summary>The byte angle of a direction (east 0, north 64), as polyobject actions take it.</summary>
    public static int ByteAngle(double dx, double dy)
    {
        var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
        return (int)Math.Round((degrees < 0 ? degrees + 360 : degrees) * 256 / 360) & 255;
    }

    /// <summary>
    /// How a door made into a polyobject one is laid out: two halves that part, and — as a direction on the map — the end a
    /// sliding door slides toward (the other end when only that one has room) and the side a swinging door swings into. Null
    /// directions take the door's first end and its second face's side.
    /// </summary>
    public sealed record Layout(bool Double = false, MapPoint? Toward = null, MapPoint? Into = null);

    /// <summary>
    /// Makes a rising door (its area <paramref name="doorArea"/>, closed, opened by its two faces, as the Door tool makes it)
    /// a sliding or swinging door through <paramref name="edit"/>: the area opens up (to its frames' height for a sliding door,
    /// to the height of the room it swings into for a swinging one, whose frames rise to it), its faces lose their action, and
    /// the door is built as a polyobject — or two that part — <see cref="Thickness"/> thick in its middle, showing
    /// <paramref name="picture"/> on its faces and the track on its ends, in a pen drawn below the map; its faces (and any switch
    /// that opened it) open it with the door's speed, wait, lock and monsters. Works on the map as it is, so it can follow the
    /// door's making in one undo step. Returns why it cannot be made, or null.
    /// </summary>
    public static string? FromRising(StructureEdit edit, Sector doorArea, DoorKind kind, Look look, string family, Layout layout, string? picture = null)
    {
        if (kind is not (DoorKind.Slides or DoorKind.Swings)) throw new ArgumentOutOfRangeException(nameof(kind));
        var map = edit.Map;
        var walls = map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, doorArea) || ReferenceEquals(l.Back?.Sector, doorArea))).ToList();
        var faces = walls.Where(l => ReferenceEquals(l.Back?.Sector, doorArea) && l.Args[0] == 0 && Doors.Decode(l.Special, l.Args) is not null).ToList();
        if (faces.Count != 2 || !Doors.Parallel(faces[0], faces[1])) return "Only a door with two faces, one either side, can slide or swing.";
        var settings = Doors.Decode(faces[0].Special, faces[0].Args)!;
        var monsters = faces[0].Flag("monsteruse") || faces[0].Flag("monsteractivate");
        var remote = doorArea.Tag != 0 ? map.Lines.Where(l => l.OnMap && l.Args[0] == doorArea.Tag && Doors.Decode(l.Special, l.Args) is not null).ToList() : [];
        var texture = picture ?? (faces[0].Front.Top is not ("-" or "") ? faces[0].Front.Top : look.Door);

        // The doorway: from the first face's start along it (u), and from it across to the second face (n).
        var (a, b) = (faces[0], faces[1]);
        var width = a.Length;
        var (ux, uy) = ((a.End.X - a.Start.X) / width, (a.End.Y - a.Start.Y) / width);
        var (nx, ny) = (-uy, ux);
        var depth = ((b.Start.X - a.Start.X) * nx) + ((b.Start.Y - a.Start.Y) * ny);
        if (depth < 0) (nx, ny, depth) = (-nx, -ny, -depth);
        if (depth < 4) return "This door is too thin to hold a door that slides or swings.";
        MapPoint At(double along, double across) => new(a.Start.X + (ux * along) + (nx * across), a.Start.Y + (uy * along) + (ny * across));
        var thick = Math.Min(Thickness, depth / 2);
        var (near, far) = ((depth - thick) / 2, (depth + thick) / 2);
        var rooms = new[] { a.Front.Sector, b.Front.Sector };

        // The halves along the doorway, the first the one the door actions name, and where each goes.
        IReadOnlyList<(double From, double To)> spans = layout.Double ? [(0, width / 2), (width / 2, width)] : [(0, width)];

        // A swing turns the first half counter-clockwise and its mirror the other way, each about its hinge. Turned
        // counter-clockwise, a door pointing along u from its hinge comes to point left of u: on the second face's side when
        // n (first face toward second) is u turned left. A double door's halves hinge at their outer ends; when they would
        // swing the wrong way, the end half leads instead.
        var leftIsFar = (ux * ny) - (uy * nx) > 0;
        var intoFar = layout.Into is not { } into || (into.X * nx) + (into.Y * ny) > 0;
        var towardStart = layout.Toward is not { } toward || (toward.X * -ux) + (toward.Y * -uy) > 0;
        if (kind == DoorKind.Swings && layout.Double && leftIsFar != intoFar) spans = [spans[1], spans[0]];
        double? ceiling;
        if (kind == DoorKind.Slides)
        {
            // It slides into the wall beside the doorway: there must be wall (no room) there, the half's width of it.
            bool Room(double along) => LiveSectorAt(map, At(along, depth / 2)) is { } s && s.CeilingHeight > s.FloorHeight && !ReferenceEquals(s, doorArea);
            bool Clear(double from, double to)
            {
                for (var d = from; d <= to + 1e-6; d += 4)
                {
                    if (Room(d)) return false;
                }

                return true;
            }

            var reach = layout.Double ? width / 2 : width;
            var (startClear, endClear) = (Clear(-reach + 2, -2), Clear(width + 2, width + reach - 2));
            if (layout.Double ? !(startClear && endClear) : !(startClear || endClear))
            {
                return layout.Double
                    ? string.Create(CultureInfo.InvariantCulture, $"A double sliding door needs wall {reach:0} wide either side of the doorway to slide into, and a room is closer on {(startClear ? "one side" : endClear ? "one side" : "both sides")}.")
                    : string.Create(CultureInfo.InvariantCulture, $"A sliding door needs wall {reach:0} wide beside the doorway to slide into, and a room is closer on both sides. Make it a double door, or narrower.");
            }

            towardStart = layout.Double || (towardStart ? startClear : !endClear);
            ceiling = null;
        }
        else
        {
            // It swings a quarter turn into a room: the room's height is the doorway's, as GZDoom shows the door that tall there.
            var side = intoFar ? rooms[1] : rooms[0];
            var swingRoom = IsFrame(map, side, doorArea) ? Beyond(map, side, doorArea) ?? side : side;
            ceiling = swingRoom.CeilingHeight;
        }

        // Numbers for its polyobjects, before anything changes.
        var used = UsedNumbers(map, family);
        var numbers = new List<int>();
        for (var n = 1; numbers.Count < spans.Count && n < 255; n++)
        {
            if (!used.Contains(n)) numbers.Add(n);
        }

        if (numbers.Count < spans.Count) return "The map has no polyobject number left for another door.";

        // The doorway opens: the area up to its frames (or to the room it swings into), the faces with no action left.
        var neighbours = rooms.Select(r => r.CeilingHeight).ToList();
        var top = ceiling ?? neighbours.Min();
        edit.Set(doorArea, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, top);
        foreach (var room in rooms.Where(r => !ReferenceEquals(r, doorArea) && kind == DoorKind.Swings && IsFrame(map, r, doorArea) && r.CeilingHeight < top))
        {
            edit.Set(room, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, top);
        }

        // Where a room beside is taller than the doorway the wall above shows on its side, where it is lower on the doorway's.
        foreach (var l in faces)
        {
            SetAction(edit, l, 0, []);
            edit.Flag(l, "playeruse", false);
            edit.Flag(l, "monsteruse", false);
            edit.Flag(l, "repeatspecial", false);
            edit.Set(l.Front, x => x.Top, (x, v) => x.Top = v, l.Front.Sector.CeilingHeight > top ? look.Wall : "-");
            edit.Set(l.Back!, x => x.Top, (x, v) => x.Top = v, l.Front.Sector.CeilingHeight < top ? look.Wall : "-");
        }

        // The pen: below the map, a free place as wide as the door with room round it.
        var halves = new List<(int Number, List<Line> Faces, MapPoint Key)>();
        // In the pen each half lies on its own, a margin apart: halves touching there would share corners once GZDoom's node
        // builder welds corners in one place, and each would then take in the other's walls (measured: a double door that did
        // not move).
        var boxes = spans.Select(sp => new[] { At(sp.From, near), At(sp.To, near), At(sp.To, far), At(sp.From, far) }).Select(c => (X0: c.Min(p => p.X), Y0: c.Min(p => p.Y), X1: c.Max(p => p.X), Y1: c.Max(p => p.Y))).ToList();
        var penW = boxes.Sum(box => Math.Ceiling(box.X1 - box.X0) + PenMargin) + PenMargin;
        var penH = boxes.Max(box => Math.Ceiling(box.Y1 - box.Y0)) + (2 * PenMargin);
        var penAt = FreePlace(map, penW, penH);
        var offsets = new List<(double Dx, double Dy)>();
        var across0 = penAt.X + PenMargin;
        foreach (var box in boxes)
        {
            offsets.Add((across0 - box.X0, penAt.Y + PenMargin - box.Y0));
            across0 += Math.Ceiling(box.X1 - box.X0) + PenMargin;
        }
        var pen = edit.Add(new Sector { FloorHeight = doorArea.FloorHeight, CeilingHeight = top, FloorTexture = doorArea.FloorTexture, CeilingTexture = doorArea.CeilingTexture, Light = doorArea.Light });
        edit.Field(pen, PenField, UdmfValue.Bool(true));
        var penCorners = new[] { new MapPoint(penAt.X, penAt.Y), new MapPoint(penAt.X, penAt.Y + penH), new MapPoint(penAt.X + penW, penAt.Y + penH), new MapPoint(penAt.X + penW, penAt.Y) };
        Loop(edit, penCorners, pen, _ => look.Wall);

        // Each half: its walls in the pen (facing out into it), carried onto the doorway by its spot less its anchor.
        for (var i = 0; i < spans.Count; i++)
        {
            var (from, to) = spans[i];

            // A swinging half turns about its hinge, at the doorway's side; a sliding half moves as a whole.
            var hingeAtStart = spans.Count == 2 ? from == 0 : leftIsFar == intoFar;
            var key = kind == DoorKind.Swings ? At(hingeAtStart ? from : to, depth / 2) : At((from + to) / 2, depth / 2);
            var (dx, dy) = offsets[i];
            MapPoint InPen(MapPoint p) => new(p.X + dx, p.Y + dy);

            // Counter-clockwise seen from above, so each wall's front (its right) faces out into the pen: along the near side,
            // up the far end, back along the far side, down the start end.
            var shape = new[] { At(from, near), At(to, near), At(to, far), At(from, far) }.Select(InPen).ToArray();
            if (Cross(shape) < 0) Array.Reverse(shape);
            var number = numbers[i];
            var lines = Loop(edit, shape, pen, s => Math.Abs(s.Length - (to - from)) < 0.5 ? texture : look.Track);
            var ends = lines.Where(l => Math.Abs(l.Length - (to - from)) >= 0.5).ToList();
            var long_ = lines.Where(l => Math.Abs(l.Length - (to - from)) < 0.5).ToList();

            // The picture lines up across the halves of a double door, seen from either side.
            foreach (var face in long_)
            {
                var start = new MapPoint(face.Start.X - dx, face.Start.Y - dy);
                var along = ((start.X - a.Start.X) * ux) + ((start.Y - a.Start.Y) * uy);
                var forward = ((face.End.X - face.Start.X) * ux) + ((face.End.Y - face.Start.Y) * uy) > 0;
                edit.Set(face.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, Math.Round(forward ? along : width - along, 3));
            }

            var startLine = ends[0];
            SetAction(edit, startLine, Polyobjects.StartLine, [number, i == 0 && spans.Count == 2 ? numbers[1] : 0, 0, 0, 0]);
            foreach (var l in ends) edit.Flag(l, "dontpegbottom", true);
            edit.Add(Placed(Polyobjects.Anchor, InPen(key), number, family));
            edit.Add(Placed(Polyobjects.Spot, key, number, family));
            halves.Add((number, long_, key));
        }

        // What opens it: its faces (and the switches that opened the rising door), naming the first half.
        var angle = kind == DoorKind.Slides ? ByteAngle(towardStart ? -ux : ux, towardStart ? -uy : uy) : QuarterTurn;
        var distance = (int)Math.Round(layout.Double ? width / 2 : width);
        var (special, args) = Encode(kind, settings, numbers[0], angle, distance);
        foreach (var l in halves.SelectMany(h => h.Faces).Concat(remote))
        {
            SetAction(edit, l, special, args);
            edit.Flag(l, "playeruse", true);
            edit.Flag(l, "repeatspecial", true);
            edit.Flag(l, "monsteruse", monsters && settings.Lock == 0);
            edit.Field(l, LockField, settings.Lock != 0 ? UdmfValue.Int(settings.Lock) : (UdmfValue?)null);
        }

        return null;
    }

    /// <summary>Twice the signed area of a loop: positive when it runs counter-clockwise.</summary>
    private static double Cross(MapPoint[] loop)
    {
        var sum = 0.0;
        for (var i = 0; i < loop.Length; i++)
        {
            var (p, q) = (loop[i], loop[(i + 1) % loop.Length]);
            sum += (p.X * q.Y) - (q.X * p.Y);
        }

        return sum;
    }

    /// <summary>Whether a room beside the door is one of its frames: shallow, every wall of it close to the door.</summary>
    private static bool IsFrame(EditorMap map, Sector room, Sector doorArea)
    {
        if (room.CeilingHeight <= room.FloorHeight) return false;
        var lines = map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back?.Sector, room))).ToList();
        if (lines.Count == 0 || lines.Count > 8) return false;
        var xs = lines.SelectMany(l => new[] { l.Start.X, l.End.X }).ToList();
        var ys = lines.SelectMany(l => new[] { l.Start.Y, l.End.Y }).ToList();
        return Math.Min(xs.Max() - xs.Min(), ys.Max() - ys.Min()) <= Doors.FrameDepth + 0.5 && lines.Any(l => ReferenceEquals(l.Back?.Sector, doorArea) || ReferenceEquals(l.Front.Sector, doorArea));
    }

    /// <summary>
    /// Makes a sliding or swinging door a rising one again through <paramref name="edit"/>: its polyobjects, their anchors and
    /// spots go, and their pen when nothing else is in it; its doorway closes on its floor and the walls either side of where it
    /// stood open it as Doom's doors do, with its speed, wait, lock and monsters and its picture on them (its frames that
    /// picture's height again, from <paramref name="heightOf"/>); switches that opened it open the rising door by the doorway's
    /// tag. Works on the map as it is. Returns why it cannot, or null.
    /// </summary>
    public static string? ToRising(StructureEdit edit, PolyDoor door, Look look, Func<string, int?>? heightOf = null, string? shows = null)
    {
        var map = edit.Map;
        var doorway = door.Doorway;
        // Its faces: the openings to the rooms (or frames) either side, not the closed slabs of wall beside it.
        var faces = map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, doorway) || ReferenceEquals(l.Back.Sector, doorway)) && !ReferenceEquals(l.Front.Sector, l.Back.Sector)
            && (ReferenceEquals(l.Front.Sector, doorway) ? l.Back.Sector : l.Front.Sector) is { } other && other.CeilingHeight > other.FloorHeight).ToList();
        if (faces.Count != 2 || !Doors.Parallel(faces[0], faces[1])) return "Only a door standing in a doorway with an opening on each side can open upward.";
        var picture = shows ?? (door.Own.FirstOrDefault()?.Front.Middle is { } shown and not ("-" or "") ? shown : look.Door);

        Remove(edit, door, clearSwitches: false);

        // The doorway closes; its faces open it, each with the doorway behind it.
        var frames = faces.Select(l => ReferenceEquals(l.Front.Sector, doorway) ? l.Back!.Sector : l.Front.Sector).Where(r => IsFrame(map, r, doorway)).ToList();
        if (heightOf?.Invoke(picture) is { } height && height > 0)
        {
            foreach (var frame in frames)
            {
                var room = Beyond(map, frame, doorway);
                edit.Set(frame, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, Math.Min(room?.CeilingHeight ?? frame.CeilingHeight, frame.FloorHeight + height));
            }
        }

        edit.Set(doorway, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, doorway.FloorHeight);
        var (special, args) = Doors.Encode(door.Settings, 0);
        foreach (var l in faces)
        {
            if (ReferenceEquals(l.Front.Sector, doorway)) Walls.Turn(edit, l);
            SetAction(edit, l, special, args);
            edit.Flag(l, "playeruse", true);
            edit.Flag(l, "repeatspecial", true);
            edit.Flag(l, "monsteruse", door.Monsters && door.Settings.Lock == 0);
            edit.Field(l, LockField, (UdmfValue?)null);
            edit.Set(l.Front, x => x.Top, (x, v) => x.Top = v, picture);
            edit.Set(l.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, 0);
            edit.Set(l.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, 0);
            edit.Set(l.Back!, x => x.Top, (x, v) => x.Top = v, "-");
        }

        // Switches open it by the doorway's tag.
        var remote = door.Remote.Where(l => l.OnMap).ToList();
        if (remote.Count > 0)
        {
            var tag = doorway.Tag != 0 ? doorway.Tag : map.Sectors.Where(x => x.OnMap).Select(x => x.Tag).DefaultIfEmpty(0).Max() + 1;
            edit.Set(doorway, s => s.Tag, (s, v) => s.Tag = v, tag);
            var (rs, ra) = Doors.Encode(door.Settings, 0, null, tag);
            foreach (var l in remote)
            {
                SetAction(edit, l, rs, ra);
                edit.Field(l, LockField, (UdmfValue?)null);
            }
        }

        return null;
    }

    /// <summary>
    /// Takes a sliding or swinging door's polyobjects off the map through <paramref name="edit"/>: their walls, faces and corners,
    /// their anchors and spots, and their pens when nothing else is in them; with <paramref name="clearSwitches"/>, the walls
    /// elsewhere that opened it lose their action. Its doorway stays, open.
    /// </summary>
    public static void Remove(StructureEdit edit, PolyDoor door, bool clearSwitches = true)
    {
        var map = edit.Map;
        foreach (var half in door.Halves)
        {
            foreach (var l in half.Lines.Where(l => l.OnMap)) RemoveWall(edit, l);
            edit.Remove(half.Anchor);
            edit.Remove(half.Spot);
        }

        foreach (var pen in door.Halves.Select(h => h.Pen).OfType<Sector>().Distinct().Where(p => p.OnMap))
        {
            var walls = map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, pen) || ReferenceEquals(l.Back?.Sector, pen))).ToList();
            if (walls.Any(l => l.Back is not null) || walls.Count != 4) continue;
            foreach (var l in walls) RemoveWall(edit, l);
            edit.Remove(pen);
        }

        if (!clearSwitches) return;
        foreach (var l in door.Remote.Where(l => l.OnMap))
        {
            SetAction(edit, l, 0, []);
            edit.Field(l, LockField, (UdmfValue?)null);
        }
    }

    /// <summary>
    /// The sliding or swinging doors whose polyobjects are drawn in <paramref name="pen"/> (a pen WadSmith drew, or any area
    /// holding polyobject walls).
    /// </summary>
    public static IReadOnlyList<PolyDoor> InPen(MapGeometry geometry, Sector pen, string family)
    {
        var f = geometry.Frame;
        var found = new List<PolyDoor>();
        foreach (var poly in geometry.Polyobjects(family))
        {
            if (!poly.Lines.Any(slot => f.Lines[slot].Element is { } l && ReferenceEquals(l.Front.Sector, pen))) continue;
            if (OfWall(geometry, f.Lines[poly.Lines[0]].Element!, family) is { } door && !found.Any(d => d.Number == door.Number)) found.Add(door);
        }

        return found;
    }

    /// <summary>Takes a wall off the map with its faces, and its corners when no other wall ends there.</summary>
    private static void RemoveWall(StructureEdit edit, Line l)
    {
        var (start, end) = (l.Start, l.End);
        edit.Remove(l);
        edit.Remove(l.Front);
        if (l.Back is { } back) edit.Remove(back);
        foreach (var v in new[] { start, end })
        {
            if (!edit.Map.Lines.Any(x => x.OnMap && (ReferenceEquals(x.Start, v) || ReferenceEquals(x.End, v)))) edit.Remove(v);
        }
    }

    /// <summary>
    /// Gives a sliding or swinging door the picture <paramref name="texture"/> on its faces through <paramref name="edit"/>; a
    /// sliding door's doorway and frames take the picture's height (<paramref name="height"/>), up to the rooms beside.
    /// </summary>
    public static void Dress(StructureEdit edit, PolyDoor door, string texture, int? height)
    {
        foreach (var half in door.Halves)
        {
            foreach (var l in half.Lines.Where(l => l.OnMap && IsDoorAction(l.Special))) edit.Set(l.Front, x => x.Middle, (x, v) => x.Middle = v, texture);
        }

        if (door.Kind != DoorKind.Slides || height is not > 0) return;
        var map = edit.Map;
        var doorway = door.Doorway;
        var beside = map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, doorway) || ReferenceEquals(l.Back.Sector, doorway)))
            .Select(l => ReferenceEquals(l.Front.Sector, doorway) ? l.Back!.Sector : l.Front.Sector).Where(r => !ReferenceEquals(r, doorway)).Distinct().ToList();
        var frames = beside.Where(r => IsFrame(map, r, doorway)).ToList();
        if (frames.Count == 0) return;
        var limit = frames.Select(f => Beyond(map, f, doorway)?.CeilingHeight ?? f.CeilingHeight).Min();
        var top = Math.Min(limit, doorway.FloorHeight + height.Value);
        foreach (var frame in frames) edit.Set(frame, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, top);
        edit.Set(doorway, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, top);
    }

    /// <summary>The room on the far side of a doorway's frame from the door.</summary>
    private static Sector? Beyond(EditorMap map, Sector frame, Sector doorArea) =>
        map.Lines.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, frame) || ReferenceEquals(l.Back.Sector, frame)))
            .Select(l => ReferenceEquals(l.Front.Sector, frame) ? l.Back!.Sector : l.Front.Sector)
            .FirstOrDefault(s => !ReferenceEquals(s, doorArea) && !ReferenceEquals(s, frame) && s.CeilingHeight > s.FloorHeight);

    /// <summary>A closed loop of one-sided walls through <paramref name="corners"/>, each facing <paramref name="sector"/> on its right, with its picture.</summary>
    private static List<Line> Loop(StructureEdit edit, IReadOnlyList<MapPoint> corners, Sector sector, Func<(double Length, int Index), string> picture)
    {
        var vertices = corners.Select(p => edit.Add(new Vertex { X = Math.Round(p.X, 3), Y = Math.Round(p.Y, 3) })).ToList();
        var lines = new List<Line>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var (from, to) = (vertices[i], vertices[(i + 1) % vertices.Count]);
            var length = Math.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));
            lines.Add(edit.Add(new Line(from, to, edit.Add(new Side(sector) { Middle = picture((length, i)) }))));
        }

        return lines;
    }

    private static Thing Placed(int type, MapPoint at, int number, string family)
    {
        var t = new Thing { X = Math.Round(at.X, 3), Y = Math.Round(at.Y, 3), Type = type, Angle = number };
        foreach (var flag in Things.Everywhere(family)) t.SetFlag(flag, true);
        return t;
    }

    /// <summary>The polyobject numbers the map already uses (its start spots, anchors and start lines).</summary>
    private static HashSet<int> UsedNumbers(EditorMap map, string family)
    {
        var used = new HashSet<int>();
        foreach (var t in map.Things.Where(t => t.OnMap && (Polyobjects.IsAnchor(t.Type, family) || Polyobjects.SpotKind(t.Type, family) is not null))) used.Add(t.Angle);
        foreach (var l in map.Lines.Where(l => l.OnMap && l.Special is Polyobjects.StartLine or Polyobjects.ExplicitLine)) used.Add(l.Args[0]);
        return used;
    }

    /// <summary>A free place for a pen below the map: nothing within it or 32 round it, further east for each pen already there.</summary>
    private static MapPoint FreePlace(EditorMap map, double width, double height)
    {
        var points = map.Vertices.Where(v => v.OnMap).ToList();
        var (minX, minY) = points.Count == 0 ? (0.0, 0.0) : (points.Min(v => v.X), points.Min(v => v.Y));
        var y = (Math.Floor((minY - 256 - height) / 64) * 64) - 0;
        var x = Math.Floor(minX / 64) * 64;
        bool Taken(double px) => points.Any(v => v.X > px - 32 && v.X < px + width + 32 && v.Y > y - 32 && v.Y < y + height + 32)
            || map.Things.Any(t => t.OnMap && t.X > px - 32 && t.X < px + width + 32 && t.Y > y - 32 && t.Y < y + height + 32);
        while (Taken(x)) x += 64;
        return new MapPoint(x, y);
    }

    /// <summary>
    /// The area at a point on the map as it is now (no shapes needed): the nearest wall a ray to the east meets, and the side of
    /// it the point is on. Null in the void. Slow on a big map, so for a few points of one edit only.
    /// </summary>
    internal static Sector? LiveSectorAt(EditorMap map, MapPoint p)
    {
        Line? nearest = null;
        var best = double.MaxValue;
        foreach (var l in map.Lines)
        {
            if (!l.OnMap) continue;
            var (ax, ay, bx, by) = (l.Start.X, l.Start.Y, l.End.X, l.End.Y);
            if ((ay > p.Y) == (by > p.Y)) continue;
            var x = ax + ((p.Y - ay) * (bx - ax) / (by - ay));
            if (x < p.X || x - p.X >= best) continue;
            (best, nearest) = (x - p.X, l);
        }

        if (nearest is null) return null;

        // The point is on the wall's front when it lies to the right of start → end.
        var cross = ((nearest.End.X - nearest.Start.X) * (p.Y - nearest.Start.Y)) - ((nearest.End.Y - nearest.Start.Y) * (p.X - nearest.Start.X));
        return cross < 0 ? nearest.Front.Sector : nearest.Back?.Sector;
    }
}
