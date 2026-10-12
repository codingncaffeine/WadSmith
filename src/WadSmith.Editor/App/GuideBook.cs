using WadSmith.Core.Games;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.App;

/// <summary>The control a step of the Guide points at, outlined while the step is open.</summary>
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
    StairsTool,
    LightTool,
    Heights,
    Walk,
    Preferences,
}

/// <summary>
/// One step of a Guide chapter: a title, what to do (with {key:…}, {or:…}, {label:…} and {move} filled in by the editor,
/// and {monster}/{weapon} by the game), and the control it points at.
/// </summary>
public sealed record GuideStep(string Id, string Title, string Text, GuideTarget Target);

/// <summary>
/// A chapter of the Guide (§3.9): a few hands-on steps on the user's own map, then what to try next on it, and the kind of
/// landmark (D28) its "See one" opens in a real map.
/// </summary>
public sealed record GuideChapter(string Id, string Title, string Summary, IReadOnlyList<GuideStep> Steps, string TryIt, string? Landmark = null);

/// <summary>
/// Things that happen outside the map which some steps wait for; the editor reports them, and a step sees those reported
/// since it began.
/// </summary>
public static class GuideEvents
{
    public const string ThreeD = "3d";
    public const string Played = "played";
    public const string Heights = "heights";
    public const string Walk = "walk";
    public const string Difficulty = "difficulty";
    public const string MapName = "map-name";
    public const string MapMusic = "map-music";
    public const string ClassicNames = "classic-names";
    public const string BuilderKeys = "builder-keys";
    public const string Palette = "palette";
}

/// <summary>
/// The Guide (§3.9, D33): the guided first map and the chapters after it. Every step is finished by what the map holds
/// now against what it held when the step began, or by something the user did in the editor, never by a button. The
/// checks read immutable frames, so a worker runs them.
/// </summary>
public static class GuideBook
{
    /// <summary>The guided first map's id; the start screen's tile and the first-map card open it.</summary>
    public const string FirstMap = "first";

    public static readonly IReadOnlyList<GuideChapter> Chapters =
    [
        new(FirstMap, "Guided first map", "Ten minutes from one room to playing your own map.",
        [
            new("room", "Draw a room", "Pick {label:Room} at the top left{or:Room tool} and drag a rectangle against the room you have (or click corner by corner and double-click the last corner to finish). Where the two rooms touch they open into each other: in Doom there is no wall on that line (it is dashed on the plan), and it is where your door will go.", GuideTarget.RoomTool),
            new("look", "See it in 3D", "Press {key:Show the 3D view alone} to fill the window with the 3D view, and drag to turn round your rooms: where they meet there is no wall yet, they open into each other. {key:Show the 3D view alone} again brings the plan back.", GuideTarget.ThreeD),
            new("door", "Put a door between them", "Pick Door{or:Door tool} and click the dashed line where your two rooms meet. A door is built into that opening, with wall either side of it: it opens when the player uses it and closes again behind them.", GuideTarget.DoorTool),
            new("texture", "Give a wall a new look", "Open Textures on the right and drag a picture onto one of your walls, on the plan or in the 3D view.", GuideTarget.Textures),
            new("things", "Add a monster and a weapon", "Open {label:Objects} on the right and drag {monster} and {weapon} onto the floor of a room.", GuideTarget.Objects),
            new("exit", "Add a way out", "Click a wall on the plan, then press “Make it the exit switch” in the Inspector. Using it in the game ends the map.", GuideTarget.Inspector),
            new("play", "Play it", "Press {label:Play} at the top ({key:Play}). WadSmith checks the map first and offers to fix anything that would spoil it.", GuideTarget.Play),
        ],
        "Everything in it can be changed, and nothing needs saving. Each chapter below takes a few minutes on this map; Ctrl+K finds anything else.", "door"),

        new("rooms", "Rooms and heights", "Draw rooms, then raise and lower their floors and ceilings.",
        [
            new("room", "Draw a room", "Pick {label:Room}{or:Room tool} and drag a rectangle beside or across a room you have. Rooms drawn against each other open into each other where they meet (the dashed line): a door or a window goes there.", GuideTarget.RoomTool),
            new("floor", "Raise a floor", "Click a room and change {label:Floor height} in the Inspector. In the 3D view, hold Ctrl and scroll over a floor to move it 8 at a time.", GuideTarget.Inspector),
            new("ceiling", "Lower a ceiling", "Change {label:Ceiling height} the same way, or hold Ctrl and scroll over a ceiling in the 3D view. The player needs 56 between floor and ceiling to fit.", GuideTarget.Inspector),
            new("heights", "See every height at once", "Press Heights at the top. Each room takes the colour of its floor, blue for low and red for high, so steps and pits stand out.", GuideTarget.Heights),
        ],
        "Try it on your map: sink a pool a little below the floor, raise a stage at one end of a hall, or give a big room a high ceiling. The player climbs a step of 24 or less without help.", "stairs"),

        new("doors", "Doors, keys and switches", "Make doors, lock them with keys and open them from switches.",
        [
            new("door", "Make a door", "Pick {label:Door}{or:Door tool} and click the dashed line where two rooms meet. A door is built into that opening; it opens when the player uses it.", GuideTarget.DoorTool),
            new("lock", "Lock it", "Click the door and choose a key under {label:Locked with} in the Inspector. The game tells the player which key it needs.", GuideTarget.Inspector),
            new("key", "Leave the key somewhere", "Open {label:Objects}, find the key of that colour and drag it into a room the player reaches without going through the door.", GuideTarget.Objects),
            new("switch", "Open a door from a switch", "Click a door, press “Add a switch that opens it” in the Inspector, then click a solid wall: part of it becomes the switch.", GuideTarget.Inspector),
        ],
        "Try it on your map: a red door the player sees early and opens late, or a switch across a room that opens a door behind the player.", "locked door"),

        new("lifts", "Lifts and stairs", "Join floors of different heights with lifts and stairs.",
        [
            new("lift", "Make a lift", "Click a room whose floor is higher than the room beside it and press “Make it a lift” in the Inspector. Using its wall from below brings it down.", GuideTarget.Inspector),
            new("stairs", "Draw stairs", "Pick {label:Stairs}{or:Stairs tool} and drag from a low floor up to a higher one. WadSmith cuts steps the player can climb.", GuideTarget.StairsTool),
            new("walk", "Walk up them", "Choose Walk over the 3D view and use {move} to climb your stairs as the player does.", GuideTarget.Walk),
        ],
        "Try it on your map: a lift up to a ledge with something worth having on it, or stairs down into a dark basement.", "lift"),

        new("textures", "Textures and alignment", "Change how walls, floors and ceilings look, and line their textures up.",
        [
            new("texture", "Retexture a wall", "Open Textures and drag a picture onto a wall, on the plan or in the 3D view.", GuideTarget.Textures),
            new("flat", "Retexture a floor", "Drag a floor picture from Textures onto a room on the plan, or onto a floor or ceiling in the 3D view.", GuideTarget.Textures),
            new("align", "Line a texture up", "In the 3D view, hold Ctrl and drag a wall's texture to slide it, or hold Ctrl and press an arrow to nudge it. Ctrl+A lines up the walls along it.", GuideTarget.ThreeD),
        ],
        "Try it on your map: give one room a look of its own, frame a door in a different trim, and run the bricks round a corner with Ctrl+A."),

        new("things", "Monsters, items and difficulty", "Place monsters and pickups, and choose which difficulties they appear on.",
        [
            new("monster", "Add a monster", "Open {label:Objects} and drag {monster} onto a floor.", GuideTarget.Objects),
            new("pickup", "Leave something to pick up", "Drag health, ammunition or armour from {label:Objects} onto a floor.", GuideTarget.Objects),
            new("skill", "Keep it off the easy setting", "Click a monster and switch off {label:Appears on Easy} in the Inspector. Players on the easiest settings will not meet it.", GuideTarget.Inspector),
            new("difficulty", "Choose the difficulty to play", "Press the arrow beside {label:Play} and pick a difficulty. Play uses it from then on.", GuideTarget.Play),
        ],
        "Try it on your map: more monsters on Hard than on Easy, and health after every big fight.", "key"),

        new("lights", "Lights and atmosphere", "Dim rooms, add lights and colour the air.",
        [
            new("brightness", "Dim a room", "Click a room and lower {label:Brightness} in the Inspector. A dark room makes the light in the next one count.", GuideTarget.Inspector),
            new("light", "Add a light", "Pick {label:Light}{or:Light tool} and click in a room. The 3D view shows its glow; its colour and reach are in the Inspector.", GuideTarget.LightTool),
            new("colour", "Colour a room", "Click a room and choose a {label:Light colour} or a {label:Fog colour} in the Inspector.", GuideTarget.Inspector),
        ],
        "Try it on your map: a red room before a fight, a flickering light down a corridor, green fog over a pool."),

        new("secrets", "Secrets", "Hide a room the player can find, and count it.",
        [
            new("secret", "Make a secret", "Draw a small room off another one, click it and press “Make it a secret” in the Inspector. Finding it counts toward the secrets at the end of the map.", GuideTarget.Inspector),
            new("hide", "Hide its door", "Put a door into the secret{or:Door tool}, click the secret room and press “Make its door look like the wall”.", GuideTarget.Inspector),
        ],
        "Try it on your map: a secret behind a wall that looks a little different, with a reward worth the search.", "secret"),

        new("settings", "Map settings and music", "Name your map and choose what plays.",
        [
            new("name", "Name your map", "Click outside the map so nothing is selected: the Inspector shows the map's settings. Type a {label:Name}.", GuideTarget.Inspector),
            new("music", "Choose its music", "Pick a track under {label:Music}.", GuideTarget.Inspector),
            new("play", "Hear it", "Press {label:Play} ({key:Play}). The game shows your name on the automap and plays your music.", GuideTarget.Play),
        ],
        "Try it on your map: choose the sky under Sky and the map that follows under After the exit.", "exit"),

        new("veterans", "For veterans: classic names and keys", "The names and keys of the classic editors, on the same editor.",
        [
            new("names", "Classic names", "Open {label:Preferences}{or:Preferences} and switch on Show classic names: rooms become sectors, walls linedefs, objects things.", GuideTarget.Preferences),
            new("keys", "Doom Builder keys", "In the same place, switch on Doom Builder keys. Tooltips and the command palette show the keys in use.", GuideTarget.Preferences),
            new("palette", "Find any command", "Press Ctrl+K, type a few letters of what you want, such as “sector” or “test”, and press Enter.", GuideTarget.None),
        ],
        "Nothing else changes: every tool, check and shortcut is the same editor underneath, and Preferences switches back at any time."),
    ];

    /// <summary>The chapter with this id, or the first map.</summary>
    public static GuideChapter Chapter(string? id) => Chapters.FirstOrDefault(c => c.Id == id) ?? Chapters[0];

    /// <summary>The chapters the first map's card offers next.</summary>
    public static IReadOnlyList<GuideChapter> AfterFirstMap => [.. Chapters.Where(c => c.Id is "rooms" or "doors" or "lifts")];

    /// <summary>The chapter after <paramref name="chapter"/> in the book, or null after the last.</summary>
    public static GuideChapter? After(GuideChapter chapter)
    {
        var at = Chapters.ToList().IndexOf(chapter);
        return at >= 0 && at + 1 < Chapters.Count ? Chapters[at + 1] : null;
    }

    /// <summary>A monster and a weapon of the game, as the steps name them.</summary>
    public static (string Monster, string Weapon) Examples(string engine) => engine switch
    {
        "heretic" => ("a Gargoyle", "the Ethereal Crossbow"),
        "hexen" => ("an Ettin", "a weapon piece"),
        "strife" => ("an Acolyte", "the assault gun"),
        "chex" => ("a Flemoid", "the Large Zorcher"),
        _ => ("an Imp", "a shotgun"),
    };

    /// <summary>
    /// Whether a step is done: the map <paramref name="now"/> against the map when the step began, and what
    /// <paramref name="happened"/> in the editor since then.
    /// </summary>
    public static bool Done(GuideStep step, MapFrame now, MapFrame atStart, GameResources? resources, IReadOnlySet<string> happened)
    {
        int Up(Func<MapFrame, int> count) => count(now) - count(atStart);
        int Things(MapFrame f, Func<ThingCategory?, int, bool> which) => f.Things.Count(t => t.Element is not null && which(resources?.Things.Find(t.Type)?.Category, t.Type));
        bool Kind(ThingCategory? c, params ThingCategory[] kinds) => c is { } k && kinds.Contains(k);
        return step.Id switch
        {
            "room" => Up(Rooms) > 0 && Up(SharedWalls) > 0,
            "look" => happened.Contains(GuideEvents.ThreeD),
            "door" => Up(f => Lines(f, l => IsDoor(l.Special) && l.Args[0] == 0)) > 0,
            "texture" => Changed(atStart.Sides, now.Sides, s => s.Element, s => (s.Top, s.Middle, s.Bottom)),
            "things" => Up(f => Things(f, (c, _) => c == ThingCategory.Monster)) > 0 && Up(f => Things(f, (c, _) => c == ThingCategory.Weapon)) > 0,
            "exit" => Up(f => Lines(f, l => l.Special is Smart.Exits.Normal or Smart.Exits.Secret)) > 0,
            "play" => happened.Contains(GuideEvents.Played),
            "floor" => Changed(atStart.Sectors, now.Sectors, s => s.Element, s => s.FloorHeight),
            "ceiling" => Changed(atStart.Sectors, now.Sectors, s => s.Element, s => s.CeilingHeight),
            "heights" => happened.Contains(GuideEvents.Heights),
            "lock" => Up(f => Lines(f, l => l.Special == Smart.Doors.DoorLockedRaise && l.Args[3] != 0)) > 0,
            "key" => Up(f => Things(f, (c, _) => c == ThingCategory.Key)) > 0,
            "switch" => Up(f => Lines(f, l => IsDoor(l.Special) && l.Args[0] != 0)) > 0,
            "lift" => Up(f => Lines(f, l => l.Special is Smart.Lifts.DownWaitUpStay or Smart.Lifts.DownWaitUpStayLip)) > 0,
            "stairs" => Flight(atStart, now),
            "walk" => happened.Contains(GuideEvents.Walk),
            "flat" => Changed(atStart.Sectors, now.Sectors, s => s.Element, s => (s.FloorTexture, s.CeilingTexture)),
            "align" => Changed(atStart.Sides, now.Sides, s => s.Element, s => (s.OffsetX, s.OffsetY, Offsets(s.Extra))),
            "monster" => Up(f => Things(f, (c, _) => c == ThingCategory.Monster)) > 0,
            "pickup" => Up(f => Things(f, (c, _) => Kind(c, ThingCategory.Health, ThingCategory.Ammo, ThingCategory.Armor))) > 0,
            "skill" => Changed(atStart.Things, now.Things, t => t.Element, t => Skills(t.Extra)),
            "difficulty" => happened.Contains(GuideEvents.Difficulty),
            "brightness" => Changed(atStart.Sectors, now.Sectors, s => s.Element, s => s.Light),
            "light" => Up(f => Things(f, (_, type) => Smart.Lights.Kind(type) is not null)) > 0,
            "colour" => Changed(atStart.Sectors, now.Sectors, s => s.Element, s => (Field(s.Extra, "lightcolor"), Field(s.Extra, "fadecolor"))),
            "secret" => Up(f => f.Sectors.Count(s => s.Element is not null && ((s.Special & Smart.Secrets.Flag) != 0 || Field(s.Extra, "secret") is { Kind: UdmfValueKind.Boolean } v && v.AsBool()))) > 0,
            "hide" => DoorFaceRetextured(atStart, now),
            "name" => happened.Contains(GuideEvents.MapName),
            "music" => happened.Contains(GuideEvents.MapMusic),
            "names" => happened.Contains(GuideEvents.ClassicNames),
            "keys" => happened.Contains(GuideEvents.BuilderKeys),
            "palette" => happened.Contains(GuideEvents.Palette),
            _ => false,
        };
    }

    private static bool IsDoor(int special) => special is Smart.Doors.DoorOpen or Smart.Doors.DoorRaise or Smart.Doors.DoorLockedRaise;

    private static int Rooms(MapFrame f) => f.Sectors.Count(s => s.Element is not null);

    /// <summary>Walls with a different room on each side: what a room drawn against another gets.</summary>
    private static int SharedWalls(MapFrame f) => f.Lines.Count(l => l.Element is not null && l.Back >= 0 && f.Sides[l.Front].Sector != f.Sides[l.Back].Sector);

    /// <summary>
    /// What to say when a step is not done but the user has clearly tried: the room step with a new room that touches
    /// nothing. Null when there is nothing to add to the step's own words.
    /// </summary>
    public static string? Hint(GuideStep step, MapFrame now, MapFrame atStart) => step.Id switch
    {
        "room" when Rooms(now) > Rooms(atStart) && SharedWalls(now) <= SharedWalls(atStart) =>
            "Your new room does not touch the first one, so they share no wall. Drag it against the first room (it snaps to the wall), or press Ctrl+Z and draw it starting on that wall.",
        "door" when SharedWalls(now) == 0 =>
            "Your rooms share no wall yet: a door needs the line where two rooms meet. Drag one room against the other first (it snaps).",
        _ => null,
    };

    private static int Lines(MapFrame f, Func<LineFrame, bool> which) => f.Lines.Count(l => l.Element is not null && which(l));

    /// <summary>Whether an element in both frames has a different <paramref name="value"/> now.</summary>
    private static bool Changed<TFrame, TValue>(IEnumerable<TFrame> before, IEnumerable<TFrame> after, Func<TFrame, object?> element, Func<TFrame, TValue> value)
    {
        var was = new Dictionary<object, TValue>(ReferenceEqualityComparer.Instance);
        foreach (var f in before)
        {
            if (element(f) is { } e) was[e] = value(f);
        }

        foreach (var f in after)
        {
            if (element(f) is { } e && was.TryGetValue(e, out var v) && !EqualityComparer<TValue>.Default.Equals(v, value(f))) return true;
        }

        return false;
    }

    /// <summary>A flight of stairs drawn since the step began: two or more new rooms at different floor heights.</summary>
    private static bool Flight(MapFrame before, MapFrame now)
    {
        var old = new HashSet<object>(before.Sectors.Where(s => s.Element is not null).Select(s => (object)s.Element!), ReferenceEqualityComparer.Instance);
        var floors = now.Sectors.Where(s => s.Element is not null && !old.Contains(s.Element)).Select(s => s.FloorHeight).ToList();
        return floors.Count >= 2 && floors.Distinct().Count() >= 2;
    }

    /// <summary>
    /// A door's own face given another wall's texture since the step began, as a secret door's is. Only doors there when the
    /// step began count: making a door paints the faces it is cut from too.
    /// </summary>
    private static bool DoorFaceRetextured(MapFrame before, MapFrame now)
    {
        var was = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var s in before.Sides)
        {
            if (s.Element is { } e) was[e] = s.Top;
        }

        var doors = new HashSet<object>(before.Lines.Where(l => l.Element is not null && IsDoor(l.Special) && l.Args[0] == 0).Select(l => (object)l.Element!), ReferenceEqualityComparer.Instance);
        foreach (var l in now.Lines)
        {
            if (l.Element is null || !doors.Contains(l.Element) || !IsDoor(l.Special) || l.Args[0] != 0) continue;
            foreach (var slot in (int[])[l.Front, l.Back])
            {
                if (slot >= 0 && now.Sides[slot] is { Element: { } e } s && was.TryGetValue(e, out var top) && top != s.Top) return true;
            }
        }

        return false;
    }

    private static UdmfValue? Field(KeyValuePair<string, UdmfValue>[] extra, string name)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return null;
    }

    private static string Offsets(KeyValuePair<string, UdmfValue>[] extra) =>
        string.Join(";", extra.Where(p => p.Key.StartsWith("offset", StringComparison.OrdinalIgnoreCase)).Select(p => $"{p.Key}={p.Value}"));

    private static string Skills(KeyValuePair<string, UdmfValue>[] extra) =>
        string.Concat(Enumerable.Range(1, 5).Select(i => Field(extra, "skill" + i) is { Kind: UdmfValueKind.Boolean } v && v.AsBool() ? '1' : '0'));
}
