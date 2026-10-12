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
    public IEnumerable<string> RemoteWays()
    {
        static string Count(int n, string one, string many) => n == 1 ? one : $"{n} {many}";
        var remote = Remote.ToList();
        var used = remote.Count(l => l.Flag("playeruse"));
        var crossed = remote.Count(l => !l.Flag("playeruse") && (l.Flag("playercross") || l.Flag("monstercross")));
        var shot = remote.Count(l => !l.Flag("playeruse") && !l.Flag("playercross") && l.Flag("impact"));
        var other = remote.Count - used - crossed - shot;
        if (used > 0) yield return Count(used, "a switch", "switches");
        if (crossed > 0) yield return Count(crossed, "a line walked over", "lines walked over");
        if (shot > 0) yield return Count(shot, "a wall shot", "walls shot");
        if (other > 0) yield return Count(other, "another wall", "other walls");
    }

    /// <summary>Whether the door is open as the map starts (its ceiling above its floor).</summary>
    public bool StartsOpen => Area.CeilingHeight > Area.FloorHeight;
}

/// <summary>
/// Doors: recognising them (a classic local door is an area whose walls, facing into it from beside,
/// open it with a door action naming no tag: used from either room, the engine opens the area behind
/// the wall) and changing how they behave.
/// </summary>
public static class Doors
{
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
        return new Door(area, openers, settings, openers[0].Flag("monsteruse"), opensTo);
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

    /// <summary>
    /// Makes a door in a wall between two rooms through <paramref name="edit"/>, as Doom's own doors are
    /// made: an area 16 deep and as wide as the wall, flush with it on the side of the narrower room (a
    /// corridor's end), closed (its ceiling on its floor); its two walls facing the rooms open it when
    /// used, by players and monsters, again and again (Door_Raise, Doom's DR door); they show the game's
    /// door texture, and its side walls the door track texture, anchored at the bottom so they do not
    /// move with the door. Returns the door's area, or null and the reason it cannot be made there.
    /// <paramref name="before"/> must show the map as it is.
    /// </summary>
    public static (Sector? Door, string? Why) Make(StructureEdit edit, MapGeometry before, Line wall, Look look)
    {
        if (wall.Back is null) return (null, "A door goes in a wall between two rooms; this wall has a room on one side only.");
        var (front, back) = (wall.Front.Sector, wall.Back.Sector);
        if (ReferenceEquals(front, back)) return (null, "A door goes in a wall between two rooms; this wall has the same room on both sides.");
        if (wall.Length > MaxWidth) return (null, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"This wall is {wall.Length:0} long, and a door fills the wall it is made in, up to {MaxWidth:0}. Draw a short corridor between the rooms with the Room tool and click its end."));
        if (Recognise(before, front) is not null || Recognise(before, back) is not null) return (null, "This wall is already a door's.");

        // The door goes into the corridor: the room with walls running on from both ends of this one.
        var sides = new[] { back, front }.OrderByDescending(r => Corridor(before, wall, r)).ToList();
        foreach (var into in sides)
        {
            var strip = Strip(wall, into, Depth);
            if (!Clear(before, wall, strip)) continue;
            var made = ShapeDrawing.Draw(edit, before, strip, look);
            if (made.Count != 1) throw new InvalidOperationException("The door's area did not come out as one area.");
            var door = made[0];
            door.CeilingHeight = door.FloorHeight;
            var texture = wall.Length <= 64 ? look.Door : look.BigDoor;
            // The door's walls: the ones the drawing made or split, and the ones whose face it gave to the door.
            var walls = edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct();
            foreach (var l in walls.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, door) || ReferenceEquals(l.Back?.Sector, door))).ToList())
            {
                var other = ReferenceEquals(l.Front.Sector, door) ? l.Back?.Sector : l.Front.Sector;
                if (other is not null && Parallel(l, wall))
                {
                    // A face of the door: the room in front, the door behind, so using it opens the door.
                    if (ReferenceEquals(l.Front.Sector, door)) Walls.Turn(edit, l);
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
                else if (other is null)
                {
                    // A side of the door: the track, which stays put while the door moves.
                    edit.Set(l.Front, x => x.Middle, (x, v) => x.Middle = v, look.Track);
                    edit.Flag(l, "dontpegbottom", true);
                }
            }

            return (door, null);
        }

        return (null, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"There is not room for a door {Depth:0} deep on either side of this wall."));
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

    private static MapPoint[] Strip(Line wall, Sector into, double depth)
    {
        var (nx, ny) = Normal(wall, into);
        var (a, b) = (new MapPoint(wall.Start.X, wall.Start.Y), new MapPoint(wall.End.X, wall.End.Y));
        return [a, b, new MapPoint(b.X + nx * depth, b.Y + ny * depth), new MapPoint(a.X + nx * depth, a.Y + ny * depth)];
    }

    /// <summary>Whether nothing but the wall itself and walls along the strip's sides lies inside the strip.</summary>
    private static bool Clear(MapGeometry before, Line wall, MapPoint[] strip)
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
    private static bool StrictlyInside(MapPoint[] r, MapPoint p)
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

    private static bool Parallel(Line l, Line wall)
    {
        var (ax, ay) = (l.End.X - l.Start.X, l.End.Y - l.Start.Y);
        var (bx, by) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        return Math.Abs(ax * by - ay * bx) <= 1e-6 * Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
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
