using System.Globalization;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Editing;

/// <summary>What one line of an edit script did: its number, and what it made or why it could not.</summary>
public sealed record ScriptStep(int Line, string Text, bool Done, string Said);

/// <summary>
/// Edit scripts: a map built by the editor's own tools, one command a line, so a map can be made, checked and
/// remade without the window (<c>wadsmith-cli script</c>, and tests). Coordinates are map units; # starts a comment.
/// Points pick what they are in or near: a room at the point, the wall nearest it.
/// </summary>
public static class EditScript
{
    /// <summary>The commands, with what they take, for the help text.</summary>
    public const string Commands = """
        room X0 Y0 X1 Y1            a rectangular room (drawn as the Room tool draws it)
        shape X Y X Y X Y …         a room of any shape, corner by corner
        round X0 Y0 X1 Y1 [SIDES]   a round room in the box
        look NAME                   new rooms look like the named look (Tech base, Hell, …)
        floor X Y HEIGHT            the floor of the room at the point
        ceiling X Y HEIGHT          its ceiling
        light X Y                   a light
        door X Y                    a door in the wall nearest the point
        lock X Y COLOUR             the door at the point locked with the red, blue or yellow key
        key COLOUR X Y              a keycard
        window X Y [WIDTH]          a window in the wall nearest the point
        lift X Y                    the room at the point becomes a lift
        stairs X0 Y0 X1 Y1          stairs from the first point (bottom) to the second (top)
        teleport X Y TOX TOY        the room at the point sends to the second point
        secret X Y                  the room at the point becomes a secret
        switch X Y WALLX WALLY      a switch on the wall nearest the second point for the door or lift at the first
        exit X Y                    the wall nearest the point becomes the exit switch
        split X Y X Y …             a line across a room splits it
        join X Y X Y …              the rooms at the points join into the first
        start X Y [ANGLE]           the player's start
        thing TYPE X Y [ANGLE]      an object by editor number
        """;

    /// <summary>Runs <paramref name="script"/> on <paramref name="map"/>, a line at a time, each one undo step; returns what each line did.</summary>
    public static IReadOnlyList<ScriptStep> Run(EditorMap map, string script, Look look, string engine)
    {
        var history = new History(map);
        var steps = new List<ScriptStep>();
        var number = 0;
        foreach (var raw in script.Split('\n'))
        {
            number++;
            var text = raw.Split('#')[0].Trim();
            if (text.Length == 0) continue;
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string said;
            bool done;
            try
            {
                (done, said) = Step(map, history, words, ref look, engine);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                (done, said) = (false, ex is IndexOutOfRangeException ? "something is missing after the command" : ex.Message);
            }

            steps.Add(new ScriptStep(number, text, done, said));
        }

        return steps;
    }

    private static (bool Done, string Said) Step(EditorMap map, History history, string[] w, ref Look look, string engine)
    {
        var g = MapGeometry.Build(map.Frame);
        double N(int i) => double.Parse(w[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        MapPoint P(int i) => new(N(i), N(i + 1));
        Sector Room(int i) => g.SectorAt(P(i)) ?? throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"no room at ({N(i)}, {N(i + 1)})"));
        Line Wall(int i) => g.LineNear(P(i), 32) ?? throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"no wall near ({N(i)}, {N(i + 1)})"));
        var l = look;
        string? why = null;
        string made = "";
        void Record(string label, Action<StructureEdit> action) => history.Record(label, action);

        switch (w[0].ToLowerInvariant())
        {
            case "room":
                Record("Draw a room", e => made = $"{ShapeDrawing.Draw(e, g, [P(1), new(N(1), N(4)), P(3), new(N(3), N(2))], l).Count} area(s)");
                break;
            case "shape":
                Record("Draw a room", e => made = $"{ShapeDrawing.Draw(e, g, [.. Enumerable.Range(0, (w.Length - 1) / 2).Select(i => P(1 + 2 * i))], l).Count} area(s)");
                break;
            case "round":
                Record("Draw a room", e => made = $"{ShapeDrawing.Draw(e, g, Shapes.Ellipse(P(1), P(3), w.Length > 5 ? (int)N(5) : Shapes.DefaultSides), l).Count} area(s)");
                break;
            case "look":
                var name = string.Join(' ', w.Skip(1));
                look = Look.Named(engine).FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).Look ?? throw new ArgumentException($"no look called {name}");
                return (true, $"new rooms look like {name}");
            case "floor" or "ceiling":
                var area = Room(1);
                var height = N(3);
                Record(w[0] == "floor" ? "Change floor height" : "Change ceiling height", e =>
                {
                    if (w[0] == "floor") e.Set(area, s => s.FloorHeight, (s, v) => s.FloorHeight = v, height);
                    else e.Set(area, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, height);
                });
                made = string.Create(CultureInfo.InvariantCulture, $"{w[0]} {height}");
                break;
            case "light":
                Record("Place a light", e => e.Add(Lights.New(P(1), engine)));
                made = "a light";
                break;
            case "door":
                var doorWall = Wall(1);
                Record("Make a door", e => why = Doors.Make(e, g, doorWall, l).Why);
                made = "a door";
                break;
            case "lock":
                var colour = w[3].ToLowerInvariant();
                var keys = Keys.Doom.FirstOrDefault(k => k.Colour == colour);
                if (keys.Colour is null) throw new ArgumentException($"no key is {w[3]}");
                if (Doors.Recognise(g, Room(1)) is not { } door) throw new ArgumentException("no door there");
                Record("Change the door's lock", e => Doors.Change(e, door, door.Settings with { Lock = keys.Lock }, false, door.StartsOpen));
                made = $"locked with the {colour} key";
                break;
            case "key":
                var kc = w[1].ToLowerInvariant();
                var key = Keys.Doom.FirstOrDefault(k => k.Colour == kc);
                if (key.Colour is null) throw new ArgumentException($"no key is {w[1]}");
                Record("Place a thing", e => e.Add(Thing(key.Card, P(2), 90, engine)));
                made = $"the {kc} key";
                break;
            case "window":
                var windowWall = Wall(1);
                var along = Along(windowWall, P(1));
                var width = w.Length > 3 ? N(3) : WallWindows.DefaultWidth;
                Record("Make a window", e => why = WallWindows.Make(e, g, windowWall, along - width / 2, along + width / 2, l).Why);
                made = "a window";
                break;
            case "lift":
                var liftArea = Room(1);
                Record("Make a lift", e => why = Lifts.Make(e, g, liftArea));
                made = "a lift";
                break;
            case "stairs":
                Record("Make stairs", e => (_, why) = Smart.Stairs.Make(e, g, P(1), P(3), l));
                made = "stairs";
                break;
            case "teleport":
                var pad = Room(1);
                Record("Make a teleporter", e => why = Teleports.Make(e, g, pad, P(3), 90, engine).Why);
                made = "a teleporter";
                break;
            case "secret":
                var secret = Room(1);
                Record("Make a secret", e => Secrets.Make(e, g, secret));
                made = "a secret";
                break;
            case "switch":
                var target = Room(1);
                var switchWall = Wall(3);
                Record("Make a switch", e => why = Switches.Make(e, g, switchWall, P(3), target, l).Why);
                made = "a switch";
                break;
            case "exit":
                var exitWall = Wall(1);
                Record("Make the exit", e => Exits.Make(e, exitWall, false, l));
                made = "the exit switch";
                break;
            case "split":
                Record("Split a room", e => why = Splitting.Split(e, g, [.. Enumerable.Range(0, (w.Length - 1) / 2).Select(i => P(1 + 2 * i))]).Why);
                made = "a room split in two";
                break;
            case "join":
                var rooms = Enumerable.Range(0, (w.Length - 1) / 2).Select(i => Room(1 + 2 * i)).Distinct().ToList();
                Record("Join rooms", e => Rooms.Join(e, g, rooms));
                made = $"{rooms.Count} rooms joined";
                break;
            case "start":
                Record("Place a thing", e => e.Add(Thing(1, P(1), w.Length > 3 ? (int)N(3) : 90, engine)));
                made = "the player's start";
                break;
            case "thing":
                Record("Place a thing", e => e.Add(Thing((int)N(1), P(2), w.Length > 4 ? (int)N(4) : 90, engine)));
                made = $"thing {w[1]}";
                break;
            default:
                return (false, $"no command called {w[0]}");
        }

        return why is null ? (true, made) : (false, why);
    }

    private static Thing Thing(int type, MapPoint at, int angle, string engine)
    {
        var t = new Thing { X = at.X, Y = at.Y, Type = type, Angle = angle };
        foreach (var f in Smart.Things.Everywhere(engine)) t.SetFlag(f, true);
        return t;
    }

    /// <summary>How far along a wall, from its start, the point nearest <paramref name="p"/> is.</summary>
    private static double Along(Line wall, MapPoint p)
    {
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        return Math.Clamp(((p.X - wall.Start.X) * dx + (p.Y - wall.Start.Y) * dy) / wall.Length, 0, wall.Length);
    }
}
