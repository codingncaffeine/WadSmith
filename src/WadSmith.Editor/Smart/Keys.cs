using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>A key of a game family: its colour (or name, for Hexen's), the objects that are it, and the lock a door takes for it.</summary>
public sealed record KeyKindOf(string Colour, IReadOnlyList<int> Things, int Lock);

/// <summary>
/// The games' keys and the doors they open, per game family as the engine's LOCKDEFS has them: Doom's keycard and skull key
/// of each colour (locks 129–131 take either, as the Door tool's locks do; 1–6 that one key), Heretic's green, blue and
/// yellow keys (locks 1–3 and 129–131), and Hexen's eleven named keys (locks 1–11). Strife opens its doors with quest items,
/// which are not keys here.
/// </summary>
public static class Keys
{
    /// <summary>Doom's colours, each with its keycard and skull key (editor numbers), and the lock that takes either.</summary>
    public static readonly IReadOnlyList<(string Colour, int Card, int Skull, int Lock)> Doom = [("red", 13, 38, 129), ("blue", 5, 40, 130), ("yellow", 6, 39, 131)];

    private static readonly IReadOnlyList<KeyKindOf> DoomKeys = [.. Doom.Select(k => new KeyKindOf(k.Colour, [k.Card, k.Skull], k.Lock))];

    private static readonly IReadOnlyList<KeyKindOf> HereticKeys = [new("green", [73], 129), new("blue", [79], 130), new("yellow", [80], 131)];

    private static readonly IReadOnlyList<KeyKindOf> HexenKeys =
    [
        new("steel", [8030], 1), new("cave", [8031], 2), new("axe", [8032], 3), new("fire", [8033], 4), new("emerald", [8034], 5), new("dungeon", [8035], 6),
        new("silver", [8036], 7), new("rusted", [8037], 8), new("horn", [8038], 9), new("swamp", [8039], 10), new("castle", [8200], 11),
    ];

    /// <summary>The keys of a game family ("doom", "chex", "heretic", "hexen", "strife").</summary>
    public static IReadOnlyList<KeyKindOf> Of(string family) => family switch
    {
        "heretic" => HereticKeys,
        "hexen" => HexenKeys,
        "strife" => [],
        _ => DoomKeys,
    };

    /// <summary>The colour of a key object in a game family, or null.</summary>
    public static string? ColourOf(int type, string family = "doom") => Of(family).FirstOrDefault(k => k.Things.Contains(type))?.Colour;

    /// <summary>The key objects a lock takes in a game family (empty for no lock or a lock that is not a key's).</summary>
    public static IReadOnlyList<int> Opening(int lockNumber, string family = "doom") => family switch
    {
        "heretic" => lockNumber switch { 1 or 129 => [73], 2 or 130 => [79], 3 or 131 => [80], _ => [] },
        "hexen" => HexenKeys.FirstOrDefault(k => k.Lock == lockNumber)?.Things ?? [],
        "strife" => [],
        _ => lockNumber switch
        {
            1 => [13], 2 => [5], 3 => [6], 4 => [38], 5 => [40], 6 => [39],
            129 => [13, 38], 130 => [5, 40], 131 => [6, 39],
            _ => [],
        },
    };

    /// <summary>The colour a lock needs in a game family, or null.</summary>
    public static string? ColourOfLock(int lockNumber, string family = "doom") => Opening(lockNumber, family) is [var first, ..] ? ColourOf(first, family) : null;

    /// <summary>The lock a door action carries (Door_LockedRaise keeps it fourth), or 0.</summary>
    public static int LockOf(Line wall) => wall.Special == Doors.DoorLockedRaise ? wall.Args[3] : 0;

    /// <summary>The walls whose door action a key of <paramref name="type"/> opens.</summary>
    public static IReadOnlyList<Line> DoorsOpenedBy(MapGeometry geometry, int type, string family = "doom") =>
        [.. geometry.Frame.Lines.Where(l => l.Element is { OnMap: true } && l.Special == Doors.DoorLockedRaise && Opening(l.Args[3], family).Contains(type)).Select(l => l.Element!)];

    /// <summary>The key objects of a colour on the map.</summary>
    public static IReadOnlyList<Thing> OfColour(MapGeometry geometry, string colour, string family = "doom")
    {
        var things = Of(family).FirstOrDefault(k => k.Colour == colour)?.Things ?? [];
        return [.. geometry.Frame.Things.Where(t => t.Element is { OnMap: true } && things.Contains(t.Type)).Select(t => t.Element!)];
    }
}
