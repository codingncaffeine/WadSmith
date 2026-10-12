using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// Doom's keys and the doors they open: a keycard and a skull key of each colour, and the locks on doors
/// (129–131: either key of a colour, as the Door tool's locks are; 1–6: that one key).
/// </summary>
public static class Keys
{
    /// <summary>The colours, each with its keycard and skull key (editor numbers), and the lock that takes either.</summary>
    public static readonly IReadOnlyList<(string Colour, int Card, int Skull, int Lock)> Doom = [("red", 13, 38, 129), ("blue", 5, 40, 130), ("yellow", 6, 39, 131)];

    /// <summary>The colour of a key object, or null.</summary>
    public static string? ColourOf(int type) => Doom.FirstOrDefault(k => k.Card == type || k.Skull == type).Colour;

    /// <summary>The key objects a lock takes (empty for no lock or a lock that is not Doom's).</summary>
    public static IReadOnlyList<int> Opening(int lockNumber) => lockNumber switch
    {
        1 => [13], 2 => [5], 3 => [6], 4 => [38], 5 => [40], 6 => [39],
        129 => [13, 38], 130 => [5, 40], 131 => [6, 39],
        _ => [],
    };

    /// <summary>The colour a lock needs, or null.</summary>
    public static string? ColourOfLock(int lockNumber) => Opening(lockNumber) is [var first, ..] ? ColourOf(first) : null;

    /// <summary>The lock a door action carries (Door_LockedRaise keeps it fourth), or 0.</summary>
    public static int LockOf(Line wall) => wall.Special == Doors.DoorLockedRaise ? wall.Args[3] : 0;

    /// <summary>The walls whose door action a key of <paramref name="type"/> opens.</summary>
    public static IReadOnlyList<Line> DoorsOpenedBy(MapGeometry geometry, int type) =>
        [.. geometry.Frame.Lines.Where(l => l.Element is { OnMap: true } && l.Special == Doors.DoorLockedRaise && Opening(l.Args[3]).Contains(type)).Select(l => l.Element!)];

    /// <summary>The key objects of a colour on the map.</summary>
    public static IReadOnlyList<Thing> OfColour(MapGeometry geometry, string colour)
    {
        var (_, card, skull, _) = Doom.First(k => k.Colour == colour);
        return [.. geometry.Frame.Things.Where(t => t.Element is { OnMap: true } && (t.Type == card || t.Type == skull)).Select(t => t.Element!)];
    }
}
