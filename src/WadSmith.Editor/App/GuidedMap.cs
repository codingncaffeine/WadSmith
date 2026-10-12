using WadSmith.Core.Games;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.App;

/// <summary>The control a step of the guided first map points at, outlined while the step is open.</summary>
public enum GuideTarget
{
    None,
    RoomTool,
    ThreeD,
    DoorTool,
    Textures,
    Objects,
    Inspector,
    Play,
}

/// <summary>
/// One step of the guided first map: a title, what to do (with {key:…}, {or:…} and {label:…} filled in by the editor,
/// and {monster}/{weapon} by the game), and the control it points at.
/// </summary>
public sealed record GuideStep(string Id, string Title, string Text, GuideTarget Target);

/// <summary>What the map holds that the guide's steps look for, read from a frame.</summary>
public sealed record GuideFacts(int Rooms, bool Door, int WallsRetextured, int Monsters, int Weapons, bool Exit);

/// <summary>
/// The guided first map (§3.9, D33): seven steps from one room to Play, each finished by what is in the map (or by
/// the 3D view opened alone, or Play started), never by a button. The facts are read from an immutable frame, so a
/// worker reads them.
/// </summary>
public static class GuidedMap
{
    public static readonly IReadOnlyList<GuideStep> Steps =
    [
        new("room", "Draw a room", "Pick {label:Room} at the top left{or:Room tool} and drag a rectangle against the room you have. Drawn against it, the two rooms share a wall.", GuideTarget.RoomTool),
        new("look", "See it in 3D", "Press {key:Show the 3D view alone} to fill the window with the 3D view, and drag to turn round your rooms. {key:Show the 3D view alone} again brings the plan back.", GuideTarget.ThreeD),
        new("door", "Put a door between them", "Pick Door{or:Door tool} and click the wall your two rooms share. It becomes a door that opens when the player uses it.", GuideTarget.DoorTool),
        new("texture", "Give a wall a new look", "Open Textures on the right and drag a picture onto one of your walls, on the plan or in the 3D view.", GuideTarget.Textures),
        new("things", "Add a monster and a weapon", "Open {label:Objects} on the right and drag {monster} and {weapon} onto the floor of a room.", GuideTarget.Objects),
        new("exit", "Add a way out", "Click a wall on the plan, then press “Make it the exit switch” in the Inspector. Using it in the game ends the map.", GuideTarget.Inspector),
        new("play", "Play it", "Press {label:Play} at the top ({key:Play}). WadSmith checks the map first and offers to fix anything that would spoil it.", GuideTarget.Play),
    ];

    /// <summary>A monster and a weapon of the game, as the step names them.</summary>
    public static (string Monster, string Weapon) Examples(string engine) => engine switch
    {
        "heretic" => ("a Gargoyle", "the Ethereal Crossbow"),
        "hexen" => ("an Ettin", "a weapon piece"),
        "strife" => ("an Acolyte", "the assault gun"),
        "chex" => ("a Flemoid", "the Large Zorcher"),
        _ => ("an Imp", "a shotgun"),
    };

    /// <summary>Each wall face's three textures, to tell later whether one was changed.</summary>
    public static IReadOnlyDictionary<Side, (string Top, string Middle, string Bottom)> Walls(MapFrame f)
    {
        var walls = new Dictionary<Side, (string, string, string)>(ReferenceEqualityComparer.Instance);
        foreach (var s in f.Sides)
        {
            if (s.Element is { } side) walls[side] = (s.Top, s.Middle, s.Bottom);
        }

        return walls;
    }

    /// <summary>What the map holds now; walls retextured counts the faces in <paramref name="wallsBefore"/> whose textures changed.</summary>
    public static GuideFacts Facts(MapFrame f, GameResources? resources, IReadOnlyDictionary<Side, (string Top, string Middle, string Bottom)> wallsBefore)
    {
        var rooms = f.Sectors.Count(s => s.Element is not null);
        var door = f.Lines.Any(l => l.Element is not null && l.Special is Smart.Doors.DoorOpen or Smart.Doors.DoorRaise or Smart.Doors.DoorLockedRaise);
        var exit = f.Lines.Any(l => l.Element is not null && l.Special is Smart.Exits.Normal or Smart.Exits.Secret);
        var retextured = 0;
        foreach (var s in f.Sides)
        {
            if (s.Element is { } side && wallsBefore.TryGetValue(side, out var before) && before != (s.Top, s.Middle, s.Bottom)) retextured++;
        }

        int monsters = 0, weapons = 0;
        foreach (var t in f.Things)
        {
            if (t.Element is null) continue;
            switch (resources?.Things.Find(t.Type)?.Category)
            {
                case ThingCategory.Monster:
                    monsters++;
                    break;
                case ThingCategory.Weapon:
                    weapons++;
                    break;
            }
        }

        return new GuideFacts(rooms, door, retextured, monsters, weapons, exit);
    }

    /// <summary>
    /// Whether a step is done: what the map holds now against what it held when the step began, and whether the 3D view
    /// was opened alone or Play started while it was open.
    /// </summary>
    public static bool Done(GuideStep step, GuideFacts now, GuideFacts atStart, bool saw3D, bool played) => step.Id switch
    {
        "room" => now.Rooms > atStart.Rooms,
        "look" => saw3D,
        "door" => now.Door,
        "texture" => now.WallsRetextured > 0,
        "things" => now.Monsters > atStart.Monsters && now.Weapons > atStart.Weapons,
        "exit" => now.Exit,
        "play" => played,
        _ => false,
    };
}
