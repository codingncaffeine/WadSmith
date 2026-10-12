using System.Globalization;

namespace WadSmith.Editor.Geometry;

/// <summary>A stretch of the line inside one area (or outside every area).</summary>
/// <param name="From">Where it starts, in map units from the line's start.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Area">The area's slot, or -1 outside the map.</param>
/// <param name="Floor">The area's floor height.</param>
/// <param name="Ceiling">The area's ceiling height.</param>
/// <param name="Sky">Whether the area is open to the sky.</param>
public readonly record struct ProfileSpan(double From, double To, int Area, double Floor, double Ceiling, bool Sky);

/// <summary>Where the line goes from one area into the next: how far up or down the floor goes, the room left to stand, and what stops the player there.</summary>
/// <param name="At">Where, in map units from the line's start.</param>
/// <param name="Rise">The next floor less this one: positive up.</param>
/// <param name="Opening">How much room there is between the higher floor and the lower ceiling.</param>
/// <param name="Stops">Why the player cannot walk on there, in words; null when the player can.</param>
public readonly record struct ProfileStep(double At, double Rise, double Opening, string? Stops);

/// <summary>
/// A cross-section of the map along a line (the elevation profile): the floors and ceilings it passes over, and every
/// step from one area to the next, with what the player can't climb or fit through. Reads only a geometry's frame, so a
/// worker can draw one across the whole map.
/// </summary>
public sealed record ElevationProfile(MapPoint From, MapPoint To, double Length, IReadOnlyList<ProfileSpan> Spans, IReadOnlyList<ProfileStep> Steps)
{
    /// <summary>The highest the player steps up, and the room the player needs to stand (the engine's numbers).</summary>
    public const double StepUp = 24;
    public const double PlayerHeight = 56;

    /// <summary>The cross-section from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static ElevationProfile Build(MapGeometry geometry, MapPoint from, MapPoint to)
    {
        var f = geometry.Frame;
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return new ElevationProfile(from, to, 0, [], []);

        // Where the line crosses walls, as shares of its length.
        var cuts = new List<double> { 0, 1 };
        foreach (var slot in geometry.LineSlotsIn(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y)))
        {
            var l = f.Lines[slot];
            if (l.Element is null) continue;
            var (a, b) = (f.Vertices[l.V1], f.Vertices[l.V2]);
            var (ex, ey) = (b.X - a.X, b.Y - a.Y);
            var denominator = dx * ey - dy * ex;
            if (Math.Abs(denominator) < 1e-12) continue;
            var t = ((a.X - from.X) * ey - (a.Y - from.Y) * ex) / denominator;
            var u = ((a.X - from.X) * dy - (a.Y - from.Y) * dx) / denominator;
            if (t > 1e-9 && t < 1 - 1e-9 && u >= -1e-9 && u <= 1 + 1e-9) cuts.Add(t);
        }

        cuts.Sort();
        var spans = new List<ProfileSpan>();
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var (t0, t1) = (cuts[i], cuts[i + 1]);
            if (t1 - t0 < 1e-9) continue;
            var mid = (t0 + t1) / 2;
            var area = geometry.SectorAt(new MapPoint(from.X + dx * mid, from.Y + dy * mid)) is { } s && s.Slot < f.Sectors.Count ? s.Slot : -1;
            var span = area < 0
                ? new ProfileSpan(t0 * length, t1 * length, -1, 0, 0, false)
                : new ProfileSpan(t0 * length, t1 * length, area, f.Sectors[area].FloorHeight, f.Sectors[area].CeilingHeight, IsSky(f.Sectors[area].CeilingTexture));

            // A wall between two parts of one area is no step: the span goes on.
            if (spans.Count > 0 && spans[^1].Area == span.Area) spans[^1] = spans[^1] with { To = span.To };
            else spans.Add(span);
        }

        var steps = new List<ProfileStep>();
        for (var i = 0; i + 1 < spans.Count; i++)
        {
            var (here, there) = (spans[i], spans[i + 1]);
            var rise = there.Floor - here.Floor;
            var opening = Math.Min(here.Ceiling, there.Ceiling) - Math.Max(here.Floor, there.Floor);
            string? stops = (here.Area < 0, there.Area < 0) switch
            {
                (_, true) => "a solid wall",
                (true, _) => null,
                _ when rise > StepUp => string.Create(CultureInfo.InvariantCulture, $"{rise:0} up: too high to step"),
                _ when there.Ceiling - there.Floor < PlayerHeight => string.Create(CultureInfo.InvariantCulture, $"{there.Ceiling - there.Floor:0} tall: too low to stand"),
                _ when opening < PlayerHeight => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, opening):0} high: too low to pass"),
                _ => null,
            };
            steps.Add(new ProfileStep(here.To, rise, opening, stops));
        }

        return new ElevationProfile(from, to, length, spans, steps);
    }

    /// <summary>The engine's sky flats: an area under one is open to the sky.</summary>
    private static bool IsSky(string flat) => flat.StartsWith("F_SKY", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the line shows, in a sentence: how long, how many areas, the floors' range and the first place the player is stopped.</summary>
    public string Describe()
    {
        var areas = Spans.Count(s => s.Area >= 0);
        if (areas == 0) return string.Create(CultureInfo.InvariantCulture, $"{Length:0} long, outside the map.");
        var inside = Spans.Where(s => s.Area >= 0).ToList();
        var floors = inside.Min(s => s.Floor) == inside.Max(s => s.Floor) ? string.Create(CultureInfo.InvariantCulture, $"floor at {inside[0].Floor:0}") : string.Create(CultureInfo.InvariantCulture, $"floors from {inside.Min(s => s.Floor):0} to {inside.Max(s => s.Floor):0}");
        var stop = Steps.FirstOrDefault(s => s.Stops is not null && s.At > Spans.First(x => x.Area >= 0).From);
        var how = stop.Stops is null ? "the player walks all of it" : string.Create(CultureInfo.InvariantCulture, $"the player is stopped {stop.At:0} along: {stop.Stops}");
        return string.Create(CultureInfo.InvariantCulture, $"{Length:0} long ({Length / 32:0.#} players across), {areas} area{(areas == 1 ? "" : "s")}, {floors}; {how}.");
    }
}
