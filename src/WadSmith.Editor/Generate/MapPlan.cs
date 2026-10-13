namespace WadSmith.Editor.Generate;

/// <summary>What a room is for on the way through the map.</summary>
public enum RoomRole
{
    Start,
    Path,
    Side,
    Key,
    Secret,
    Arena,
    Exit,

    /// <summary>A monster closet beside a room, opened when the player walks in.</summary>
    Closet,
}

/// <summary>How two rooms are joined: an open corridor, a door, a locked door, stairs, or a lift.</summary>
public enum LinkKind
{
    Open,
    Door,
    Locked,
    Stairs,
    Lift,
}

/// <summary>A rectangle on the map, west/south/east/north, in map units.</summary>
public readonly record struct Box(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;

    public int Height => Y1 - Y0;

    public double CentreX => (X0 + X1) / 2.0;

    public double CentreY => (Y0 + Y1) / 2.0;

    public bool Contains(double x, double y) => x > X0 && x < X1 && y > Y0 && y < Y1;
}

/// <summary>A planned room: its cell on the lattice, its rectangle, heights, look, light, and what it is for.</summary>
public sealed class PlannedRoom
{
    public required int Index { get; init; }

    public required (int I, int J) Cell { get; init; }

    public required RoomRole Role { get; set; }

    /// <summary>How many locked doors lie between the start and this room.</summary>
    public required int Zone { get; init; }

    /// <summary>How far along the way to the exit it is, 0 to 1.</summary>
    public double Progress { get; set; }

    public Box Box { get; set; }

    public double Floor { get; set; }

    public double Ceiling { get; set; }

    public int Light { get; set; } = 192;

    public LookChoice? Look { get; set; }

    public bool Outdoors { get; set; }

    /// <summary>What the room is like (by its part of the map when the style is mixed).</summary>
    public MapStyle Style { get; set; }

    /// <summary>How much is cut off each corner (south-west, north-west, north-east, south-east): a cave's rough outline.</summary>
    public (int SW, int NW, int NE, int SE) Cuts { get; set; }

    /// <summary>Solid columns standing in the room (a castle hall's).</summary>
    public List<Box> Pillars { get; } = [];

    /// <summary>For a closet: the room it opens into, where on their shared wall its door is, and the line in the room's entrance that opens it.</summary>
    public int ClosetOf { get; set; } = -1;

    public (double X, double Y) ClosetDoor { get; set; }

    public (double X, double Y) Trigger { get; set; }

    /// <summary>The room's outline, corner by corner, clockwise as the Room tool draws (its rectangle with its corners cut).</summary>
    public IReadOnlyList<(int X, int Y)> Outline()
    {
        var (b, c) = (Box, Cuts);
        if (c == default) return [(b.X0, b.Y0), (b.X0, b.Y1), (b.X1, b.Y1), (b.X1, b.Y0)];
        List<(int, int)> points = [];
        void Add(int x, int y)
        {
            if (points.Count == 0 || points[^1] != (x, y)) points.Add((x, y));
        }

        Add(b.X0, b.Y0 + c.SW);
        Add(b.X0, b.Y1 - c.NW);
        Add(b.X0 + c.NW, b.Y1);
        Add(b.X1 - c.NE, b.Y1);
        Add(b.X1, b.Y1 - c.NE);
        Add(b.X1, b.Y0 + c.SE);
        Add(b.X1 - c.SE, b.Y0);
        Add(b.X0 + c.SW, b.Y0);
        if (points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        return points;
    }

    /// <summary>Whether a circle of <paramref name="radius"/> at the point stands inside the room, clear of its walls, cut corners and pillars.</summary>
    public bool Holds(double x, double y, double radius)
    {
        var b = Box;
        if (x - radius <= b.X0 || x + radius >= b.X1 || y - radius <= b.Y0 || y + radius >= b.Y1) return false;
        var slack = radius * Math.Sqrt(2);
        var c = Cuts;
        if (x - b.X0 + (y - b.Y0) < c.SW + slack || x - b.X0 + (b.Y1 - y) < c.NW + slack || b.X1 - x + (b.Y1 - y) < c.NE + slack || b.X1 - x + (y - b.Y0) < c.SE + slack) return false;
        return Pillars.All(p => x + radius <= p.X0 || x - radius >= p.X1 || y + radius <= p.Y0 || y - radius >= p.Y1);
    }

    /// <summary>The key a key room holds.</summary>
    public KeyKind? Key { get; set; }

    /// <summary>A dim room with a light in it.</summary>
    public bool Dark { get; set; }

    /// <summary>The name the map shows for it: "Entry hall", "Red key room", "Arena".</summary>
    public string Name { get; set; } = "";
}

/// <summary>A corridor between two rooms on neighbouring cells: its rectangle, heights, kind, and the key colour of a locked door.</summary>
public sealed class PlannedLink
{
    public required int A { get; init; }

    public required int B { get; init; }

    public required Box Box { get; set; }

    /// <summary>Whether the corridor runs east–west (else north–south).</summary>
    public required bool AlongX { get; init; }

    public LinkKind Kind { get; set; }

    public KeyKind? Key { get; set; }

    public double Floor { get; set; }

    public double Ceiling { get; set; }

    /// <summary>Whether this link closes a loop rather than leading somewhere new.</summary>
    public bool Loop { get; set; }

    /// <summary>The room at the door's end of the corridor (the door is set where the corridor meets it).</summary>
    public int DoorAt { get; set; }
}

/// <summary>What a planned object is.</summary>
public enum ThingRole
{
    Start,
    Monster,
    Boss,
    Weapon,
    Ammo,
    Health,
    Armor,
    Key,
    Decoration,
    Light,
}

/// <summary>
/// A planned object: what it is, where, which way it faces, the lowest difficulty it appears on (0 every difficulty, 1 from
/// the normal one, 2 the hard ones only), whether it waits for the player, and for a monster which one (its number may
/// come from another game, given when the map is made).
/// </summary>
public sealed record PlannedThing(ThingRole Role, int Number, double X, double Y, int Angle, int FromSkill = 0, bool Ambush = false, MonsterKind? Monster = null, int Room = -1, string? Family = null);

/// <summary>A teleporter: the pad's rectangle in a room and where it sends the player.</summary>
public sealed record PlannedTeleport(int Room, Box Pad, int ToRoom, double ToX, double ToY);

/// <summary>
/// The generator's whole plan for a map (D50): rooms, corridors, objects, teleporters, the exit's wall, and the facts the page
/// shows. Pure data from the settings and the seed; <see cref="MapMaker"/> turns it into an edit script.
/// </summary>
public sealed class MapPlan
{
    public required GeneratorSettings Settings { get; init; }

    /// <summary>The lattice's cell: one room to a cell (castle halls need bigger ones).</summary>
    public int Cell => Settings.Style is MapStyle.Castle or MapStyle.Mixed ? 1280 : 1024;

    /// <summary>The most a room reaches from its cell's middle: the cell less the band kept clear at its edge.</summary>
    public int MaxHalf => Cell / 2 - 64;

    public List<PlannedRoom> Rooms { get; } = [];

    public List<PlannedLink> Links { get; } = [];

    public List<PlannedThing> Things { get; } = [];

    public List<PlannedTeleport> Teleports { get; } = [];

    /// <summary>The rooms of the way from the start to the exit, in order.</summary>
    public List<int> Path { get; } = [];

    /// <summary>Where on the exit room's wall the exit switch goes.</summary>
    public (double X, double Y) Exit { get; set; }

    /// <summary>How many monsters each difficulty has: easy, normal, hard.</summary>
    public (int Easy, int Normal, int Hard) MonsterCounts
    {
        get
        {
            var m = Things.Where(t => t.Role is ThingRole.Monster or ThingRole.Boss).ToList();
            return (m.Count(t => t.FromSkill == 0), m.Count(t => t.FromSkill <= 1), m.Count);
        }
    }

    /// <summary>About how many minutes it takes to play: the way's length at a walk, plus time for each monster.</summary>
    public int Minutes
    {
        get
        {
            var walk = Path.Zip(Path.Skip(1)).Sum(p => Math.Abs(Rooms[p.First].Box.CentreX - Rooms[p.Second].Box.CentreX) + Math.Abs(Rooms[p.First].Box.CentreY - Rooms[p.Second].Box.CentreY));
            var monsters = Things.Count(t => t.Role is ThingRole.Monster && t.FromSkill <= 1) + 20 * Things.Count(t => t.Role == ThingRole.Boss);
            return Math.Max(1, (int)Math.Round(walk * 2.2 / 300 / 60 + monsters * 4.0 / 60 + Rooms.Count * 0.15));
        }
    }
}
