using System.Globalization;
using System.Text.RegularExpressions;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Checks;

/// <summary>Something the engine said about the map when it was played: a texture it did not find, and where.</summary>
/// <param name="Texture">The name it did not find.</param>
/// <param name="Where">The wall face or the area (the map's own element, as of the Play).</param>
/// <param name="Part">"upper", "middle", "lower", "floor" or "ceiling".</param>
public sealed record EngineFinding(string Texture, Element Where, string Part);

/// <summary>
/// Reads GZDoom's log of a Play into findings on the map's own elements (Phase 4: log → Problems). The engine counts walls
/// and areas as the map was saved — the live ones in slot order — so the frame that was played names them. Lines as GZDoom
/// 4.14 writes them (measured 2026-10-12): "Unknown middle texture 'X' on first side of linedef 0", "Unknown floor texture
/// 'X' in sector 0".
/// </summary>
public static partial class EngineLog
{
    [GeneratedRegex(@"^Unknown (upper|middle|lower) texture '([^']*)' on (first|second) side of linedef (\d+)", RegexOptions.Multiline)]
    private static partial Regex WallTexture();

    [GeneratedRegex(@"^Unknown (floor|ceiling) texture '([^']*)' in sector (\d+)", RegexOptions.Multiline)]
    private static partial Regex FlatTexture();

    public static IReadOnlyList<EngineFinding> Read(string log, MapFrame played)
    {
        var lines = played.Lines.Where(l => l.Element is not null).Select(l => l.Element!).ToList();
        var sectors = played.Sectors.Where(s => s.Element is not null).Select(s => s.Element!).ToList();
        var found = new List<EngineFinding>();
        foreach (Match m in WallTexture().Matches(log))
        {
            var i = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            if (i >= lines.Count) continue;
            var face = m.Groups[3].Value == "first" ? lines[i].Front : lines[i].Back;
            if (face is not null) found.Add(new EngineFinding(m.Groups[2].Value, face, m.Groups[1].Value));
        }

        foreach (Match m in FlatTexture().Matches(log))
        {
            var i = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (i < sectors.Count) found.Add(new EngineFinding(m.Groups[2].Value, sectors[i], m.Groups[1].Value));
        }

        return found;
    }

    /// <summary>The findings still true: the element is on the map and still has that texture there.</summary>
    public static IReadOnlyList<EngineFinding> Still(IReadOnlyList<EngineFinding> findings) => [.. findings.Where(f => f.Where.OnMap && Now(f) == f.Texture)];

    private static string? Now(EngineFinding f) => (f.Where, f.Part) switch
    {
        (Side s, "upper") => s.Top,
        (Side s, "middle") => s.Middle,
        (Side s, "lower") => s.Bottom,
        (Sector s, "floor") => s.FloorTexture,
        (Sector s, "ceiling") => s.CeilingTexture,
        _ => null,
    };

    /// <summary>
    /// The findings as Problems, one a texture: what the game shows there (a checkerboard — seen in GZDoom 2026-10-12), and
    /// where. Those on elements <paramref name="known"/> already names (WadSmith's own check found them first) are left out.
    /// </summary>
    public static IReadOnlyList<Problem> Problems(IReadOnlyList<EngineFinding> findings, IReadOnlySet<Element>? known = null) =>
        [.. findings.Where(f => known is null || !known.Contains(f.Where) && !(f.Where is Side { Line: { } l } && known.Contains(l))).GroupBy(f => f.Texture).Select(g =>
        {
            var walls = g.Count(f => f.Where is Side);
            var areas = g.Count(f => f.Where is Sector);
            var where = string.Join(" and ", new[] { walls == 0 ? null : walls == 1 ? "1 wall" : $"{walls} walls", areas == 0 ? null : areas == 1 ? "1 floor or ceiling" : $"{areas} floors or ceilings" }.OfType<string>());
            return new Problem("engine-missing-texture", Severity.Error, $"When the game ran, it had no texture called {g.Key} for {where} and drew a checkerboard there: put one of the game's textures there (the Textures shelf).", [.. g.Select(f => f.Where).Distinct()]);
        })];
}
