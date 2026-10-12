using WadSmith.Editor.Editing;
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
    /// Makes a wall an exit through <paramref name="edit"/>: a solid wall becomes the exit switch (the
    /// game's exit switch texture, used to end the map); a wall between rooms ends the map when walked
    /// across. <paramref name="secret"/> goes on to the secret map instead of the next one.
    /// </summary>
    public static void Make(StructureEdit edit, Line wall, bool secret, Look look)
    {
        edit.Set(wall, l => l.Special, (l, v) => l.Special = v, secret ? Secret : Normal);
        for (var i = 0; i < 5; i++)
        {
            var k = i;
            edit.Set(wall, l => l.Args[k], (l, v) => l.Args[k] = v, 0);
        }

        if (wall.Back is null)
        {
            edit.Flag(wall, "playeruse", true);
            edit.Set(wall.Front, s => s.Middle, (s, v) => s.Middle = v, look.ExitSwitch);
            edit.Set(wall.Front, s => s.OffsetX, (s, v) => s.OffsetX = v, 0);
            edit.Set(wall.Front, s => s.OffsetY, (s, v) => s.OffsetY = v, 0);
        }
        else
        {
            edit.Flag(wall, "playercross", true);
        }
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
