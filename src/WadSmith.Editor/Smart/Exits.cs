using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// Exits: walls that end the map when the player uses them (an exit switch), walks across them or
/// shoots them, going on to the next map or the secret one (Exit_Normal, Exit_Secret).
/// </summary>
public static class Exits
{
    public const int Normal = 243;
    public const int Secret = 244;

    public static bool IsExit(Line l) => l.Special is Normal or Secret;

    /// <summary>How the exit is set off, in words: "uses it", "walks across it", "shoots it".</summary>
    public static string How(Line l) => l.Flag("playeruse") ? "uses it" : l.Flag("playercross") ? "walks across it" : l.Flag("impact") ? "shoots it" : "sets it off";

    /// <summary>
    /// Makes a wall an exit through <paramref name="edit"/> and returns the exit: on a solid wall, a piece as wide as
    /// the switch picture (see <see cref="Switches.Piece"/>) round <paramref name="at"/>, the wall's middle when null,
    /// becomes the exit switch (the game's exit switch texture, used to end the map) and the rest stays the wall it was,
    /// a short wall becoming the switch whole; a wall between rooms ends the map when walked across.
    /// <paramref name="secret"/> goes on to the secret map instead of the next one.
    /// </summary>
    public static Line Make(StructureEdit edit, Line wall, bool secret, Look look, MapPoint? at = null)
    {
        var exit = wall.Back is null ? Switches.Piece(edit, wall, at) : wall;
        edit.Set(exit, l => l.Special, (l, v) => l.Special = v, secret ? Secret : Normal);
        for (var i = 0; i < 5; i++)
        {
            var k = i;
            edit.Set(exit, l => l.Args[k], (l, v) => l.Args[k] = v, 0);
        }

        if (exit.Back is null)
        {
            edit.Flag(exit, "playeruse", true);
            edit.Set(exit.Front, s => s.Middle, (s, v) => s.Middle = v, look.ExitSwitch);
            edit.Set(exit.Front, s => s.OffsetX, (s, v) => s.OffsetX = v, 0);
            edit.Set(exit.Front, s => s.OffsetY, (s, v) => s.OffsetY = v, 0);
        }
        else
        {
            edit.Flag(exit, "playercross", true);
        }

        return exit;
    }

    /// <summary>Makes an exit go to the next map or the secret one.</summary>
    public static void GoTo(StructureEdit edit, Line exit, bool secret) => edit.Set(exit, l => l.Special, (l, v) => l.Special = v, secret ? Secret : Normal);

    /// <summary>Takes the exit away: the wall keeps its look and does nothing when used or crossed.</summary>
    public static void Clear(StructureEdit edit, Line exit)
    {
        edit.Set(exit, l => l.Special, (l, v) => l.Special = v, 0);
        foreach (var f in new[] { "playeruse", "playercross", "impact", "repeatspecial" }) edit.Flag(exit, f, false);
    }
}
