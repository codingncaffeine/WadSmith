using System.Runtime.CompilerServices;
using WadSmith.Core.Exporting;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Translation;

/// <summary>A line or sector with no classic equivalent: which, and why, in the words Problems shows.</summary>
public sealed record NotClassic(string What, int Index, string Why);

/// <summary>
/// The way back from GZDoom's actions to a classic map's line and sector types (6b): the engine's own translation table
/// (xlat/doom.txt, heretic, strife) read backwards. A line whose action, arguments and activation are exactly what some
/// classic type means gets that type, with the tag the type takes; a sector whose action is what a classic type means
/// gets that type. With a port's rules (Phase 12) only the types that port understands are chosen — Boom's generalized
/// line types and sector bits among them when it has them. What has no classic equivalent is reported, so the editor
/// can grey it and Problems can list it, and is written as a plain line or sector so the file stays whole.
/// </summary>
public static class ClassicTranslation
{
    /// <summary>Door_LockedRaise: monsters cannot open it whatever its line says.</summary>
    private const int LockedDoor = 13;

    /// <summary>Boom's generalized sector bits (classic 0x3FE0) as GZDoom keeps them, three places higher.</summary>
    private const int GeneralizedSectorBits = 0x3FE0 << 3;

    /// <summary>Boom's secret bit in a classic sector type.</summary>
    private const int BoomSecret = 0x80;

    /// <summary>The UDMF activation fields a line can carry, as the forward translation writes them.</summary>
    private static readonly string[] ActivationNames = ["playercross", "playeruse", "passuse", "monstercross", "impact", "playerpush", "missilecross", "anycross"];

    /// <summary>Each table's generalized line types by what they mean, built once on first use.</summary>
    private static readonly ConditionalWeakTable<XlatTranslator, Dictionary<string, (int Type, bool TagInFirst)>> Generalized = new();

    /// <summary>
    /// The classic line type for a line in the zdoom namespace, with the tag the classic line takes: the lowest type whose
    /// entry yields the line's action, arguments (the tag where the entry says TAG) and activation. A plain line is type 0
    /// with its id as its tag. Null when no type means what the line does.
    /// </summary>
    public static (int Type, int Tag)? TypeOf(MapLine line, XlatTranslator xlat, PortRules? rules = null) => TypeOf(line.Special, line.Args, line.Flag, line.Id, xlat, rules);

    /// <summary>
    /// The classic line type for a line given as its parts (see <see cref="TypeOf(MapLine, XlatTranslator, PortRules?)"/>),
    /// for the checks that read frames. With <paramref name="rules"/>, only a type the port understands.
    /// </summary>
    public static (int Type, int Tag)? TypeOf(int special, IReadOnlyList<int> args, Func<string, bool> flag, int id, XlatTranslator xlat, PortRules? rules = null)
    {
        if (special == 0) return (0, Math.Max(0, id));
        var activation = new HashSet<string>(ActivationNames.Where(flag), StringComparer.Ordinal);
        var repeats = flag("repeatspecial");
        var monsters = flag("monsteractivate") || flag("monsteruse") || flag("monstercross");

        // Crossed by players and monsters is how GZDoom spells a classic walk-over line monsters set off too (the engine
        // reads that type as players crossing with monsteractivate): the monsters are the type's MONST, not its activation.
        if (activation.Contains("playercross")) activation.Remove("monstercross");

        // Using a wall through the wall in front of it is a line flag in a classic map (Boom's 0x200), not part of the type.
        activation.Remove("passuse");
        foreach (var (type, entry) in xlat.Lines.OrderBy(e => e.Key))
        {
            if (entry.Special != special) continue;
            if (rules is not null && !rules.Lines.Contains(type)) continue;
            if (((entry.Flags & 1) != 0) != repeats) continue;
            // Monsters never open a locked door, so whether one says they may does not decide its type.
            if (((entry.Flags & 0x10) != 0) != monsters && entry.Special != LockedDoor) continue;
            if (!activation.SetEquals(MapTranslation.ActivationFieldsOf(entry.Flags))) continue;
            var tag = -1;
            var fits = true;
            for (var a = 0; a < 5 && fits; a++)
            {
                var expr = a < entry.Args.Length ? entry.Args[a] : null;
                var value = a < args.Count ? args[a] : 0;
                if (expr is null) fits = value == 0;
                else if (expr.UsesTag)
                {
                    // Every TAG argument carries the same tag; the entry's shape (tag, -tag, tag + k) is honoured by trying the value as the tag.
                    var candidate = value;
                    if (tag >= 0 && candidate != tag) fits = false;
                    else if (expr.Eval(candidate) != value) fits = false;
                    else tag = candidate;
                }
                else fits = expr.Eval(0) == value;
            }

            if (fits) return (type, Math.Max(0, tag < 0 ? (id > 0 ? id : 0) : tag));
        }

        if (rules is { Generalized: true } && GeneralizedIndex(xlat).TryGetValue(Key(special, args, flag), out var g))
        {
            return (g.Type, Math.Max(0, g.TagInFirst ? (args.Count > 0 ? args[0] : 0) : id));
        }

        return null;
    }

    /// <summary>What a line does, as the generalized index keys it: the action, its arguments after the tag, and the activation, repeat and monster fields.</summary>
    private static string Key(int special, IReadOnlyList<int> args, Func<string, bool> flag)
    {
        var fields = ActivationNames.Where(flag).Order(StringComparer.Ordinal).ToList();
        if (flag("repeatspecial")) fields.Add("repeat");
        if (flag("monsteractivate") || flag("monsteruse")) fields.Add("monsters");
        int Arg(int i) => i < args.Count ? args[i] : 0;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{special}|{Arg(1)},{Arg(2)},{Arg(3)},{Arg(4)}|{string.Join(',', fields)}");
    }

    /// <summary>Every Boom generalized type of the table read forward with a tag, keyed by what it means; the lowest type wins.</summary>
    private static Dictionary<string, (int Type, bool TagInFirst)> GeneralizedIndex(XlatTranslator xlat) => Generalized.GetValue(xlat, x =>
    {
        const int Probe = 1;
        var index = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        foreach (var block in x.Boom.OrderBy(b => b.First))
        {
            for (var type = block.First; type <= block.Last; type++)
            {
                var line = new MapLine();
                MapTranslation.TranslateDoomLine(line, new MapLine { Special = type, Id = Probe, SourceFlags = 0 }, FlagLayouts.DoomLine, x, [], 0);
                if (line.Special == 0) continue;
                index.TryAdd(Key(line.Special, line.Args, line.Flag), (type, line.Args[0] == Probe));
            }
        }

        return index;
    });

    /// <summary>The classic sector type for a sector's action in the zdoom namespace, or null when no type means it; 0 for none.</summary>
    public static int? SectorTypeOf(MapSector sector, XlatTranslator xlat, PortRules? rules = null) => SectorTypeOf(sector.Special, IsSecret(sector), xlat, rules);

    /// <summary>
    /// The classic sector type for a sector given as its action and whether it is a secret (see
    /// <see cref="SectorTypeOf(MapSector, XlatTranslator, PortRules?)"/>). With <paramref name="rules"/>, only a base type
    /// the port understands, and Boom's bits (damage, secret, friction, wind, MBF21's two) only when it has them.
    /// </summary>
    public static int? SectorTypeOf(int special, bool secret, XlatTranslator xlat, PortRules? rules = null)
    {
        if (rules is null)
        {
            if (special == 0) return secret ? 9 : 0;
            if (secret) return null;
            foreach (var (type, entry) in xlat.Sectors.OrderBy(e => e.Key))
            {
                if (entry.NewType == special) return type;
            }

            return null;
        }

        var bits = (special & GeneralizedSectorBits) >> 3;
        var low = special & ~GeneralizedSectorBits;
        if (secret) bits |= BoomSecret;
        if ((bits & ~rules.SectorBits) != 0)
        {
            // Without Boom's bits a secret is only type 9, which has no other action.
            return bits == BoomSecret && low == 0 && rules.Sectors.Contains(9) ? 9 : null;
        }

        if (low == 0) return bits;
        foreach (var (type, entry) in xlat.Sectors.OrderBy(e => e.Key))
        {
            if (entry.NewType != low || !rules.Sectors.Contains(type)) continue;
            if (bits != 0 && (!entry.BitmaskAllowed || (type & ~0x1F) != 0)) continue;
            return type | bits;
        }

        return null;
    }

    private static bool IsSecret(MapSector sector) => sector.Extra.TryGet("secret", out var v) && v.AsBool();

    /// <summary>
    /// A map as <paramref name="project"/>'s file takes it: as it is for a GZDoom target, back through the engine's table
    /// for a classic one (what has no classic type goes into <paramref name="report"/> and is written plain). The engine's
    /// data (<paramref name="engineData"/>, gzdoom.pk3) holds the table; a classic target without it cannot be saved.
    /// </summary>
    public static MapModel ForProject(Projects.Project project, MapModel model, string? engineData, List<NotClassic>? report = null)
    {
        if (!project.ClassicFormat) return model;
        if (engineData is null || !File.Exists(engineData)) throw new CorruptDataException("A map kept in the classic format needs GZDoom installed: WadSmith reads the engine's own table of what each classic line type does.");
        var ns = project.ClassicNamespace;
        var xlat = XlatTranslator.Load(new Containers.ResourceStack([Containers.ResourceContainer.Open(engineData)]), XlatTranslator.PathFor(ns));
        return ToClassicNamespace(model, xlat, ns, report ?? [], project.PortRules);
    }

    /// <summary>
    /// The map in <paramref name="ns"/> (doom, heretic, hexen or strife), ready for the classic writer: every line's and
    /// sector's classic type, its tag as the line's and sector's id, the actions' arguments and GZDoom's activation fields
    /// gone (a Hexen-format map keeps its actions and arguments, which are Hexen's own). What has no classic equivalent —
    /// for <paramref name="rules"/>' port when given — is listed in <paramref name="report"/> and written plain.
    /// </summary>
    public static MapModel ToClassicNamespace(MapModel zdoom, XlatTranslator? xlat, string ns, List<NotClassic> report, PortRules? rules = null)
    {
        if (zdoom.Namespace != "zdoom") throw new ArgumentException("A map in GZDoom's namespace goes back to a classic one.", nameof(zdoom));
        if (ns == "hexen") return ToHexen(zdoom, report, rules);
        if (xlat is null) throw new ArgumentException("A Doom-format map needs the engine's translator.", nameof(xlat));
        var m = MapTranslation.Copy(zdoom, ns);
        for (var i = 0; i < m.Lines.Count; i++)
        {
            var line = m.Lines[i];
            var original = zdoom.Lines[i];
            var found = TypeOf(original, xlat, rules);
            if (found is not { } t)
            {
                report.Add(new NotClassic("wall", i, $"its action {ActionName(original.Special)} with these settings has no classic type"));
                t = (0, Math.Max(0, original.Id));
            }

            line.Special = t.Type;
            line.Id = t.Tag;
            Array.Clear(line.Args);
            line.SourceFlags = null;
            // Using a wall through the one in front stays: the classic writer keeps it as the line flag Boom reads (0x200).
            foreach (var name in ActivationNames.Where(n => n != "passuse")) line.Extra.Remove(name);
            foreach (var name in new[] { "repeatspecial", "monsteractivate", "monsteruse", "firstsideonly" }) line.Extra.Remove(name);
        }

        for (var i = 0; i < m.Sectors.Count; i++)
        {
            var sector = m.Sectors[i];
            var found = SectorTypeOf(zdoom.Sectors[i], xlat, rules);
            if (found is not { } t)
            {
                report.Add(new NotClassic("room", i, IsSecret(zdoom.Sectors[i]) ? "a secret with another action has no classic type (vanilla keeps the secret in the type)" : $"its action {zdoom.Sectors[i].Special} has no classic type"));
                t = IsSecret(zdoom.Sectors[i]) ? 9 : 0;
            }

            sector.Special = t;
            sector.Extra.Remove("secret");
            sector.Extra.Remove("dropactors");
        }

        return m;
    }

    /// <summary>
    /// The map in Hexen's format: GZDoom's actions are Hexen's own numbers, so a line, sector or object keeps its action
    /// when the port has it (every action when no port is given) and loses it otherwise; arguments are bytes.
    /// </summary>
    private static MapModel ToHexen(MapModel zdoom, List<NotClassic> report, PortRules? rules)
    {
        var m = MapTranslation.Copy(zdoom, "hexen");
        static bool Bytes(int[] args) => args.All(a => a is >= 0 and <= 255);
        for (var i = 0; i < m.Lines.Count; i++)
        {
            var line = m.Lines[i];
            if (line.Special == 0) continue;
            if ((rules is not null && !rules.Lines.Contains(line.Special)) || !Bytes(line.Args))
            {
                report.Add(new NotClassic("wall", i, $"its action {ActionName(line.Special)} with these settings is not in Hexen's format"));
                line.Special = 0;
                Array.Clear(line.Args);
            }
        }

        for (var i = 0; i < m.Sectors.Count; i++)
        {
            var sector = m.Sectors[i];
            var special = sector.Special | (IsSecret(sector) ? 1024 : 0);
            if (special != 0 && rules is not null && !rules.Sectors.Contains(special))
            {
                report.Add(new NotClassic("room", i, $"its action {special} is not in Hexen's format"));
                special = 0;
            }

            sector.Special = special;
            sector.Extra.Remove("secret");
            sector.Extra.Remove("dropactors");
        }

        for (var i = 0; i < m.Things.Count; i++)
        {
            var thing = m.Things[i];
            if (thing.Special == 0) continue;
            if ((rules is not null && !rules.Lines.Contains(thing.Special)) || !Bytes(thing.Args))
            {
                report.Add(new NotClassic("thing", i, $"its action {ActionName(thing.Special)} with these settings is not in Hexen's format"));
                thing.Special = 0;
                Array.Clear(thing.Args);
            }
        }

        return m;
    }

    private static string ActionName(int special) => ActionCatalog.Get(special)?.Name ?? special.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
