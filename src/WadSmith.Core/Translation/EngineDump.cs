using System.Globalization;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Translation;

/// <summary>
/// What GZDoom itself holds for a loaded map, as printed by the dump add-on in tools/engine:
/// per line its action, arguments, activation bits, flag words and alpha; per sector its type; and
/// every line id. Used to check WadSmith's translation against the engine.
/// </summary>
public sealed class EngineDump
{
    public sealed record LineState(int Special, int[] Args, int Activation, int Flags, int Flags2, double Alpha);

    public List<LineState> Lines { get; } = [];

    public sealed record SectorState(int Special, int Flags, int Damage);

    public List<SectorState> Sectors { get; } = [];

    public SortedSet<(int Id, int Line)> LineIds { get; } = [];

    public static EngineDump Parse(string log)
    {
        var d = new EngineDump();
        var inv = CultureInfo.InvariantCulture;
        var inside = false;
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("WSDUMP BEGIN", StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (line == "WSDUMP END") break;
            if (!inside) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (p[0])
            {
                case "WSL":
                    d.Lines.Add(new LineState(int.Parse(p[2], inv), [int.Parse(p[3], inv), int.Parse(p[4], inv), int.Parse(p[5], inv), int.Parse(p[6], inv), int.Parse(p[7], inv)],
                        int.Parse(p[8], inv), int.Parse(p[9], inv), int.Parse(p[10], inv), double.Parse(p[11], inv)));
                    break;
                case "WSS":
                    d.Sectors.Add(new SectorState(int.Parse(p[2], inv), p.Length > 3 ? int.Parse(p[3], inv) : 0, p.Length > 4 ? int.Parse(p[4], inv) : 0));
                    break;
                case "WSI":
                    d.LineIds.Add((int.Parse(p[1], inv), int.Parse(p[2], inv)));
                    break;
            }
        }

        if (!inside) throw new CorruptDataException("The engine log holds no map dump.");
        return d;
    }

    // GZDoom's activation bits for the UDMF trigger fields.
    private static readonly (string Field, int Bit)[] Activation =
    [
        ("playercross", 1), ("playeruse", 2), ("monstercross", 4), ("impact", 8), ("playerpush", 16), ("missilecross", 32),
        ("anycross", 128), ("monsteruse", 256), ("monsterpush", 512), ("playeruseback", 1024), ("damagespecial", 2048), ("deathspecial", 4096),
    ];

    private static readonly (string Field, int Bit)[] Flags =
    [
        ("blocking", 0x1), ("blockmonsters", 0x2), ("twosided", 0x4), ("dontpegtop", 0x8), ("dontpegbottom", 0x10), ("secret", 0x20),
        ("blocksound", 0x40), ("dontdraw", 0x80), ("mapped", 0x100), ("repeatspecial", 0x200), ("monsteractivate", 0x2000), ("blockplayers", 0x4000),
        ("blockeverything", 0x8000), ("zoneboundary", 0x10000), ("jumpover", 0x20000), ("blockfloaters", 0x40000), ("clipmidtex", 0x80000),
        ("wrapmidtex", 0x100000), ("midtex3d", 0x200000), ("checkswitchrange", 0x400000), ("firstsideonly", 0x800000), ("blockprojectiles", 0x1000000),
        ("blockuse", 0x2000000), ("blocksight", 0x4000000), ("blockhitscan", 0x8000000),
    ];

    /// <summary>The activation bits GZDoom gives a UDMF line with these trigger fields (use plus pass-use is its use-through bit).</summary>
    public static int ActivationOf(MapLine l)
    {
        var bits = 0;
        foreach (var (f, b) in Activation)
        {
            if (l.Flag(f)) bits |= b;
        }

        if (l.Flag("passuse") && (bits & 2) != 0) bits = (bits & ~2) | 64;
        return bits;
    }

    public static int FlagsOf(MapLine l)
    {
        var bits = 0;
        foreach (var (f, b) in Flags)
        {
            if (l.Flag(f)) bits |= b;
        }

        return bits;
    }

    private const int SecfFriction = 16, SecfPush = 32, SecfSecret = unchecked((int)0x80000000);

    // Sector types GZDoom turns into settings at load time and then clears (its InitSectorSpecial),
    // with the damage each sets; null damage means it sets none.
    private static readonly Dictionary<int, int?> ConsumedTypes = new()
    {
        [68] = 20, [69] = 10, [71] = 5, [74] = null, [75] = 20, [78] = null, [79] = null, [80] = 20, [82] = 5, [83] = 8,
        [85] = 4, [104] = 5, [105] = 2, [115] = 1000000, [116] = 4, [195] = null, [196] = -1, [200] = null,
    };

    /// <summary>
    /// What the engine holds for a translated sector once the map has loaded: Boom's flag bits
    /// become sector flags and damage, the type keeps its low byte, and the types the engine turns
    /// into settings are cleared. Mirrors GZDoom 4.14's InitSectorSpecial for non-MBF21 maps.
    /// </summary>
    internal static SectorState AfterLoad(MapSector s)
    {
        var special = s.Special;
        var flags = 0;
        var damage = 0;
        if (s.Extra.TryGet("secret", out var secret) && secret.AsBool()) flags |= SecfSecret;
        if ((special & 0x0400) != 0) flags |= SecfSecret;
        if ((special & 0x0800) != 0) flags |= SecfFriction;
        if ((special & 0x1000) != 0) flags |= SecfPush;
        damage = (special & 0x0300) switch { 0x100 => 5, 0x200 => 10, 0x300 => 20, _ => 0 };
        special &= 0xFF;
        if (special == 84) damage = 5;
        if (special == 79) flags |= SecfFriction;
        if (ConsumedTypes.TryGetValue(special, out var typeDamage))
        {
            if (typeDamage is { } d) damage = d;
            special = 0;
        }

        return new SectorState(special, flags, damage);
    }

    /// <summary>Every difference between a translated map and the engine's dump of the original, one sentence each.</summary>
    /// <param name="m">The translated map.</param>
    /// <param name="gameLineFlags">Line flags the game turns on for every line (see EngineCompatibility.GameDefaultLineFlags).</param>
    public IReadOnlyList<string> Compare(MapModel m, int gameLineFlags = 0)
    {
        var diffs = new List<string>();
        if (m.Lines.Count != Lines.Count) diffs.Add($"The map has {m.Lines.Count} lines; the engine loaded {Lines.Count}.");
        for (var i = 0; i < Math.Min(m.Lines.Count, Lines.Count); i++)
        {
            var ours = m.Lines[i];
            var eng = Lines[i];
            if (ours.Special != eng.Special) diffs.Add($"Line {i}: action {ours.Special}, engine {eng.Special}.");
            if (!ours.Args.SequenceEqual(eng.Args)) diffs.Add($"Line {i}: arguments {string.Join(",", ours.Args)}, engine {string.Join(",", eng.Args)}.");
            var act = ActivationOf(ours);
            if (eng.Special != 0 && act != eng.Activation) diffs.Add($"Line {i}: activation {act}, engine {eng.Activation}.");
            var flags = FlagsOf(ours) | gameLineFlags;
            if (flags != eng.Flags) diffs.Add($"Line {i}: flags 0x{flags:X}, engine 0x{eng.Flags:X}.");
            var flags2 = ours.Flag("blocklandmonsters") ? 1 : 0;
            if (flags2 != eng.Flags2) diffs.Add($"Line {i}: second flags {flags2}, engine {eng.Flags2}.");
            var alpha = ours.Extra.TryGet("alpha", out var a) ? a.AsFloat() : 1.0;
            if (Math.Abs(alpha - eng.Alpha) > 0.001) diffs.Add($"Line {i}: alpha {alpha}, engine {eng.Alpha}.");
        }

        if (m.Sectors.Count != Sectors.Count) diffs.Add($"The map has {m.Sectors.Count} sectors; the engine loaded {Sectors.Count}.");
        for (var i = 0; i < Math.Min(m.Sectors.Count, Sectors.Count); i++)
        {
            var expected = AfterLoad(m.Sectors[i]);
            var eng = Sectors[i];
            if (expected.Special != eng.Special) diffs.Add($"Sector {i}: type {expected.Special} after loading, engine {eng.Special}.");
            const int checkedFlags = SecfSecret | SecfFriction | SecfPush;
            if ((expected.Flags & checkedFlags) != (eng.Flags & checkedFlags)) diffs.Add($"Sector {i}: secret/friction/wind flags 0x{expected.Flags & checkedFlags:X}, engine 0x{eng.Flags & checkedFlags:X}.");
            if (expected.Damage != eng.Damage) diffs.Add($"Sector {i}: damage {expected.Damage}, engine {eng.Damage}.");
        }

        var ourIds = new SortedSet<(int Id, int Line)>(m.Lines.Select((l, i) => (Id: l.Id, Line: i)).Where(x => x.Id > 0));
        foreach (var x in ourIds.Except(LineIds)) diffs.Add($"Line {x.Line}: id {x.Id}, which the engine does not give it.");
        foreach (var x in LineIds.Except(ourIds)) diffs.Add($"Line {x.Line}: the engine gives it id {x.Id}, the translation does not.");
        return diffs;
    }
}
