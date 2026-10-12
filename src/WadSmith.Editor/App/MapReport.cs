using System.Globalization;
using WadSmith.Core.Games;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.App;

/// <summary>One line of a map's explanation, with the spot its Show button centres on (none for a plain fact).</summary>
public sealed record ReportLine(string Text, MapPoint? At = null);

/// <summary>A titled group of lines.</summary>
public sealed record ReportSection(string Heading, IReadOnlyList<ReportLine> Lines);

/// <summary>"Explain this map" (D32): a sentence about the whole map, then what is in it, section by section.</summary>
public sealed record MapReport(string Summary, IReadOnlyList<ReportSection> Sections);

/// <summary>
/// Explains any map the way a mapper reads one: what the editor recognises in it and where the first of each is, which
/// keys open which doors (and a locked door whose key is missing), and what the player meets on each difficulty. Reads a
/// private copy of a frame, so a worker runs it while the map is edited.
/// </summary>
public static class MapReporting
{
    private static readonly (string Name, string[] Flags)[] Difficulties = [("Easy", ["skill1", "skill2"]), ("Normal", ["skill3"]), ("Hard", ["skill4", "skill5"])];

    public static MapReport Explain(MapFrame frame, GameResources? resources)
    {
        var map = EditorMap.FromModel(frame.ToModel());
        var geometry = MapGeometry.Build(map.Frame);
        ThingCategory? Category(int type) => resources?.Things.Find(type)?.Category;
        bool IsKey(int type) => Category(type) == ThingCategory.Key || Keys.ColourOf(type) is not null;
        string Name(int type) => resources?.Things.Find(type)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"Object {type}");

        var rooms = map.Sectors.Count(s => s.OnMap);
        var things = map.Things.Where(t => t.OnMap).ToList();
        var sections = new List<ReportSection>();

        // What the editor recognises: how many of each, and the first the player meets.
        var found = Landmarks.Find(map, geometry, IsKey);
        var features = new List<ReportLine>();
        foreach (var kind in Landmarks.Kinds.Where(k => k != "key"))
        {
            var all = found.Where(f => f.Kind == kind).ToList();
            if (all.Count > 0) features.Add(new(Count(all.Count, kind) + (all.Count > 1 ? "; Show goes to the nearest the start." : "."), all[0].At));
        }

        if (features.Count == 0) features.Add(new("No doors, lifts, stairs, windows, secrets, switches, teleporters or exits yet."));
        sections.Add(new("WHAT IT IS MADE OF", features));

        // Keys and the doors they open, and any lock with no key of its kind on the map.
        var keyLines = new List<ReportLine>();
        foreach (var group in things.Where(t => IsKey(t.Type)).GroupBy(t => t.Type))
        {
            var opens = Keys.DoorsOpenedBy(geometry, group.Key).Select(l => l.Back?.Sector ?? (object)l).Distinct().Count();
            var first = group.First();
            keyLines.Add(new($"{Name(group.Key)}{(group.Count() > 1 ? $" (×{group.Count()})" : "")} opens {(opens == 0 ? "no door here" : opens == 1 ? "1 door" : $"{opens} doors")}.", new(first.X, first.Y)));
        }

        foreach (var locked in map.Lines.Where(l => l.OnMap && l.Special == Doors.DoorLockedRaise && l.Args[3] != 0).GroupBy(l => l.Args[3]))
        {
            var needs = Keys.Opening(locked.Key);
            if (needs.Count == 0 || things.Any(t => needs.Contains(t.Type))) continue;
            var wall = locked.First();
            var colour = Keys.ColourOfLock(locked.Key);
            keyLines.Add(new($"A door needs the {colour ?? "missing"} key, and the map has none: the player cannot open it.", Middle(wall)));
        }

        if (keyLines.Count > 0) sections.Add(new("KEYS", keyLines));

        // What the player meets on each difficulty.
        var meets = new List<ReportLine>();
        foreach (var (name, flags) in Difficulties)
        {
            // What a player alone meets: objects for games with friends only are left out.
            var on = things.Where(t => flags.Any(t.Flag) && t.Flag("single")).ToList();
            int Of(params ThingCategory[] kinds) => on.Count(t => Category(t.Type) is { } c && kinds.Contains(c));
            meets.Add(new(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {Count(Of(ThingCategory.Monster), "monster")}, {Count(Of(ThingCategory.Weapon), "weapon")}, {Of(ThingCategory.Ammo)} ammunition, {Of(ThingCategory.Health)} health, {Of(ThingCategory.Armor)} armour, {Count(Of(ThingCategory.Powerup), "power-up")}.")));
        }

        sections.Add(new("WHAT THE PLAYER MEETS", meets));

        // Where the player starts.
        if (things.FirstOrDefault(t => t.Type == 1) is { } start) sections.Add(new("WHERE IT STARTS", [new("Player 1 starts here.", new(start.X, start.Y))]));
        else sections.Add(new("WHERE IT STARTS", [new("There is no player 1 start: the game cannot begin this map.")]));

        var normal = things.Count(t => t.Flag("skill3") && t.Flag("single") && Category(t.Type) == ThingCategory.Monster);
        var secrets = found.Count(f => f.Kind == "secret");
        var exits = found.Count(f => f.Kind == "exit");
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{Count(rooms, "room")}, {Count(normal, "monster")} on Normal, {Count(secrets, "secret")} and {(exits == 0 ? "no exit" : Count(exits, "exit"))}.");
        return new(summary, sections);
    }

    private static MapPoint Middle(Line l) => new((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);

    private static string Count(int n, string what) => n switch
    {
        1 => what == "stairs" ? "1 flight of stairs" : $"1 {what}",
        _ => string.Create(CultureInfo.InvariantCulture, $"{n} {Plural(what)}"),
    };

    private static string Plural(string what) => what switch
    {
        "stairs" => "flights of stairs",
        "switch" => "switches",
        "ammunition" or "health" or "armour" => what,
        _ => what + "s",
    };
}
