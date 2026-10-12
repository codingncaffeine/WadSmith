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
/// <param name="Openers">The walls that open it when used: they face into the door from the rooms beside it.</param>
/// <param name="Settings">How it behaves.</param>
/// <param name="Monsters">Whether monsters open it too.</param>
/// <param name="OpensTo">The height its ceiling rises to: 4 below the lowest ceiling beside it (the engine's rule).</param>
public sealed record Door(Sector Area, IReadOnlyList<Line> Openers, DoorSettings Settings, bool Monsters, double OpensTo)
{
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
    public static (int Special, int[] Args) Encode(DoorSettings settings, int lightTag, int? speed = null)
    {
        var s = speed is { } given && given is not NormalSpeed and not FastSpeed && (given >= FastSpeed) == settings.Fast ? given : settings.Fast ? FastSpeed : NormalSpeed;
        if (settings.Lock != 0) return (DoorLockedRaise, [0, s, settings.StaysOpen ? 0 : settings.Delay, settings.Lock, lightTag]);
        if (settings.StaysOpen) return (DoorOpen, [0, s, lightTag, 0, 0]);
        return (DoorRaise, [0, s, settings.Delay, lightTag, 0]);
    }

    /// <summary>The light tag a door action carries (where its arguments keep it).</summary>
    public static int LightTag(int special, IReadOnlyList<int> args) => special switch
    {
        DoorRaise => args[3],
        DoorOpen => args[2],
        DoorLockedRaise => args[4],
        _ => 0,
    };

    /// <summary>
    /// Changes how a door behaves through <paramref name="edit"/>: every wall that opens it gets the
    /// same action, and whether monsters open it; a door that starts open has its ceiling raised to
    /// where it opens to, one that starts closed has it down on its floor.
    /// </summary>
    public static void Change(StructureEdit edit, Door door, DoorSettings settings, bool monsters, bool startsOpen)
    {
        foreach (var l in door.Openers)
        {
            var (special, args) = Encode(settings, LightTag(l.Special, l.Args), l.Args[1]);
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
