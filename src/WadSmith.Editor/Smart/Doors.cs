using System.Globalization;
using System.Text.RegularExpressions;
using WadSmith.Core.Graphics;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>How a door opens (D54, D62).</summary>
public enum DoorKind
{
    /// <summary>Its ceiling rises, as every door of Doom, Heretic and Hexen made of an area does (Door_Raise and the rest).</summary>
    Rises,

    /// <summary>Its picture slides open, as Strife's sliding doors do (Door_Animated): the frames of an ANIMDEFS <c>animateddoor</c>.</summary>
    Animated,

    /// <summary>It slides aside into the wall beside it: a polyobject, as Hexen's sliding doors are (Polyobj_DoorSlide).</summary>
    Slides,

    /// <summary>It swings on a hinge at its side into a room: a polyobject, as Hexen's swinging doors are (Polyobj_DoorSwing).</summary>
    Swings,
}

/// <summary>How a door behaves, in the words its Inspector uses.</summary>
/// <param name="Fast">Opens at the fast speed (Doom's "blazing" doors) instead of the normal one.</param>
/// <param name="StaysOpen">Stays open once opened, instead of closing again after <paramref name="Delay"/>.</param>
/// <param name="Lock">The lock number (0 for none): 129 red, 130 blue, 131 yellow in Doom (card or skull).</param>
/// <param name="Delay">How long it waits open before closing, in game tics (35 a second).</param>
public sealed record DoorSettings(bool Fast, bool StaysOpen, int Lock, int Delay = Doors.Wait);

/// <summary>
/// A door found in the map (D21): the area that opens, the walls that open it, how it behaves, and
/// how high it opens. Found the same way in any map, made by WadSmith or not.
/// </summary>
/// <param name="Area">The door's area: its ceiling rises to open it.</param>
/// <param name="Openers">The walls that open it: its own walls, used from the rooms beside it, then any wall elsewhere naming its tag (a switch, a line walked over).</param>
/// <param name="Settings">How it behaves.</param>
/// <param name="Monsters">Whether monsters open it too.</param>
/// <param name="OpensTo">The height its ceiling rises to: 4 below the lowest ceiling beside it (the engine's rule).</param>
public sealed record Door(Sector Area, IReadOnlyList<Line> Openers, DoorSettings Settings, bool Monsters, double OpensTo)
{
    /// <summary>The walls elsewhere that open it by its tag.</summary>
    public IEnumerable<Line> Remote => Openers.Where(l => l.Args[0] != 0);

    /// <summary>Its own walls that open it when used.</summary>
    public IEnumerable<Line> Own => Openers.Where(l => l.Args[0] == 0);

    /// <summary>How the walls elsewhere set it off, in words: "a switch", "2 lines walked over", "a wall shot".</summary>
    public IEnumerable<string> RemoteWays() => Triggers.Ways([.. Remote]);

    /// <summary>Whether the door is open as the map starts (its ceiling above its floor).</summary>
    public bool StartsOpen => Area.CeilingHeight > Area.FloorHeight;

    /// <summary>How it opens: a sliding door of Strife's when its walls play a picture's frames, else its ceiling rises.</summary>
    public DoorKind Kind => Openers.Count > 0 && Openers[0].Special == Doors.DoorAnimated ? DoorKind.Animated : DoorKind.Rises;
}

/// <summary>
/// Doors: recognising them (a classic local door is an area whose walls, facing into it from beside,
/// open it with a door action naming no tag: used from either room, the engine opens the area behind
/// the wall) and changing how they behave.
/// </summary>
public static partial class Doors
{
    /// <summary>
    /// The game's door pictures, for a door's "Looks like": the walls named as the family names its doors, at least 32 wide
    /// and 64 tall (tracks and key trims are narrow), by name; not the ends of Hexen's doors (D_END*), Strife's tracks and its
    /// door panel (DORTRK*, DORTKL*, DORTKS01), nor the later frames of Strife's sliding doors (their first frame is the door).
    /// </summary>
    public static IReadOnlyList<TextureInfo> Looks(string engine, IEnumerable<TextureInfo> walls)
    {
        var pattern = engine switch
        {
            "heretic" => HereticDoors(),
            "hexen" => HexenDoors(),
            "strife" => StrifeDoors(),
            _ => DoomDoors(),
        };
        return [.. walls.Where(w => pattern.IsMatch(w.Name) && w.Width >= 32 && w.Height >= 64).OrderBy(w => w.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The game's sliding door pictures (Strife's, ANIMDEFS <c>animateddoor</c>) it has as walls, by name: the only pictures a
    /// door that opens like Strife's can show, since the engine plays their frames and no others.
    /// </summary>
    public static IReadOnlyList<TextureInfo> SlidingLooks(IReadOnlyDictionary<string, AnimatedDoor> animated, IEnumerable<TextureInfo> walls) =>
        [.. walls.Where(w => animated.ContainsKey(w.Name)).OrderBy(w => w.Name, StringComparer.Ordinal)];

    /// <summary>
    /// The frames of a doorway (see <see cref="Make"/>): for each of the door's own faces, the area in front of it when it
    /// is a frame — every wall of it within <see cref="FrameDepth"/> of the face, so a room in front of a door without
    /// frames is never taken for one — with the room beyond it.
    /// </summary>
    public static IReadOnlyList<(Sector Frame, Sector Room)> FramesOf(MapGeometry geometry, Door door)
    {
        var frames = new List<(Sector, Sector)>();
        foreach (var opener in door.Own)
        {
            var frame = opener.Front.Sector;
            if (ReferenceEquals(frame, door.Area) || geometry.WallBoundsAt(frame.Slot) is not { } b) continue;

            // A frame is shallow and as wide as the door: every corner of every wall around it lies within the frame's depth of
            // the door's face, and along the face between its ends.
            var around = geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, frame) || ReferenceEquals(l.Back?.Sector, frame))).ToList();
            if (around.Count == 0 || around.SelectMany(l => new[] { l.Start, l.End }).Any(v => DistanceTo(opener, v) > FrameDepth + 0.5 || !Beside(opener, v))) continue;
            foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (!l.OnMap || ReferenceEquals(l, opener) || !Parallel(l, opener) || !(ReferenceEquals(l.Front.Sector, frame) || ReferenceEquals(l.Back?.Sector, frame))) continue;
                var room = ReferenceEquals(l.Front.Sector, frame) ? l.Back?.Sector : l.Front.Sector;
                if (room is null || ReferenceEquals(room, door.Area) || Apart(l, opener) > FrameDepth + 0.5) continue;
                frames.Add((frame, room));
                break;
            }
        }

        return frames;
    }

    /// <summary>
    /// The doorway a part of the map belongs to — a door's area, or the doorway a sliding or swinging polyobject door stands
    /// in — or null: the area itself, one of its frames, a wall round either (the door's faces and sides, a frame's walls), a
    /// face of such a wall, or one of a polyobject door's own walls. A click on the plan or in the 3D view lands on one of these,
    /// and the door is what the user means: the Inspector shows it, and Delete takes it away. Walls elsewhere that open it (a
    /// switch) are not parts of it. Looks only near the element, through the shapes: quick, for the UI thread. Asking about many
    /// elements at once, pass the same <paramref name="known"/> to each call: every area is then looked at once.
    /// </summary>
    public static Sector? DoorwayOf(MapGeometry geometry, Element element, string family, Dictionary<Sector, Sector?>? known = null) => element switch
    {
        Side { Line: { } wall } => DoorwayOf(geometry, wall, family, known),
        Line { OnMap: true } wall when PolyDoors.OfWall(geometry, wall, family) is { } poly && poly.Halves.Any(h => h.Lines.Contains(wall)) => poly.Doorway,
        Line { OnMap: true } wall => DoorwayOfArea(geometry, wall.Front.Sector, family, known) ?? (wall.Back is { } back ? DoorwayOfArea(geometry, back.Sector, family, known) : null),
        Sector { OnMap: true } area => DoorwayOfArea(geometry, area, family, known),
        _ => null,
    };

    /// <summary>The doorway an area is (a door's area, a polyobject door's doorway) or is a frame of; null for any other area.</summary>
    private static Sector? DoorwayOfArea(MapGeometry geometry, Sector area, string family, Dictionary<Sector, Sector?>? known)
    {
        if (known is null) return DoorwayOfArea(geometry, area, family);
        if (!known.TryGetValue(area, out var doorway)) known[area] = doorway = DoorwayOfArea(geometry, area, family);
        return doorway;
    }

    private static Sector? DoorwayOfArea(MapGeometry geometry, Sector area, string family)
    {
        if (!area.OnMap) return null;
        if (Recognise(geometry, area) is not null || PolyDoors.Recognise(geometry, area, family) is not null) return area;

        // A frame is shallow: a room too big to be one is passed over before its walls are looked at.
        if (area.CeilingHeight <= area.FloorHeight || geometry.WallBoundsAt(area.Slot) is not { } b || Math.Min(b.MaxX - b.MinX, b.MaxY - b.MinY) > FrameReach) return null;
        foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            if (!l.OnMap || l.Back is null) continue;
            var other = ReferenceEquals(l.Front.Sector, area) ? l.Back.Sector : ReferenceEquals(l.Back.Sector, area) ? l.Front.Sector : null;
            if (other is null || ReferenceEquals(other, area)) continue;
            if ((Recognise(geometry, other) is not null || PolyDoors.Recognise(geometry, other, family) is not null) && FrameAgainst(geometry, area, l)) return other;
        }

        return null;
    }

    /// <summary>
    /// The frames of a doorway (a door's area, or the doorway a polyobject door stands in): the shallow open areas against its
    /// walls, each within <see cref="FrameDepth"/> of the wall it shares with the doorway.
    /// </summary>
    public static IReadOnlyList<Sector> FramesBeside(MapGeometry geometry, Sector doorway)
    {
        if (geometry.WallBoundsAt(doorway.Slot) is not { } b) return [];
        var frames = new List<Sector>();
        foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            if (!l.OnMap || l.Back is null) continue;
            var other = ReferenceEquals(l.Front.Sector, doorway) ? l.Back.Sector : ReferenceEquals(l.Back.Sector, doorway) ? l.Front.Sector : null;
            if (other is null || ReferenceEquals(other, doorway) || frames.Contains(other) || !FrameAgainst(geometry, other, l)) continue;
            frames.Add(other);
        }

        return frames;
    }

    /// <summary>The widest a frame's box can be across both ways: a frame no deeper than <see cref="FrameDepth"/> along the widest door, turned 45°.</summary>
    private const double FrameReach = (MaxWidth + FrameDepth) / 1.41 + 1;

    /// <summary>Whether an area is a frame against a doorway's wall: open, every corner of its walls within a frame's depth of that wall and along it.</summary>
    private static bool FrameAgainst(MapGeometry geometry, Sector area, Line against)
    {
        if (area.CeilingHeight <= area.FloorHeight || geometry.WallBoundsAt(area.Slot) is not { } b) return false;
        var corners = geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area))).SelectMany(l => new[] { l.Start, l.End }).ToList();
        return corners.Count > 0 && corners.All(v => DistanceTo(against, v) <= FrameDepth + 0.5 && Beside(against, v));
    }

    /// <summary>
    /// Takes a door out of the wall it stood in through <paramref name="edit"/> (Delete; the user's report, 2026-10-15): its area
    /// and <paramref name="frames"/> become wall like the wall beside them. Beside pieces of wall (areas closed from floor to
    /// ceiling, as the Door tool leaves either side of a door) they join those pieces, and pieces of the same heights either side
    /// become one; with none beside, they become a piece of wall of their own, closed at the highest ceiling beside. The faces
    /// toward each room show what the wall beside shows there, walls left in one line facing the same rooms become one wall, and
    /// what stood in the doorway goes. A polyobject door's polyobjects are taken off first (<see cref="PolyDoors.Remove"/>).
    /// <paramref name="geometry"/> shows the map before this edit (the areas' walls are found through it, and read from the map).
    /// </summary>
    public static void WallUp(StructureEdit edit, MapGeometry geometry, Sector doorway, IReadOnlyCollection<Sector> frames, Look look)
    {
        if (!doorway.OnMap) return;
        var column = new HashSet<Sector>(ReferenceEqualityComparer.Instance) { doorway };
        foreach (var f in frames.Where(f => f.OnMap)) column.Add(f);
        static bool Closed(Sector s) => s.FloorHeight >= s.CeilingHeight;
        static string? Shown(string texture) => texture is "-" or "" ? null : texture;
        bool Bounds(Line l, Sector area) => ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area);
        HashSet<Line> Around(IEnumerable<Sector> areas)
        {
            var found = new HashSet<Line>(ReferenceEqualityComparer.Instance);
            foreach (var area in areas)
            {
                if (geometry.WallBoundsAt(area.Slot) is not { } b) continue;
                foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && Bounds(l, area))) found.Add(l);
            }

            return found;
        }

        var walls = Around(column);
        Sector? Across(Line l) => column.Contains(l.Front.Sector) ? l.Back?.Sector : l.Front.Sector;

        // The pieces of wall beside it, and the faces toward the rooms (walls between the column and an open area).
        var beside = walls.Select(Across).OfType<Sector>().Where(s => !column.Contains(s) && Closed(s) && Recognise(geometry, s) is null).Distinct().ToList();
        var faces = walls.Where(l => l.Back is not null && column.Contains(l.Front.Sector) != column.Contains(l.Back.Sector) && Across(l) is { } room && !Closed(room)).ToList();

        // What the wall beside each face shows toward its room: the next wall along the same line facing the same room (a piece of
        // wall's face, or the room's own solid wall), else what the face showed above the doorway, else the Look's wall.
        (string Top, string Bottom, double OffsetY, bool PegTop, bool PegBottom) LookBeside(Line face, Sector room)
        {
            foreach (var end in new[] { face.Start, face.End })
            {
                foreach (var next in geometry.LinesIn(end.X, end.Y, end.X, end.Y))
                {
                    if (!next.OnMap || walls.Contains(next) || (next.Start != end && next.End != end) || !Parallel(next, face)) continue;
                    var toward = ReferenceEquals(next.Front.Sector, room) ? next.Front : next.Back is { } b && ReferenceEquals(b.Sector, room) ? b : null;
                    if (toward is null) continue;
                    if (next.Back is null)
                    {
                        var solid = Shown(toward.Middle) ?? look.Wall;
                        return (solid, solid, toward.OffsetY, false, false);
                    }

                    var lower = Shown(toward.Bottom) ?? Shown(toward.Top) ?? look.Wall;
                    return (Shown(toward.Top) ?? lower, lower, toward.OffsetY, next.Flag("dontpegtop"), next.Flag("dontpegbottom"));
                }
            }

            var own = ReferenceEquals(face.Front.Sector, room) ? face.Front : face.Back!;
            var shown = Shown(own.Top) ?? Shown(own.Bottom) ?? look.Wall;
            return (shown, shown, own.OffsetY, true, false);
        }

        var dressed = faces.Select(l => (Line: l, Room: Across(l)!, Look: LookBeside(l, Across(l)!))).ToList();
        var standing = new List<Thing>();
        var f0 = geometry.Frame;
        foreach (var area in column)
        {
            if (geometry.WallBoundsAt(area.Slot) is not { } b) continue;
            standing.AddRange(geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(i => ReferenceEquals(geometry.SectorAt(new MapPoint(f0.Things[i].X, f0.Things[i].Y)), area)).Select(i => f0.Things[i].Element!));
        }

        // The piece it joins: the one beside it along the most wall; pieces of the same heights beside it join too.
        double Shared(Sector s) => walls.Where(l => Bounds(l, s)).Sum(l => l.Length);
        var keep = beside.OrderByDescending(Shared).FirstOrDefault() ?? doorway;
        var merging = column.Concat(beside.Where(s => s.FloorHeight == keep.FloorHeight && s.CeilingHeight == keep.CeilingHeight)).Where(s => !ReferenceEquals(s, keep)).Distinct().ToList();
        var lines = new HashSet<Line>(walls, ReferenceEqualityComparer.Instance);
        lines.UnionWith(Around(merging.Where(s => !column.Contains(s))));

        // Its own faces lose their action (walls elsewhere that open it by its tag are the caller's): what stays of them is wall.
        foreach (var l in walls.Where(l => IsDoorAction(l.Special) && l.Args[0] == 0))
        {
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, 0);
            for (var i = 0; i < 5; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, 0);
            }

            edit.Flag(l, "playeruse", false);
            edit.Flag(l, "monsteruse", false);
            edit.Flag(l, "repeatspecial", false);
        }

        foreach (var (l, room, (top, bottom, offsetY, pegTop, pegBottom)) in dressed)
        {
            var side = ReferenceEquals(l.Front.Sector, room) ? l.Front : l.Back!;
            edit.Set(side, s => s.Top, (s, v) => s.Top = v, top);
            edit.Set(side, s => s.Middle, (s, v) => s.Middle = v, "-");
            edit.Set(side, s => s.Bottom, (s, v) => s.Bottom = v, bottom);
            edit.Set(side, s => s.OffsetY, (s, v) => s.OffsetY = v, offsetY);
            edit.Flag(l, "dontpegtop", pegTop);
            edit.Flag(l, "dontpegbottom", pegBottom);
        }

        foreach (var t in standing) edit.Remove(t);
        if (ReferenceEquals(keep, doorway))
        {
            // A piece of wall of its own: closed at the highest ceiling beside it, as the Door tool closes the wall beside a door.
            var top = dressed.Select(d => d.Room.CeilingHeight).DefaultIfEmpty(column.Max(s => s.CeilingHeight)).Max();
            edit.Set(doorway, s => s.FloorHeight, (s, v) => s.FloorHeight = v, top);
            edit.Set(doorway, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, top);
        }

        foreach (var area in merging)
        {
            foreach (var l in lines.Where(l => l.OnMap))
            {
                if (ReferenceEquals(l.Front.Sector, area)) edit.Set(l.Front, s => s.Sector, (s, v) => s.Sector = v, keep);
                if (l.Back is { } back && ReferenceEquals(back.Sector, area)) edit.Set(back, s => s.Sector, (s, v) => s.Sector = v, keep);
            }

            edit.Remove(area);
        }

        // The walls inside it go; the corners of its faces where two walls now run on in one line, facing the same rooms, go too.
        var loose = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);
        foreach (var l in lines.Where(l => l.OnMap && l.Back is not null && ReferenceEquals(l.Front.Sector, keep) && ReferenceEquals(l.Back.Sector, keep)).ToList()) Deleting.RemoveWall(edit, l, loose);
        var rerouted = new List<Line>();
        List<Line> WallsAt(Vertex v) => [.. geometry.LinesIn(v.X, v.Y, v.X, v.Y).Concat(rerouted).Distinct().Where(l => l.OnMap && (l.Start == v || l.End == v))];
        foreach (var v in dressed.SelectMany(d => new[] { d.Line.Start, d.Line.End }).Concat(loose).Distinct().ToList())
        {
            if (!v.OnMap) continue;
            var at = WallsAt(v);
            if (at.Count == 0) edit.Remove(v);
            else if (at.Count == 2 && Parallel(at[0], at[1]) && Deleting.Straighten(edit, v, at[0], at[1])) rerouted.Add(at[0]);
        }
    }

    /// <summary>
    /// Gives a door the picture <paramref name="texture"/> on its faces through <paramref name="edit"/>, hung from the door's
    /// top edge as before, and the doorway the picture's size (<paramref name="size"/>): as wide as the picture about the door's
    /// middle, the wall either side giving way or growing back (<see cref="Refit"/>), and its frames, when it has them, as tall,
    /// so the wall shows above a short door and a tall one fills the doorway. Returns why the door is not the picture's width,
    /// or null when it is.
    /// </summary>
    public static string? Dress(StructureEdit edit, MapGeometry geometry, Door door, string texture, (int Width, int Height)? size)
    {
        // The frames are found on the map as it was, before the doorway changes width.
        var frames = FramesOf(geometry, door);
        foreach (var l in door.Own)
        {
            edit.Set(l.Front, x => x.Top, (x, v) => x.Top = v, texture);
            edit.Set(l.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, 0);
            edit.Set(l.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, 0);
        }

        if (size is not { } s) return null;
        string? why = s.Width > 0 ? Refit(edit, door, [.. frames.Select(f => f.Frame)], s.Width) : null;
        if (s.Height <= 0) return why;
        foreach (var (frame, room) in frames)
        {
            var ceiling = Math.Min(room.CeilingHeight, frame.FloorHeight + s.Height);
            edit.Set(frame, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, ceiling);
        }

        return why;
    }

    /// <summary>The narrowest a piece of wall beside a door is left when the door widens.</summary>
    public const double MinSlab = 8;

    /// <summary>
    /// Makes a door's column — the door and the frames in front of and behind it — <paramref name="width"/> wide along its wall,
    /// about its middle: the corners of the column's two sides move along the wall, so the door, its frames, its tracks and the
    /// pieces of wall either side (which share those corners) change together and the map stays whole. A side that is a room's
    /// own wall (a door filling its opening) cannot move; the pieces of wall beside keep <see cref="MinSlab"/>, so a picture
    /// wider than there is room for gets the widest door that fits. Returns why the door is not <paramref name="width"/> wide.
    /// </summary>
    private static string? Refit(StructureEdit edit, Door door, IReadOnlyList<Sector> frames, double width)
    {
        if (door.Own.FirstOrDefault() is not { } face || face.Length <= 0) return null;
        var (ox, oy) = (face.Start.X, face.Start.Y);
        var (dx, dy) = ((face.End.X - face.Start.X) / face.Length, (face.End.Y - face.Start.Y) / face.Length);
        double Along(Vertex v) => (v.X - ox) * dx + (v.Y - oy) * dy;

        var column = new HashSet<Sector>(ReferenceEqualityComparer.Instance) { door.Area };
        foreach (var f in frames) column.Add(f);
        bool Bounds(Line l, ISet<Sector> areas) => areas.Contains(l.Front.Sector) || (l.Back?.Sector is { } b && areas.Contains(b));
        var lines = edit.Map.Lines.Where(l => l.OnMap && Bounds(l, column)).ToList();
        var corners = lines.SelectMany(l => new[] { l.Start, l.End }).Distinct().ToList();
        var doorCorners = lines.Where(l => Bounds(l, new HashSet<Sector>(ReferenceEqualityComparer.Instance) { door.Area })).SelectMany(l => new[] { l.Start, l.End }).ToList();
        var (left, right) = (doorCorners.Min(Along), doorCorners.Max(Along));
        var now = right - left;
        if (Math.Abs(now - width) < 0.5) return null;

        // The column's two sides: its walls square to the door, at either end; beyond each, a piece of wall or the room's own wall.
        double? Room(double at, int outward)
        {
            var side = lines.Where(l => Math.Abs(Along(l.Start) - at) < 0.5 && Math.Abs(Along(l.End) - at) < 0.5).ToList();
            if (side.Count == 0) return null;
            var beyond = new HashSet<Sector>(ReferenceEqualityComparer.Instance);
            foreach (var l in side)
            {
                // Only a closed piece of wall (floor up on its ceiling, as the Door tool makes them) gives way; a room never does.
                var other = column.Contains(l.Front.Sector) ? l.Back?.Sector : l.Front.Sector;
                if (other is null || column.Contains(other) || other.FloorHeight < other.CeilingHeight) return null;
                beyond.Add(other);
            }

            // How far the side can move outward: to the far end of the piece of wall beyond it, less what a piece keeps.
            var far = edit.Map.Lines.Where(l => l.OnMap && Bounds(l, beyond)).SelectMany(l => new[] { l.Start, l.End }).Select(Along).ToList();
            var reach = outward < 0 ? at - far.Min() : far.Max() - at;
            return reach - MinSlab;
        }

        var (roomLeft, roomRight) = (Room(left, -1), Room(right, +1));
        if (roomLeft is null || roomRight is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The picture is {width:0} wide and the door {now:0}: the door's sides are rooms' walls, which it does not move. Make its opening {width:0} wide, or choose a picture {now:0} wide.");
        }

        // The middle stays; a wider door takes from both sides alike, as far as the narrower side has room.
        var half = width / 2;
        var most = now / 2 + Math.Min(roomLeft.Value, roomRight.Value);
        var fits = Math.Min(half, Math.Floor(most));
        var middle = (left + right) / 2;
        var (newLeft, newRight) = (Math.Round(middle - fits), Math.Round(middle + fits));
        foreach (var v in corners)
        {
            var at = Along(v);
            var shift = Math.Abs(at - left) < 0.5 ? newLeft - left : Math.Abs(at - right) < 0.5 ? newRight - right : 0;
            if (shift == 0) continue;
            edit.Set(v, x => x.X, (x, value) => x.X = value, Math.Round(v.X + dx * shift, 3));
            edit.Set(v, x => x.Y, (x, value) => x.Y = value, Math.Round(v.Y + dy * shift, 3));
        }

        return fits < half
            ? string.Create(CultureInfo.InvariantCulture, $"The picture is {width:0} wide; this wall has room for a door {newRight - newLeft:0} wide, which it is now. Choose a narrower picture, or make the wall longer.")
            : null;
    }

    /// <summary>Whether a corner lies along a wall between its two ends (half a unit either way), seen square to it.</summary>
    private static bool Beside(Line wall, Vertex v)
    {
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return false;
        var along = ((v.X - wall.Start.X) * dx + (v.Y - wall.Start.Y) * dy) / length;
        return along >= -0.5 && along <= length + 0.5;
    }

    /// <summary>How far a corner lies from the line a wall runs along.</summary>
    private static double DistanceTo(Line wall, Vertex v)
    {
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length <= 0 ? 0 : Math.Abs((v.X - wall.Start.X) * dy - (v.Y - wall.Start.Y) * dx) / length;
    }

    /// <summary>The distance between two parallel walls.</summary>
    private static double Apart(Line a, Line b)
    {
        var (dx, dy) = (b.End.X - b.Start.X, b.End.Y - b.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length <= 0 ? 0 : Math.Abs((a.Start.X - b.Start.X) * dy - (a.Start.Y - b.Start.Y) * dx) / length;
    }

    [GeneratedRegex("^(BIGDOOR[0-9]|DOOR[0-9]|EXITDOOR|SPCDOOR[0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DoomDoors();

    [GeneratedRegex("^DOOR", RegexOptions.IgnoreCase)]
    private static partial Regex HereticDoors();

    [GeneratedRegex("^D_(?!END)", RegexOptions.IgnoreCase)]
    private static partial Regex HexenDoors();

    [GeneratedRegex("^(SIGLDR01$|DOR(?!TRK|TKL|TKS|(ALN|CHN|CRG|IRS|QTR|STN)0[2-8]))", RegexOptions.IgnoreCase)]
    private static partial Regex StrifeDoors();

    /// <summary>GZDoom's door actions: Door_Open (stays open), Door_Raise (opens, waits, closes), Door_LockedRaise, Door_Animated (Strife's sliding doors).</summary>
    public const int DoorOpen = 11;
    public const int DoorRaise = 12;
    public const int DoorLockedRaise = 13;
    public const int DoorAnimated = 14;

    /// <summary>Whether an action opens a door this model knows: Door_Open, Door_Raise, Door_LockedRaise or Door_Animated.</summary>
    public static bool IsDoorAction(int special) => special is DoorOpen or DoorRaise or DoorLockedRaise or DoorAnimated;

    /// <summary>The lock a door action carries (Door_LockedRaise and Door_Animated keep it fourth), or 0.</summary>
    public static int LockOf(int special, IReadOnlyList<int> args) => special is DoorLockedRaise or DoorAnimated ? args[3] : 0;

    /// <summary>
    /// A sliding door's pace: tics between its frames, Strife's own 4 (its doors take about a second to slide open) and twice
    /// as fast; and how long Strife's wait open, 3 seconds.
    /// </summary>
    public const int AnimatedSpeed = 4;
    public const int AnimatedFastSpeed = 2;
    public const int AnimatedWait = 105;

    /// <summary>The engine's door speeds (eighths of a unit per tic) and wait (tics): Doom's normal and blazing doors.</summary>
    public const int NormalSpeed = 16;
    public const int FastSpeed = 64;
    public const int Wait = 150;

    /// <summary>
    /// The door <paramref name="area"/> is, or null when it is not one. Looks only at the area's walls,
    /// found through the shapes near it and read from the map itself: quick, for the UI thread.
    /// </summary>
    public static Door? Recognise(MapGeometry geometry, Sector area)
    {
        if (!area.OnMap || geometry.WallBoundsAt(area.Slot) is not { } b) return null;
        var walls = geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (l.Front.Sector == area || l.Back?.Sector == area)).ToList();
        var openers = walls.Where(l => ReferenceEquals(l.Back?.Sector, area) && l.Args[0] == 0 && Decode(l.Special, l.Args) is not null).ToList();

        // Walls anywhere naming the area's tag with a door action open it from afar.
        if (area.Tag != 0)
        {
            var f = geometry.Frame;
            foreach (var slot in geometry.LinesNaming(area.Tag))
            {
                if (f.Lines[slot].Element is { OnMap: true } l && l.Args[0] == area.Tag && Decode(l.Special, l.Args) is not null) openers.Add(l);
            }
        }

        if (openers.Count == 0) return null;

        var settings = Decode(openers[0].Special, openers[0].Args)!;
        var lowest = double.MaxValue;
        foreach (var l in walls)
        {
            var other = ReferenceEquals(l.Front.Sector, area) ? l.Back?.Sector : l.Front.Sector;
            if (other is not null && !ReferenceEquals(other, area)) lowest = Math.Min(lowest, other.CeilingHeight);
        }

        var opensTo = lowest == double.MaxValue ? area.FloorHeight : lowest - OpenGap;
        return new Door(area, openers, settings, openers[0].Flag("monsteruse") || openers[0].Flag("monsteractivate"), opensTo);
    }

    /// <summary>How a door action with its arguments behaves; null for an action that is not a door this model edits.</summary>
    public static DoorSettings? Decode(int special, IReadOnlyList<int> args) => special switch
    {
        DoorRaise => new DoorSettings(args[1] >= FastSpeed, false, 0, args[2]),
        DoorOpen => new DoorSettings(args[1] >= FastSpeed, true, 0),
        DoorLockedRaise => new DoorSettings(args[1] >= FastSpeed, args[2] == 0, args[3], args[2] == 0 ? Wait : args[2]),
        DoorAnimated => new DoorSettings(args[1] is > 0 and < AnimatedSpeed, args[2] == 0, args[3], args[2] == 0 ? AnimatedWait : args[2]),
        _ => null,
    };

    /// <summary>
    /// The door action and arguments for <paramref name="settings"/>, keeping the light tag (a classic
    /// door's tag that makes lights follow it) and any speed that is neither the normal nor the fast one; a sliding door of
    /// Strife's (<paramref name="kind"/>) gets Door_Animated, its pace in tics a frame.
    /// </summary>
    public static (int Special, int[] Args) Encode(DoorSettings settings, int lightTag, int? speed = null, int tag = 0, DoorKind kind = DoorKind.Rises)
    {
        if (kind == DoorKind.Animated)
        {
            var pace = speed is { } frame && frame is > 0 and not AnimatedSpeed and not AnimatedFastSpeed && (frame < AnimatedSpeed) == settings.Fast ? frame : settings.Fast ? AnimatedFastSpeed : AnimatedSpeed;
            return (DoorAnimated, [tag, pace, settings.StaysOpen ? 0 : settings.Delay, settings.Lock, 0]);
        }

        var s = speed is { } given && given is not NormalSpeed and not FastSpeed && (given >= FastSpeed) == settings.Fast ? given : settings.Fast ? FastSpeed : NormalSpeed;
        if (settings.Lock != 0) return (DoorLockedRaise, [tag, s, settings.StaysOpen ? 0 : settings.Delay, settings.Lock, lightTag]);
        if (settings.StaysOpen) return (DoorOpen, [tag, s, lightTag, 0, 0]);
        return (DoorRaise, [tag, s, settings.Delay, lightTag, 0]);
    }

    /// <summary>The light tag a door action carries (where its arguments keep it).</summary>
    public static int LightTag(int special, IReadOnlyList<int> args) => special switch
    {
        DoorRaise => args[3],
        DoorOpen => args[2],
        DoorLockedRaise => args[4],
        _ => 0,
    };

    /// <summary>How deep a door made by the Door tool is, in map units.</summary>
    public const double Depth = 16;

    /// <summary>The widest wall the Door tool turns into a door.</summary>
    public const double MaxWidth = 256;

    /// <summary>The widest opening a door fills whole; a wider one gets a door of <see cref="StandardWidth"/> in its middle with wall either side.</summary>
    public const double FillsUpTo = 96;

    /// <summary>The width of the door made in the middle of a wide opening: Doom's own doors are 64 wide.</summary>
    public const double StandardWidth = 64;

    /// <summary>The stretch of a long wall a door clicked on it takes: <paramref name="width"/> wide in its middle, on whole units.</summary>
    public static (double From, double To) Centred(Line wall, double width = 64) => (Math.Round((wall.Length - width) / 2), Math.Round((wall.Length + width) / 2));

    /// <summary>How deep each frame of a doorway is: the piece either side of the door whose ceiling is the door's height.</summary>
    public const double FrameDepth = 8;

    /// <summary>How far below the lowest ceiling beside it the engine opens a door.</summary>
    private const double OpenGap = 4;

    /// <summary>
    /// Makes a door into the opening <paramref name="wall"/>, over <paramref name="span"/> of it (the whole of it when none),
    /// as Doom's own doors are made: an area <see cref="Depth"/> deep in the corridor's side (the room with walls running on
    /// from both ends of the opening), closed (its ceiling on its floor); its two faces open it when used, by players and
    /// monsters, again and again (Door_Raise, Doom's DR door), showing the game's door picture; its sides show the door track,
    /// anchored at the bottom so they do not move with the door. In a room taller than the door's picture (its height from
    /// <paramref name="heightOf"/>) the door gets a doorway: a frame <see cref="FrameDepth"/> deep either side whose ceiling
    /// is the picture's height, so the wall shows above the door — the picture hangs from the door's top edge and would
    /// repeat upward otherwise. With <paramref name="fill"/>, the rest of the opening either side of the door is closed with
    /// wall — a door in a wall, which is what a door between two rooms means — as closed slabs of the same depth; with
    /// <paramref name="door"/> false, the whole opening becomes wall and no door is made ("wall it off"). With
    /// <paramref name="flush"/> the door stands flush with the wall on the room's side, framed on the corridor's side only:
    /// a secret door, hidden as the room's wall, that still shows a doorway its picture fits from behind. Returns the door's
    /// area, or null and the reason it cannot be made there. <paramref name="before"/> must show the map as it is.
    /// </summary>
    public static (Sector? Door, string? Why) Make(StructureEdit edit, MapGeometry before, Line wall, Look look, (double From, double To)? span = null, bool fill = false, bool door = true, Func<string, double?>? heightOf = null, bool flush = false)
    {
        if (wall.Back is null) return (null, door ? "A door goes in a wall between two rooms; this wall has a room on one side only." : "This wall already has a room on one side only.");
        var (front, back) = (wall.Front.Sector, wall.Back.Sector);
        if (ReferenceEquals(front, back)) return (null, "A door goes in a wall between two rooms; this wall has the same room on both sides.");
        var (from, to) = span is { } s ? (Math.Clamp(Math.Min(s.From, s.To), 0, wall.Length), Math.Clamp(Math.Max(s.From, s.To), 0, wall.Length)) : (0, wall.Length);
        var width = to - from;
        if (width < 8) return (null, "A door needs to be at least 8 wide; drag a longer stretch of the wall.");
        if (width > MaxWidth)
        {
            return (null, span is null
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"This wall is {wall.Length:0} long, and a door can be up to {MaxWidth:0} wide: drag along the wall for a door of the width you want, or draw a short corridor between the rooms with the Room tool and click its end.")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A door can be up to {MaxWidth:0} wide, and this one would be {width:0}: drag a shorter stretch of the wall."));
        }
        if (Recognise(before, front) is not null || Recognise(before, back) is not null) return (null, "This wall is already a door's.");

        // A wall of a piece of wall — an area closed from floor to ceiling, as the wall beside a door, a walled-off opening or a
        // deleted door is — takes the door through the piece, from this side's room to the room beyond.
        if (door && !flush && front.FloorHeight >= front.CeilingHeight != back.FloorHeight >= back.CeilingHeight)
        {
            var (piece, near) = front.FloorHeight >= front.CeilingHeight ? (front, back) : (back, front);
            return Through(edit, before, wall, piece, near, look, from, to, heightOf);
        }

        // The door goes into the corridor: the room with walls running on from both ends of this one.
        var sides = new[] { back, front }.OrderByDescending(r => Corridor(before, wall, r)).ToList();
        var texture = width <= 64 ? look.Door : look.BigDoor;
        var height = door ? heightOf?.Invoke(texture) : null;
        foreach (var into in sides)
        {
            // A doorway as tall as the door's picture when the room is taller; when there is no room for its frames, the door alone.
            // A door's way is only as tall as its picture: where the floor beyond is a step higher and the player would not fit
            // under the opened door from there, the doorway stands on that floor, stepped up into from the lower side.
            var beyond = ReferenceEquals(into, front) ? back : front;
            var rise = beyond.FloorHeight - into.FloorHeight;
            var floor = height is { } tall && rise > 0 && rise <= Scene.Walker.StepHeight && tall - OpenGap - rise < Scene.Walker.PlayerHeight ? beyond.FloorHeight : into.FloorHeight;
            var framed = height is { } h && h > 0 && h < into.CeilingHeight - floor;
            foreach (var withFrames in framed ? new[] { true, false } : [false])
            {
                var depth = withFrames ? Depth + (flush ? 1 : 2) * FrameDepth : Depth;

                // Wall either side of the door: the whole opening is carved as one strip, cut where the door starts and ends.
                var whole = !door || (fill && (from > 0 || to < wall.Length));
                var strip = Strip(wall, into, depth, whole ? 0 : from, whole ? wall.Length : to);
                if (!Clear(before, wall, strip)) continue;
                var (nx, ny) = Normal(wall, into);
                var (dx, dy) = ((wall.End.X - wall.Start.X) / wall.Length, (wall.End.Y - wall.Start.Y) / wall.Length);
                MapPoint At(double along, double inward = 0) => new(wall.Start.X + dx * along + nx * inward, wall.Start.Y + dy * along + ny * inward);
                var cuts = new List<(MapPoint A, MapPoint B)>();
                if (whole && door)
                {
                    if (from > 0) cuts.Add((At(from), At(from, depth)));
                    if (to < wall.Length) cuts.Add((At(to), At(to, depth)));
                }

                // The frames' walls across the door column, after the cuts along its sides so that they end on them (a flush
                // door's one frame behind it).
                if (withFrames)
                {
                    if (!flush) cuts.Add((At(from, FrameDepth), At(to, FrameDepth)));
                    var behind = flush ? Depth : FrameDepth + Depth;
                    cuts.Add((At(from, behind), At(to, behind)));
                }

                var made = ShapeDrawing.Draw(edit, before, strip, look, cuts.Count > 0 ? cuts : null);
                if (made.Count != 1 + cuts.Count) throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(door ? "The door's area" : "The wall's area")} did not come out as one area: {made.Count} areas for {cuts.Count} cuts."));

                // Which areas are the door column: the ones across the span's middle; in it, by depth, the near frame, the door
                // and the far frame. The rest are wall: closed at the room's ceiling (floor up on the ceiling), never on the floor,
                // because the engine opens a door to the lowest ceiling beside it, and a slab closed on the floor would keep it shut.
                // The door is closed on its floor; the frames are as tall as the door.
                var touched = edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct().Where(l => l.OnMap).ToList();
                IEnumerable<Vertex> Corners(Sector area) => touched.Where(l => ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area)).SelectMany(l => new[] { l.Start, l.End });
                double Along(Sector area) => Corners(area).Select(v => (v.X - wall.Start.X) * dx + (v.Y - wall.Start.Y) * dy).DefaultIfEmpty(double.NaN).Average();
                double Inward(Sector area) => Corners(area).Select(v => (v.X - wall.Start.X) * nx + (v.Y - wall.Start.Y) * ny).DefaultIfEmpty(double.NaN).Average();
                var column = door ? made.OrderBy(area => Math.Abs(Along(area) - (from + to) / 2)).Take(withFrames ? flush ? 2 : 3 : 1).OrderBy(Inward).ToList() : [];
                var doorArea = door ? column[withFrames && !flush ? 1 : 0] : null;
                var frames = new HashSet<Sector>(ReferenceEqualityComparer.Instance);
                if (withFrames) frames.UnionWith(flush ? [column[1]] : [column[0], column[2]]);
                // The door and its frames are solid: in an open-air passage they take the floor's picture as their ceiling,
                // not the sky, which would show through them (Look.SolidUnder).
                foreach (var area in made)
                {
                    if (frames.Contains(area) || ReferenceEquals(area, doorArea)) area.CeilingTexture = Look.SolidUnder(area.CeilingTexture, area.FloorTexture);
                    if (withFrames && (frames.Contains(area) || ReferenceEquals(area, doorArea))) area.FloorHeight = floor;
                    if (frames.Contains(area)) area.CeilingHeight = area.FloorHeight + height!.Value;
                    else if (ReferenceEquals(area, doorArea)) area.CeilingHeight = area.FloorHeight;
                    else area.FloorHeight = area.CeilingHeight;
                }

                foreach (var slab in made)
                {
                    var isDoor = ReferenceEquals(slab, doorArea);
                    var isFrame = frames.Contains(slab);
                    foreach (var l in touched.Where(l => ReferenceEquals(l.Front.Sector, slab) || ReferenceEquals(l.Back?.Sector, slab)).ToList())
                    {
                        var other = ReferenceEquals(l.Front.Sector, slab) ? l.Back?.Sector : l.Front.Sector;
                        var mine = ReferenceEquals(l.Front.Sector, slab) ? l.Front : l.Back!;
                        if (other is not null && made.Contains(other))
                        {
                            // Between two of the new areas: the door's face against a frame; the door's side against a piece of wall
                            // (whose floor is up on the ceiling) shows the track below it; a frame's side against one shows the room's
                            // wall, hung from the wall's top so it lines up with the walls beside.
                            if (isDoor && frames.Contains(other)) DoorFace(edit, l, slab, texture);
                            else if (isDoor) edit.Set(mine, x => x.Bottom, (x, v) => x.Bottom = v, look.Track);
                            else if (isFrame && !ReferenceEquals(other, doorArea)) edit.Set(mine, x => x.Bottom, (x, v) => x.Bottom = v, look.Wall);
                            continue;
                        }

                        if (!isDoor)
                        {
                            if (other is null)
                            {
                                // A frame's side in the corridor's own wall: its texture hung from the floor, in line with the wall beside.
                                if (isFrame) edit.Flag(l, "dontpegbottom", true);
                                continue;
                            }

                            // A piece of wall, or a frame, against a room: the room's wall texture on the wall's faces (a lower, up to
                            // the slab's floor on the ceiling) and above the door, hung from the ceiling like the walls beside it.
                            if (!Parallel(l, wall)) continue;
                            var face = ReferenceEquals(l.Front.Sector, slab) ? l.Back! : l.Front;
                            edit.Set(face, x => x.Top, (x, v) => x.Top = v, look.Wall);
                            edit.Set(face, x => x.Bottom, (x, v) => x.Bottom = v, look.Wall);
                            edit.Flag(l, "dontpegtop", true);
                            continue;
                        }

                        if (other is not null && Parallel(l, wall))
                        {
                            DoorFace(edit, l, slab, texture);
                        }
                        else if (other is null)
                        {
                            // A side of the door: the track, which stays put while the door moves.
                            edit.Set(l.Front, x => x.Middle, (x, v) => x.Middle = v, look.Track);
                            edit.Flag(l, "dontpegbottom", true);
                        }
                    }
                }

                return (doorArea, null);
            }
        }

        return (null, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"There is not room for {(door ? "a door" : "a wall")} {Depth:0} deep on either side of this wall."));
    }

    /// <summary>A face of a door: the room (or the frame) in front, the door behind, so using it opens the door (Door_Raise, used again and again by players and monsters), with the door's picture on it.</summary>
    private static void DoorFace(StructureEdit edit, Line l, Sector doorSector, string texture)
    {
        if (ReferenceEquals(l.Front.Sector, doorSector)) Walls.Turn(edit, l);
        edit.Set(l.Front, x => x.Top, (x, v) => x.Top = v, texture);
        edit.Set(l.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, 0);
        edit.Set(l.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, 0);
        var (special, args) = Encode(new DoorSettings(false, false, 0), 0);
        edit.Set(l, x => x.Special, (x, v) => x.Special = v, special);
        for (var i = 0; i < args.Length; i++)
        {
            var k = i;
            edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
        }

        edit.Flag(l, "playeruse", true);
        edit.Flag(l, "monsteruse", true);
        edit.Flag(l, "repeatspecial", true);
    }

    /// <summary>
    /// <see cref="Make"/>'s door through a piece of wall (<paramref name="piece"/>, closed from floor to ceiling between
    /// <paramref name="near"/> and a room beyond): the door's column runs through the piece from <paramref name="wall"/> to the
    /// piece's far wall over the stretch <paramref name="from"/>–<paramref name="to"/>, as deep as the piece is; the piece either
    /// side stays wall. The door stands on the rooms' floor (the higher one's when the player would not fit under the opened door
    /// from a step up), framed either side when the piece is deep enough for frames and the rooms are taller than its picture.
    /// The faces above the doorway keep the wall's own texture toward each room.
    /// </summary>
    private static (Sector? Door, string? Why) Through(StructureEdit edit, MapGeometry before, Line wall, Sector piece, Sector near, Look look, double from, double to, Func<string, double?>? heightOf)
    {
        static string? Shown(string texture) => texture is "-" or "" ? null : texture;
        var (nx, ny) = Normal(wall, piece);
        var (dx, dy) = ((wall.End.X - wall.Start.X) / wall.Length, (wall.End.Y - wall.Start.Y) / wall.Length);
        double AlongOf(Vertex v) => (v.X - wall.Start.X) * dx + (v.Y - wall.Start.Y) * dy;
        double InwardOf(Vertex v) => (v.X - wall.Start.X) * nx + (v.Y - wall.Start.Y) * ny;
        MapPoint At(double along, double inward = 0) => new(wall.Start.X + dx * along + nx * inward, wall.Start.Y + dy * along + ny * inward);

        // The piece's far wall over the stretch: its walls in line with this one on the far side, with one room beyond them all.
        if (before.WallBoundsAt(piece.Slot) is not { } b) return (null, "This wall's piece of wall could not be found.");
        Sector? far = null;
        Side? farFace = null;
        double? depth = null;
        var covered = new List<(double From, double To)>();
        foreach (var l in before.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            if (!l.OnMap || ReferenceEquals(l, wall) || l.Back is null || !Parallel(l, wall)) continue;
            var other = ReferenceEquals(l.Front.Sector, piece) ? l.Back.Sector : ReferenceEquals(l.Back.Sector, piece) ? l.Front.Sector : null;
            var d = InwardOf(l.Start);
            if (other is null || ReferenceEquals(other, piece) || other.FloorHeight >= other.CeilingHeight || d <= 0.5) continue;
            var (a, c) = (Math.Min(AlongOf(l.Start), AlongOf(l.End)), Math.Max(AlongOf(l.Start), AlongOf(l.End)));
            if (c <= from + 0.5 || a >= to - 0.5) continue;
            if ((depth is { } known && Math.Abs(known - d) > 0.5) || (far is not null && !ReferenceEquals(far, other)))
            {
                return (null, "This wall is not the same thickness, with the same room beyond it, all along where the door would go: drag a stretch of it where it is.");
            }

            (depth, far, farFace) = (d, other, ReferenceEquals(l.Front.Sector, other) ? l.Front : l.Back);
            covered.Add((a, c));
        }

        var reached = from;
        foreach (var (a, c) in covered.OrderBy(x => x.From))
        {
            if (a > reached + 0.5) break;
            reached = Math.Max(reached, c);
        }

        if (far is null || depth is not { } deep || farFace is null || reached < to - 0.5) return (null, "There is no room beyond this wall all along where the door would go: a door goes through a wall from one room into another.");

        // The door stands on the lower room's floor, or on the higher one's when the player would not fit under it from there.
        var texture = to - from <= 64 ? look.Door : look.BigDoor;
        var height = heightOf?.Invoke(texture);
        var (low, high) = near.FloorHeight <= far.FloorHeight ? (near, far) : (far, near);
        var rise = high.FloorHeight - low.FloorHeight;
        var floor = height is { } tall && rise > 0 && rise <= Scene.Walker.StepHeight && tall - OpenGap - rise < Scene.Walker.PlayerHeight ? high.FloorHeight : low.FloorHeight;
        var framed = height is { } h && h > 0 && h < Math.Min(near.CeilingHeight, far.CeilingHeight) - floor && deep >= Depth + FrameDepth;
        var frameDepth = framed ? (deep - Depth) / 2 : 0;
        var strip = Strip(wall, piece, deep, from, to);
        if (!Clear(before, wall, strip)) return (null, "Something stands inside this wall where the door would go.");
        var cuts = framed ? new List<(MapPoint A, MapPoint B)> { (At(from, frameDepth), At(to, frameDepth)), (At(from, frameDepth + Depth), At(to, frameDepth + Depth)) } : null;

        // The textures the wall shows each room, for the wall above the doorway.
        var nearFace = ReferenceEquals(wall.Front.Sector, near) ? wall.Front : wall.Back!;
        var nearWall = Shown(nearFace.Bottom) ?? Shown(nearFace.Top) ?? look.Wall;
        var farWall = Shown(farFace.Bottom) ?? Shown(farFace.Top) ?? look.Wall;

        var made = ShapeDrawing.Draw(edit, before, strip, look, cuts);
        if (made.Count != 1 + (cuts?.Count ?? 0)) throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The door's area did not come out as one area: {made.Count} areas for {cuts?.Count ?? 0} cuts."));

        // Which area is which: by depth, the near frame, the door and the far frame (the door alone without frames).
        var touched = edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct().Where(l => l.OnMap).ToList();
        double Inward(Sector area) => touched.Where(l => ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area)).SelectMany(l => new[] { l.Start, l.End }).Select(InwardOf).DefaultIfEmpty(double.NaN).Average();
        var column = made.OrderBy(Inward).ToList();
        var doorArea = column[framed ? 1 : 0];
        var frames = new HashSet<Sector>(ReferenceEqualityComparer.Instance);
        if (framed) frames.UnionWith([column[0], column[2]]);
        foreach (var area in made)
        {
            area.CeilingTexture = Look.SolidUnder(area.CeilingTexture, area.FloorTexture);
            area.FloorHeight = floor;
            area.CeilingHeight = frames.Contains(area) ? floor + height!.Value : floor;
        }

        foreach (var area in made)
        {
            var isDoor = ReferenceEquals(area, doorArea);
            foreach (var l in touched.Where(l => ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area)).ToList())
            {
                var other = ReferenceEquals(l.Front.Sector, area) ? l.Back?.Sector : l.Front.Sector;
                var mine = ReferenceEquals(l.Front.Sector, area) ? l.Front : l.Back!;
                if (other is null)
                {
                    // Where the piece ends at the door: the door's side shows the track, which stays put while the door moves.
                    if (isDoor) edit.Set(mine, x => x.Middle, (x, v) => x.Middle = v, look.Track);
                    edit.Flag(l, "dontpegbottom", true);
                }
                else if (isDoor && (frames.Contains(other) || ReferenceEquals(other, near) || ReferenceEquals(other, far)))
                {
                    DoorFace(edit, l, area, texture);
                }
                else if (ReferenceEquals(other, piece))
                {
                    // The doorway's sides against the wall either side: the track beside the door, the room's wall beside a frame.
                    edit.Set(mine, x => x.Bottom, (x, v) => x.Bottom = v, isDoor ? look.Track : look.Wall);
                }
                else if (ReferenceEquals(other, near) || ReferenceEquals(other, far))
                {
                    // A frame's face: the wall above the doorway, toward the room, shows that wall's own texture, hung from the ceiling.
                    var face = ReferenceEquals(l.Front.Sector, area) ? l.Back! : l.Front;
                    var shown = ReferenceEquals(other, near) ? nearWall : farWall;
                    edit.Set(face, x => x.Top, (x, v) => x.Top = v, shown);
                    edit.Set(face, x => x.Bottom, (x, v) => x.Bottom = v, shown);
                    edit.Flag(l, "dontpegtop", true);
                }
            }
        }

        return (doorArea, null);
    }

    /// <summary>Whether the room has a wall running on from each end of this one, into its side (a corridor's end).</summary>
    private static bool Corridor(MapGeometry before, Line wall, Sector room)
    {
        var (nx, ny) = Normal(wall, room);
        bool RunsOn(Vertex at) => before.LinesIn(at.X, at.Y, at.X, at.Y).Any(l => !ReferenceEquals(l, wall) && (l.Start == at || l.End == at) && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back?.Sector, room)) && Along(l, at, nx, ny));
        return RunsOn(wall.Start) && RunsOn(wall.End);
    }

    /// <summary>Whether a wall leaves <paramref name="at"/> heading along (nx, ny), within a few degrees.</summary>
    private static bool Along(Line l, Vertex at, double nx, double ny)
    {
        var other = l.Start == at ? l.End : l.Start;
        var (dx, dy) = (other.X - at.X, other.Y - at.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length > 0 && (dx * nx + dy * ny) / length > 0.99;
    }

    /// <summary>The unit vector across the wall pointing into the room's side.</summary>
    private static (double X, double Y) Normal(Line wall, Sector room)
    {
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        var (rx, ry) = (dy / length, -dx / length);
        return ReferenceEquals(wall.Front.Sector, room) ? (rx, ry) : (-rx, -ry);
    }

    private static MapPoint[] Strip(Line wall, Sector into, double depth, double from, double to)
    {
        var (nx, ny) = Normal(wall, into);
        var (dx, dy) = ((wall.End.X - wall.Start.X) / wall.Length, (wall.End.Y - wall.Start.Y) / wall.Length);
        var (a, b) = (new MapPoint(wall.Start.X + dx * from, wall.Start.Y + dy * from), new MapPoint(wall.Start.X + dx * to, wall.Start.Y + dy * to));
        return [a, b, new MapPoint(b.X + nx * depth, b.Y + ny * depth), new MapPoint(a.X + nx * depth, a.Y + ny * depth)];
    }

    /// <summary>Whether nothing but the wall itself and walls along the strip's sides lies inside the strip.</summary>
    internal static bool Clear(MapGeometry before, Line wall, MapPoint[] strip)
    {
        var (x0, y0, x1, y1) = (strip.Min(p => p.X), strip.Min(p => p.Y), strip.Max(p => p.X), strip.Max(p => p.Y));
        foreach (var l in before.LinesIn(x0, y0, x1, y1))
        {
            if (ReferenceEquals(l, wall)) continue;
            var (a, b) = (new MapPoint(l.Start.X, l.Start.Y), new MapPoint(l.End.X, l.End.Y));

            // A wall is in the way if any of it lies strictly inside the strip.
            for (var t = 0.0; t <= 1.0; t += 1.0 / 16)
            {
                if (StrictlyInside(strip, new MapPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t))) return false;
            }
        }

        foreach (var v in before.VerticesIn(x0, y0, x1, y1))
        {
            if (StrictlyInside(strip, new MapPoint(v.X, v.Y))) return false;
        }

        return true;
    }

    /// <summary>Inside the strip's rectangle by more than a hair on every side.</summary>
    internal static bool StrictlyInside(MapPoint[] r, MapPoint p)
    {
        const double Hair = 1e-6;
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (r[i], r[(i + 1) % 4]);
            var cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var length = a.DistanceTo(b);
            if (Math.Abs(cross) / length <= Hair) return false;
        }

        // On the same side of all four edges: inside.
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (r[i], r[(i + 1) % 4]);
            var s = Math.Sign((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X));
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }

        return true;
    }

    internal static bool Parallel(Line l, Line wall)
    {
        var (ax, ay) = (l.End.X - l.Start.X, l.End.Y - l.Start.Y);
        var (bx, by) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        // Within about a tenth of a degree: corners projected onto slanted walls leave hair-width bends that are still one wall.
        return Math.Abs(ax * by - ay * bx) <= 0.002 * Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
    }

    /// <summary>
    /// Makes a door open from one side only (a shortcut back to where the player has been, D59): its own walls on
    /// <paramref name="shut"/>'s side lose their action, as in every Doom engine a wall is used from its front alone. Walls
    /// elsewhere that open it by its tag keep theirs.
    /// </summary>
    public static void OneWay(StructureEdit edit, Door door, MapPoint shut)
    {
        var own = door.Own.ToList();
        if (own.Count == 0) return;
        var (cx, cy) = (own.Average(l => (l.Start.X + l.End.X) / 2), own.Average(l => (l.Start.Y + l.End.Y) / 2));
        foreach (var l in own)
        {
            var (mx, my) = ((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
            if ((mx - cx) * (shut.X - cx) + (my - cy) * (shut.Y - cy) <= 0) continue;
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, 0);
            for (var i = 0; i < 5; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, 0);
            }

            edit.Flag(l, "playeruse", false);
            edit.Flag(l, "monsteruse", false);
        }
    }

    /// <summary>
    /// Changes how a door behaves through <paramref name="edit"/>: every wall that opens it gets the
    /// same action, and whether monsters open it; a door that starts open has its ceiling raised to
    /// where it opens to, one that starts closed has it down on its floor.
    /// </summary>
    public static void Change(StructureEdit edit, Door door, DoorSettings settings, bool monsters, bool startsOpen)
    {
        foreach (var l in door.Openers)
        {
            var (special, args) = Encode(settings, LightTag(l.Special, l.Args), l.Args[1], l.Args[0], door.Kind);
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, special);
            for (var i = 0; i < args.Length; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
            }

            edit.Flag(l, "monsteruse", monsters);
        }

        // A sliding door of Strife's rises by its picture's height from where it stands: it starts closed.
        var ceiling = startsOpen && door.Kind == DoorKind.Rises ? Math.Max(door.OpensTo, door.Area.FloorHeight) : door.Area.FloorHeight;
        edit.Set(door.Area, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, ceiling);
    }

    /// <summary>
    /// Makes a door open another way (D62) through <paramref name="edit"/>: every wall that opens it gets the action of
    /// <paramref name="kind"/> with the same behaviour, its lock kept, and it starts closed. A sliding door of Strife's plays its
    /// picture's frames, so it gets <paramref name="picture"/> — one of the game's sliding door pictures, its size giving the
    /// doorway's (see <see cref="Dress"/>). Returns why the doorway is not the picture's width, or null.
    /// </summary>
    public static string? SetKind(StructureEdit edit, MapGeometry geometry, Door door, DoorKind kind, string? picture = null, (int Width, int Height)? size = null)
    {
        foreach (var l in door.Openers)
        {
            var (special, args) = Encode(door.Settings, kind == DoorKind.Rises ? LightTag(l.Special, l.Args) : 0, null, l.Args[0], kind);
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, special);
            for (var i = 0; i < args.Length; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
            }
        }

        edit.Set(door.Area, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, door.Area.FloorHeight);
        return picture is null ? null : Dress(edit, geometry, door, picture, size);
    }
}
