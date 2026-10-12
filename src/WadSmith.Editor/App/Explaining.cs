using System.Globalization;
using WadSmith.Core.Games;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.App;

/// <summary>What something in the map is, in a heading and a few plain sentences.</summary>
public sealed record Explanation(string Heading, string Text);

/// <summary>
/// "What is this?" (D33) and the Inspector's summaries: what a room, wall, corner or object is, what it does in the game and
/// how to change it, from what the map's smart objects are recognised as (D21). Each call looks at one element and its
/// neighbours only, so it is cheap enough for the pointer.
/// </summary>
public static class Explaining
{
    /// <summary>The player's height and how high a step the player climbs without help.</summary>
    private const double PlayerHeight = 56;
    private const double StepUp = 24;

    /// <summary>What <paramref name="element"/> is; null for nothing.</summary>
    public static Explanation? Explain(Element? element, MapGeometry geometry, GameResources? resources) => element switch
    {
        null => null,
        Side side => side.Line is { } line ? Explain(line, geometry, resources) : null,
        Sector s => Room(s, geometry),
        Line l => Wall(l, geometry),
        Thing t => Object(t, geometry, resources),
        Vertex => new("Corner", "Where walls meet. Drag it to reshape every wall that ends here; double-click a wall to add another corner on it."),
        _ => null,
    };

    private static Explanation Room(Sector s, MapGeometry g)
    {
        if (Platforms.ControlOf(g, s) is { } held)
        {
            return new(held.Kind == PlatformKind.Water ? "Water's control room" : "Platform's control room",
                "A little room outside the map that the game reads a platform from: its floor is the platform's underside and its ceiling the top. The player never sees it.");
        }

        if (Doors.Recognise(g, s) is { } door) return new("Door", Describe(door) + " Click it to change its key, its speed or how long it stays open.");
        if (Lifts.Recognise(g, s) is { } lift) return new("Lift", Describe(lift) + " Click it to change its speed or how long it waits.");
        if (WallWindows.Recognise(g, s) is { } window) return new("Window", Describe(window));
        if (StairsFinder.Recognise(g, s) is { } stairs) return new("Stairs", Describe(stairs, s) + " Click it to change how much each step rises.");
        if (Secrets.IsSecret(s))
        {
            return new("Secret area", (Secrets.Recognise(g, s) is { } secret ? Describe(secret) : "Counts toward the map's secrets when the player steps in.") + " Click it to hide its door.");
        }

        var tall = s.CeilingHeight - s.FloorHeight;
        var fits = tall < PlayerHeight
            ? string.Create(CultureInfo.InvariantCulture, $" At {tall:0} tall it is too low for the player, who needs {PlayerHeight:0}.")
            : "";
        return new("Room", string.Create(CultureInfo.InvariantCulture,
            $"An area of the map with its own floor at {s.FloorHeight:0}, ceiling at {s.CeilingHeight:0} and brightness {s.Light} of 255.{fits} Drag a texture onto it to change its floor; click it to change its heights, light and colour."));
    }

    private static Explanation Wall(Line l, MapGeometry g)
    {
        if (Switches.Recognise(g, l) is { } sw) return new("Switch", Describe(sw));
        if (Teleports.IsTeleport(l)) return new("Teleporter", Teleports.Recognise(g, l) is { } t ? Describe(t) : "Sends the player somewhere else in the map.");
        if (Exits.IsExit(l))
        {
            return new(l.Flag("playeruse") ? "Exit switch" : l.Flag("playercross") ? "Exit line" : "Exit",
                $"Ends the map when the player {Exits.How(l)}, going on to {(l.Special == Exits.Secret ? "the secret map" : "the next map")}.");
        }

        // A door's own face: what the door it opens does.
        foreach (var side in (Sector?[])[l.Back?.Sector, l.Front.Sector])
        {
            if (side is not null && Doors.Recognise(g, side) is { } door && door.Openers.Contains(l)) return new("Door", Describe(door));
        }

        if (l.Back is not { } back)
        {
            return new("Solid wall", "Nothing passes through it. Drop a texture on it to change its look; on the plan, drag it to move it or double-click it to add a corner.");
        }

        var (a, b) = (l.Front.Sector, back.Sector);
        var step = Math.Abs(a.FloorHeight - b.FloorHeight);
        var gap = Math.Min(a.CeilingHeight, b.CeilingHeight) - Math.Max(a.FloorHeight, b.FloorHeight);
        var how = l.Flag("blocking") ? " It blocks the player, so nothing walks through it."
            : gap < PlayerHeight ? string.Create(CultureInfo.InvariantCulture, $" The opening is {Math.Max(0, gap):0} tall, too low for the player.")
            : step > StepUp ? string.Create(CultureInfo.InvariantCulture, $" The floors differ by {step:0}, more than the player climbs ({StepUp:0}): a ledge or a drop.")
            : step > 0 ? string.Create(CultureInfo.InvariantCulture, $" The floors differ by {step:0}, a step the player climbs.")
            : " The floors are level, so the player walks straight through.";
        return new("Wall between two areas", "Joins two areas: what is above and below the opening is drawn with its upper and lower textures." + how);
    }

    private static Explanation Object(Thing t, MapGeometry g, GameResources? resources)
    {
        if (Lights.Recognise(t) is { } light) return new("Light", Describe(light));
        if (Teleports.IsArrival(t)) return new("Teleport arrival", "Where a teleporter sends whoever steps on it, facing the way its arrow points.");
        var known = resources?.Things.Find(t.Type);
        if (t.Type is >= 1 and <= 4) return new($"Player {t.Type} start", t.Type == 1 ? "Where the player begins the map, facing the way its arrow points. Play starts you here." : $"Where player {t.Type} begins in a game with friends.");
        if (Keys.ColourOf(t.Type) is { } colour) return new(known?.Name ?? $"The {colour} key", $"The player picks it up to open doors locked with the {colour} key.");
        var name = known?.Name ?? string.Create(CultureInfo.InvariantCulture, $"Object {t.Type}");
        var what = known?.Category switch
        {
            ThingCategory.Monster => t.Flag("ambush")
                ? "A monster. It is deaf: it waits until it sees the player."
                : "A monster. It wakes when it sees or hears the player.",
            ThingCategory.Weapon => "A weapon the player picks up.",
            ThingCategory.Ammo => "Ammunition the player picks up.",
            ThingCategory.Health => "Health the player picks up.",
            ThingCategory.Armor => "Armour the player picks up.",
            ThingCategory.Powerup => "Something the player picks up for a power or a tool.",
            ThingCategory.Decoration => "A decoration: it shows in the game and does nothing to the player.",
            null => "The game does not define this object number.",
            _ => "A marker the game reads; the player never sees it.",
        };
        return new(name, what + Difficulties(t));
    }

    /// <summary>Which difficulties the object appears on, said only when it is not all of them.</summary>
    private static string Difficulties(Thing t)
    {
        var on = new List<string>();
        if (t.Flag("skill1") || t.Flag("skill2")) on.Add("Easy");
        if (t.Flag("skill3")) on.Add("Normal");
        if (t.Flag("skill4") || t.Flag("skill5")) on.Add("Hard");
        return on.Count switch
        {
            3 => "",
            0 => " It appears on no difficulty, so the game never shows it.",
            _ => $" It appears on {string.Join(" and ", on)} only.",
        };
    }

    // The sentences the Inspector shows too, one per smart object.
    public static string Describe(Lift lift)
    {
        var ways = string.Join(", ", Triggers.Ways(lift.Triggers));
        var wait = string.Create(CultureInfo.InvariantCulture, $"{lift.Settings.Delay / 35.0:0.#} s");
        return string.Create(CultureInfo.InvariantCulture, $"Goes down to {lift.GoesDownTo:0} when set off from {ways}{(lift.Monsters ? " (monsters too)" : "")}, waits {wait} and comes back up.");
    }

    public static string Describe(Light l)
    {
        var name = Lights.Colours.FirstOrDefault(c => (c.R, c.G, c.B) == l.Colour).Name?.ToLowerInvariant() ?? Lights.Hex(l.Colour);
        var how = l.Kind switch
        {
            LightKind.Pulsing => string.Create(CultureInfo.InvariantCulture, $", pulsing between {l.Reach} and {l.SecondReach}"),
            LightKind.Flickering or LightKind.RandomFlicker => string.Create(CultureInfo.InvariantCulture, $", flickering between {l.Reach} and {l.SecondReach}"),
            _ => "",
        };
        return string.Create(CultureInfo.InvariantCulture, $"A {name} light reaching {l.Reach}{how}, {l.Height:0} above the floor. The 3D view shows it; GZDoom draws it in the game.");
    }

    public static string Describe(Switch s)
    {
        var what = s.Targets.Count switch
        {
            0 => "",
            1 => string.Create(CultureInfo.InvariantCulture, $" (the area with tag {s.Wall.Args[0]})"),
            var n => string.Create(CultureInfo.InvariantCulture, $" ({n} areas with tag {s.Wall.Args[0]})"),
        };
        var none = s.Targets.Count == 0 && Switches.NamesAreas(s.Wall.Special) ? string.Create(CultureInfo.InvariantCulture, $" No area has tag {s.Wall.Args[0]}, so it does nothing.") : "";
        return $"{char.ToUpperInvariant(s.Does[0])}{s.Does[1..]}{what} when the player uses it{(s.Repeats ? ", again and again" : ", once")}.{none}";
    }

    public static string Describe(Staircase st, Sector step) =>
        $"Step {st.Heights.ToList().IndexOf(step.FloorHeight) + 1} of {st.Count}, {st.Describe()}.";

    public static string Describe(Secret s)
    {
        var doors = s.Doors.Count switch { 0 => "", 1 => " A door leads into it.", var n => $" {n} doors lead into it." };
        return $"Counts toward the map's secrets when the player steps in.{doors}{(s.Hidden ? " Its way in shows as a solid wall on the in-game map." : " Its way in shows on the in-game map.")}";
    }

    public static string Describe(WallWindow w)
    {
        var sill = w.Sill - w.Rooms.Min(r => r.FloorHeight);
        return string.Create(CultureInfo.InvariantCulture, $"An opening {w.Top - w.Sill:0} tall between two rooms, its sill {sill:0} above the lower floor{(w.Blocks ? "; nothing walks or flies through it" : "")}. The player sees through it and cannot climb in.");
    }

    public static string Describe(Teleporter t)
    {
        var who = t.Monsters ? "the player or a monster" : "the player";
        var how = t.ByUse ? "uses it" : "walks across it from the front";
        var where = t.Arrivals.Count switch
        {
            0 => "nowhere: no arrival spot has its id or stands in an area with its tag",
            1 => string.Create(CultureInfo.InvariantCulture, $"the arrival spot at {t.Arrivals[0].X:0}, {t.Arrivals[0].Y:0}"),
            var n => $"the first of {n} arrival spots",
        };
        return $"Sends {who} who {how} to {where}{(t.Repeats ? "" : ", once")}.";
    }

    public static string Describe(Door d)
    {
        var remote = string.Join(", ", d.RemoteWays());
        var own = d.Own.Any() ? d.Monsters ? "when the player or a monster uses it" : "when the player uses it" : "";
        var who = remote.Length == 0 ? own : own.Length == 0 ? $"from {remote}" : $"{own}, or from {remote}";
        var key = Doors.DoomLocks.FirstOrDefault(k => k.Lock == d.Settings.Lock && k.Lock != 0).Name is { } name ? $", with the {name.ToLowerInvariant()}" : d.Settings.Lock != 0 ? $", with lock {d.Settings.Lock}" : "";
        var after = d.Settings.StaysOpen ? "stays open" : string.Create(CultureInfo.InvariantCulture, $"closes again after {d.Settings.Delay / 35.0:0.#} s");
        return $"Opens {who}{key}; {after}.";
    }
}
