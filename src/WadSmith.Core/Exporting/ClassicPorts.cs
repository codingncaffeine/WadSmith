using System.Globalization;
using System.Text.Json;

namespace WadSmith.Core.Exporting;

/// <summary>A set of numbers written as ranges and single numbers, "1-77,79-84,86".</summary>
public sealed class NumberSet
{
    private readonly (int First, int Last)[] _ranges;

    private NumberSet((int, int)[] ranges) => _ranges = ranges;

    public static NumberSet Parse(string text)
    {
        var ranges = new List<(int, int)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', 1);
            var first = int.Parse(dash < 0 ? part : part[..dash], CultureInfo.InvariantCulture);
            var last = dash < 0 ? first : int.Parse(part[(dash + 1)..], CultureInfo.InvariantCulture);
            if (last < first) throw new FormatException($"\"{part}\" runs backwards.");
            ranges.Add((first, last));
        }

        return new NumberSet([.. ranges]);
    }

    public bool Contains(int n) => _ranges.Any(r => n >= r.First && n <= r.Last);
}

/// <summary>What one classic port understands of one game family's maps (see data/classic/ports.json).</summary>
public sealed record PortRules(NumberSet Lines, bool Generalized, NumberSet Sectors, int SectorBits, int LineFlags, int ThingFlags, NumberSet Things, NumberSet? ExMyLacks);

/// <summary>
/// A classic port family a map can be exported for (Phase 12): vanilla (the 1993 engine and its limits), limit-removing,
/// Boom and MBF21, each with what it understands per game family. <see cref="Families"/> has no entry for a family the
/// port does not play (Boom and MBF21 are Doom-only).
/// </summary>
public sealed record ClassicPort(string Id, string Title, string Ports, bool VanillaLimits, int? Complevel, bool Umapinfo, IReadOnlyDictionary<string, PortRules> Families)
{
    /// <summary>The rules for a game family (a map namespace: doom, heretic, hexen, strife), or null when the port does not play it.</summary>
    public PortRules? For(string family) => Families.GetValueOrDefault(family);
}

/// <summary>The classic ports from <c>data/classic/ports.json</c>.</summary>
public static class ClassicPorts
{
    public const string Vanilla = "vanilla";
    public const string LimitRemoving = "limit-removing";
    public const string Boom = "boom";
    public const string Mbf21 = "mbf21";

    private static readonly Lazy<IReadOnlyList<ClassicPort>> Loaded = new(() => Parse(DataFiles.ReadText("classic/ports.json")));

    public static IReadOnlyList<ClassicPort> All => Loaded.Value;

    public static ClassicPort? Get(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The ports that play a game family, in the file's order.</summary>
    public static IEnumerable<ClassicPort> ForFamily(string family) => All.Where(p => p.For(family) is not null);

    internal static IReadOnlyList<ClassicPort> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raw = doc.RootElement.GetProperty("profiles").EnumerateArray().ToList();
        var byId = new Dictionary<string, Dictionary<string, PortRules>>(StringComparer.Ordinal);
        var ports = new List<ClassicPort>();
        foreach (var p in raw)
        {
            var id = p.GetProperty("id").GetString()!;
            Dictionary<string, PortRules> families;
            if (p.TryGetProperty("sameAs", out var same))
            {
                families = byId.GetValueOrDefault(same.GetString()!) ?? throw new InvalidDataException($"ports.json: \"{id}\" is the same as \"{same.GetString()}\", which comes later or does not exist.");
            }
            else
            {
                families = [];
                foreach (var f in p.GetProperty("families").EnumerateObject()) families[f.Name] = Rules(id, f.Name, f.Value);
            }

            byId[id] = families;
            var limits = p.GetProperty("limits").GetString();
            if (limits is not ("vanilla" or "format")) throw new InvalidDataException($"ports.json: \"{id}\" has limits \"{limits}\".");
            ports.Add(new ClassicPort(id, p.GetProperty("title").GetString()!, p.GetProperty("ports").GetString()!, limits == "vanilla",
                p.TryGetProperty("complevel", out var c) ? c.GetInt32() : null, p.TryGetProperty("umapinfo", out var u) && u.GetBoolean(), families));
        }

        return ports;
    }

    private static PortRules Rules(string id, string family, JsonElement e)
    {
        if (family is not ("doom" or "heretic" or "hexen" or "strife")) throw new InvalidDataException($"ports.json: \"{id}\" names an unknown family \"{family}\".");
        static int Hex(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? Convert.ToInt32(v.GetString(), 16) : 0;
        return new PortRules(
            NumberSet.Parse(e.GetProperty("lines").GetString()!),
            e.TryGetProperty("generalized", out var g) && g.GetBoolean(),
            NumberSet.Parse(e.GetProperty("sectors").GetString()!),
            Hex(e, "sectorBits"),
            Hex(e, "lineFlags"),
            Hex(e, "thingFlags"),
            NumberSet.Parse(e.GetProperty("things").GetString()!),
            e.TryGetProperty("exMyLacks", out var x) ? NumberSet.Parse(x.GetString()!) : null);
    }
}
