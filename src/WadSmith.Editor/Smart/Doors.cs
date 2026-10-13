using System.Text.RegularExpressions;
using WadSmith.Core.Graphics;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

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
    /// and 64 tall (tracks and key trims are narrow), by name.
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
    /// Gives a door the picture <paramref name="texture"/> on its faces through <paramref name="edit"/>, hung from the door's
    /// top edge as before, and its doorway's frames the picture's height (<paramref name="size"/>) when it has them, so the
    /// wall shows above a short door and a tall one fills the doorway.
    /// </summary>
    public static void Dress(StructureEdit edit, MapGeometry geometry, Door door, string texture, (int Width, int Height)? size)
    {
        foreach (var l in door.Own)
        {
            edit.Set(l.Front, x => x.Top, (x, v) => x.Top = v, texture);
            edit.Set(l.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, 0);
            edit.Set(l.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, 0);
        }

        if (size is not { Height: > 0 } s) return;
        foreach (var (frame, room) in FramesOf(geometry, door))
        {
            var ceiling = Math.Min(room.CeilingHeight, frame.FloorHeight + s.Height);
            edit.Set(frame, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, ceiling);
        }
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

    [GeneratedRegex("^D_", RegexOptions.IgnoreCase)]
    private static partial Regex HexenDoors();

    [GeneratedRegex("^DOR", RegexOptions.IgnoreCase)]
    private static partial Regex StrifeDoors();

    /// <summary>GZDoom's door actions: Door_Open (stays open), Door_Raise (opens, waits, closes), Door_LockedRaise.</summary>
    public const int DoorOpen = 11;
    public const int DoorRaise = 12;
    public const int DoorLockedRaise = 13;

    /// <summary>The engine's door speeds (eighths of a unit per tic) and wait (tics): Doom's normal and blazing doors.</summary>
    public const int NormalSpeed = 16;
    public const int FastSpeed = 64;
    public const int Wait = 150;

    /// <summary>Doom's key locks as Doom's own doors use them: the card or the skull of that colour.</summary>
    public static readonly IReadOnlyList<(int Lock, string Name)> DoomLocks = [(0, "Nothing"), (129, "Red key"), (130, "Blue key"), (131, "Yellow key")];

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

        var opensTo = lowest == double.MaxValue ? area.FloorHeight : lowest - 4;
        return new Door(area, openers, settings, openers[0].Flag("monsteruse") || openers[0].Flag("monsteractivate"), opensTo);
    }

    /// <summary>How a door action with its arguments behaves; null for an action that is not a door this model edits.</summary>
    public static DoorSettings? Decode(int special, IReadOnlyList<int> args) => special switch
    {
        DoorRaise => new DoorSettings(args[1] >= FastSpeed, false, 0, args[2]),
        DoorOpen => new DoorSettings(args[1] >= FastSpeed, true, 0),
        DoorLockedRaise => new DoorSettings(args[1] >= FastSpeed, args[2] == 0, args[3], args[2] == 0 ? Wait : args[2]),
        _ => null,
    };

    /// <summary>
    /// The door action and arguments for <paramref name="settings"/>, keeping the light tag (a classic
    /// door's tag that makes lights follow it) and any speed that is neither the normal nor the fast one.
    /// </summary>
    public static (int Special, int[] Args) Encode(DoorSettings settings, int lightTag, int? speed = null, int tag = 0)
    {
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
    /// <paramref name="door"/> false, the whole opening becomes wall and no door is made ("wall it off"). Returns the door's
    /// area, or null and the reason it cannot be made there. <paramref name="before"/> must show the map as it is.
    /// </summary>
    public static (Sector? Door, string? Why) Make(StructureEdit edit, MapGeometry before, Line wall, Look look, (double From, double To)? span = null, bool fill = false, bool door = true, Func<string, double?>? heightOf = null)
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

        // The door goes into the corridor: the room with walls running on from both ends of this one.
        var sides = new[] { back, front }.OrderByDescending(r => Corridor(before, wall, r)).ToList();
        var texture = width <= 64 ? look.Door : look.BigDoor;
        var height = door ? heightOf?.Invoke(texture) : null;
        foreach (var into in sides)
        {
            // A doorway as tall as the door's picture when the room is taller; when there is no room for its frames, the door alone.
            var framed = height is { } h && h > 0 && h < into.CeilingHeight - into.FloorHeight;
            foreach (var withFrames in framed ? new[] { true, false } : [false])
            {
                var depth = withFrames ? Depth + 2 * FrameDepth : Depth;

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

                // The frames' walls across the door column, after the cuts along its sides so that they end on them.
                if (withFrames)
                {
                    cuts.Add((At(from, FrameDepth), At(to, FrameDepth)));
                    cuts.Add((At(from, FrameDepth + Depth), At(to, FrameDepth + Depth)));
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
                var column = door ? made.OrderBy(area => Math.Abs(Along(area) - (from + to) / 2)).Take(withFrames ? 3 : 1).OrderBy(Inward).ToList() : [];
                var doorArea = door ? column[withFrames ? 1 : 0] : null;
                var frames = withFrames ? new HashSet<Sector>(ReferenceEqualityComparer.Instance) { column[0], column[2] } : new HashSet<Sector>(ReferenceEqualityComparer.Instance);
                foreach (var area in made)
                {
                    if (frames.Contains(area)) area.CeilingHeight = area.FloorHeight + height!.Value;
                    else if (ReferenceEquals(area, doorArea)) area.CeilingHeight = area.FloorHeight;
                    else area.FloorHeight = area.CeilingHeight;
                }

                void DoorFace(Line l, Sector doorSector)
                {
                    // A face of the door: the room (or the frame) in front, the door behind, so using it opens the door.
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
                            if (isDoor && frames.Contains(other)) DoorFace(l, slab);
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
                            DoorFace(l, slab);
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
            var (special, args) = Encode(settings, LightTag(l.Special, l.Args), l.Args[1], l.Args[0]);
            edit.Set(l, x => x.Special, (x, v) => x.Special = v, special);
            for (var i = 0; i < args.Length; i++)
            {
                var k = i;
                edit.Set(l, x => x.Args[k], (x, v) => x.Args[k] = v, args[k]);
            }

            edit.Flag(l, "monsteruse", monsters);
        }

        var ceiling = startsOpen ? Math.Max(door.OpensTo, door.Area.FloorHeight) : door.Area.FloorHeight;
        edit.Set(door.Area, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, ceiling);
    }
}
