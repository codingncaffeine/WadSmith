using System.Text.Json;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// One entry of the landmark index (D28): a spot in a game's own map that shows a feature, and one sentence about it.
/// The index is ours (map, coordinates, sentence); the map is read from the user's own copy of the game.
/// </summary>
public sealed record Landmark(string Game, string Map, string Kind, double X, double Y, string Says)
{
    public MapPoint At => new(X, Y);
}

/// <summary>A feature found in a map, for choosing landmarks: its kind, where it is, and how far from the player's start.</summary>
public sealed record FoundLandmark(string Kind, MapPoint At, double FromStart);

/// <summary>
/// "See it in a real map" (D28): the landmark index, the finder that lists candidates in any map (nearest the start
/// first, as a player meets them), and the check that a spot really shows its feature, which the tests run on every entry.
/// </summary>
public static class Landmarks
{
    /// <summary>The kinds, in the order the tools and the Guide name them.</summary>
    public static readonly string[] Kinds = ["door", "locked door", "lift", "stairs", "window", "secret", "switch", "teleporter", "exit", "key"];

    /// <summary>How near a wall or an object must be to a landmark's spot to count as shown there.</summary>
    public const double Near = 32;

    private static readonly Lazy<IReadOnlyList<Landmark>> Index = new(() =>
    {
        using var doc = JsonDocument.Parse(Core.DataFiles.ReadText("landmarks/landmarks.json"));
        return [.. doc.RootElement.GetProperty("landmarks").EnumerateArray().Select(e => new Landmark(
            e.GetProperty("game").GetString()!, e.GetProperty("map").GetString()!, e.GetProperty("kind").GetString()!,
            e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("says").GetString()!))];
    });

    /// <summary>Every landmark in the index.</summary>
    public static IReadOnlyList<Landmark> All => Index.Value;

    /// <summary>The id the index uses for a game: the shareware and Ultimate releases share their episode's maps.</summary>
    public static string IndexId(string gameId) => gameId switch
    {
        "doom-ultimate" or "doom-shareware" => "doom",
        "heretic-shareware" => "heretic",
        _ => gameId,
    };

    /// <summary>The landmark of <paramref name="kind"/> in the first of <paramref name="games"/> (in the caller's order of preference) that has one.</summary>
    public static Landmark? For(string kind, IEnumerable<string> games)
    {
        foreach (var game in games)
        {
            if (All.FirstOrDefault(l => l.Kind == kind && l.Game == IndexId(game)) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// A game's own map as the editor opens it: a Doom-format map translated as GZDoom reads it. Null when the file has no
    /// such map. Reads files: call it on a worker.
    /// </summary>
    public static EditorMap? Read(string file, string slot, string engineData, string mapNamespace)
    {
        var container = ResourceContainer.Open(file);
        if (MapLocator.Find(container).FirstOrDefault(m => LumpName.Comparer.Equals(m.Name, slot)) is not { } lumps) return null;
        var model = MapConversion.Read(lumps, mapNamespace);
        var xlat = lumps.Format == MapFormat.Doom ? XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(engineData)]), XlatTranslator.PathFor(model.Namespace)) : null;
        return EditorMap.FromModel(MapTranslation.ToZDoom(model, xlat, []));
    }

    /// <summary>Every feature of the kinds above in the map, nearest the player's start first.</summary>
    public static IReadOnlyList<FoundLandmark> Find(EditorMap map, MapGeometry g, Func<int, bool> isKey)
    {
        var start = map.Things.FirstOrDefault(t => t.OnMap && t.Type == 1) is { } p ? new MapPoint(p.X, p.Y) : default;
        var found = new List<FoundLandmark>();
        void Add(string kind, MapPoint at) => found.Add(new(kind, at, at.DistanceTo(start)));
        var flights = new HashSet<Sector>();
        foreach (var s in map.Sectors.Where(s => s.OnMap))
        {
            if (Centre(g, s) is not { } at) continue;
            if (Doors.Recognise(g, s) is { } door) Add(door.Settings.Lock != 0 ? "locked door" : "door", at);
            if (Lifts.Recognise(g, s) is not null) Add("lift", at);
            if (WallWindows.Recognise(g, s) is not null) Add("window", at);
            if (Secrets.Recognise(g, s) is not null) Add("secret", at);
            if (!flights.Contains(s) && StairsFinder.Recognise(g, s) is { } stairs)
            {
                flights.UnionWith(stairs.Steps);
                // The middle step stands for the flight.
                if (Centre(g, stairs.Steps[stairs.Steps.Count / 2]) is { } middle) Add("stairs", middle);
            }
        }

        foreach (var l in map.Lines.Where(l => l.OnMap))
        {
            var mid = new MapPoint((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
            if (Switches.Recognise(g, l) is not null) Add("switch", mid);
            if (Teleports.IsTeleport(l)) Add("teleporter", mid);
            if (Exits.IsExit(l)) Add("exit", mid);
        }

        foreach (var t in map.Things.Where(t => t.OnMap && isKey(t.Type))) Add("key", new(t.X, t.Y));
        return [.. found.OrderBy(f => f.FromStart)];
    }

    /// <summary>Whether the map shows a feature of <paramref name="kind"/> at <paramref name="at"/>: the area there is one, or a wall or object of it is within <see cref="Near"/>.</summary>
    public static bool Shows(string kind, MapPoint at, EditorMap map, MapGeometry g, Func<int, bool> isKey)
    {
        var area = g.SectorAt(at);
        return kind switch
        {
            "door" => area is not null && Doors.Recognise(g, area) is { Settings.Lock: 0 },
            "locked door" => area is not null && Doors.Recognise(g, area) is { Settings.Lock: not 0 },
            "lift" => area is not null && Lifts.Recognise(g, area) is not null,
            "window" => area is not null && WallWindows.Recognise(g, area) is not null,
            "secret" => area is not null && Secrets.Recognise(g, area) is not null,
            "stairs" => area is not null && StairsFinder.Recognise(g, area) is not null,
            "switch" => g.LinesIn(at.X - Near, at.Y - Near, at.X + Near, at.Y + Near).Any(l => Switches.Recognise(g, l) is not null),
            "teleporter" => g.LinesIn(at.X - Near, at.Y - Near, at.X + Near, at.Y + Near).Any(Teleports.IsTeleport),
            "exit" => g.LinesIn(at.X - Near, at.Y - Near, at.X + Near, at.Y + Near).Any(Exits.IsExit),
            "key" => map.Things.Any(t => t.OnMap && isKey(t.Type) && at.DistanceTo(new(t.X, t.Y)) <= Near),
            _ => false,
        };
    }

    /// <summary>A point inside the area: the middle of its bounds when that is inside, else a corner of it nudged in.</summary>
    private static MapPoint? Centre(MapGeometry g, Sector s)
    {
        if (g.WallBoundsAt(s.Slot) is not { } b) return null;
        var middle = new MapPoint(Math.Round((b.MinX + b.MaxX) / 2), Math.Round((b.MinY + b.MaxY) / 2));
        if (g.SectorAt(middle) == s) return middle;
        foreach (var loop in g.Loops(s))
        {
            foreach (var corner in loop)
            {
                for (var dx = -8; dx <= 8; dx += 16)
                {
                    for (var dy = -8; dy <= 8; dy += 16)
                    {
                        var p = new MapPoint(corner.X + dx, corner.Y + dy);
                        if (g.SectorAt(p) == s) return p;
                    }
                }
            }
        }

        return null;
    }
}
