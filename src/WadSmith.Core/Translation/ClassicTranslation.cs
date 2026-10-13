using WadSmith.Core.Games;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Translation;

/// <summary>A line or sector with no classic equivalent: which, and why, in the words Problems shows.</summary>
public sealed record NotClassic(string What, int Index, string Why);

/// <summary>
/// The way back from GZDoom's actions to a classic map's line and sector types (6b): the engine's own translation table
/// (xlat/doom.txt, heretic, strife) read backwards. A line whose action, arguments and activation are exactly what some
/// classic type means gets that type, with the tag the type takes; a sector whose action is what a classic type means
/// gets that type. What has no classic equivalent is reported, so the editor can grey it and Problems can list it, and
/// is written as a plain line or sector so the file stays whole.
/// </summary>
public static class ClassicTranslation
{
    /// <summary>Door_LockedRaise: monsters cannot open it whatever its line says.</summary>
    private const int LockedDoor = 13;

    /// <summary>The UDMF activation fields a line can carry, as the forward translation writes them.</summary>
    private static readonly string[] ActivationNames = ["playercross", "playeruse", "passuse", "monstercross", "impact", "playerpush", "missilecross", "anycross"];

    /// <summary>
    /// The classic line type for a line in the zdoom namespace, with the tag the classic line takes: the lowest type whose
    /// entry yields the line's action, arguments (the tag where the entry says TAG) and activation. A plain line is type 0
    /// with its id as its tag. Null when no type means what the line does.
    /// </summary>
    public static (int Type, int Tag)? TypeOf(MapLine line, XlatTranslator xlat) => TypeOf(line.Special, line.Args, line.Flag, line.Id, xlat);

    /// <summary>The classic line type for a line given as its parts (see <see cref="TypeOf(MapLine, XlatTranslator)"/>), for the checks that read frames.</summary>
    public static (int Type, int Tag)? TypeOf(int special, IReadOnlyList<int> args, Func<string, bool> flag, int id, XlatTranslator xlat)
    {
        if (special == 0) return (0, Math.Max(0, id));
        var activation = new HashSet<string>(ActivationNames.Where(flag), StringComparer.Ordinal);
        var repeats = flag("repeatspecial");
        var monsters = flag("monsteractivate") || flag("monsteruse") || flag("monstercross");
        foreach (var (type, entry) in xlat.Lines.OrderBy(e => e.Key))
        {
            if (entry.Special != special) continue;
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

        return null;
    }

    /// <summary>The classic sector type for a sector's action in the zdoom namespace, or null when no type means it; 0 for none.</summary>
    public static int? SectorTypeOf(MapSector sector, XlatTranslator xlat) => SectorTypeOf(sector.Special, IsSecret(sector), xlat);

    /// <summary>The classic sector type for a sector given as its action and whether it is a secret (see <see cref="SectorTypeOf(MapSector, XlatTranslator)"/>).</summary>
    public static int? SectorTypeOf(int special, bool secret, XlatTranslator xlat)
    {
        if (special == 0) return secret ? 9 : 0;
        if (secret) return null;
        foreach (var (type, entry) in xlat.Sectors.OrderBy(e => e.Key))
        {
            if (entry.NewType == special) return type;
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
        return ToClassicNamespace(model, xlat, ns, report ?? []);
    }

    /// <summary>
    /// The map in <paramref name="ns"/> (doom, heretic or strife), ready for the classic writer: every line's and sector's
    /// classic type, its tag as the line's and sector's id, the actions' arguments and GZDoom's activation fields gone.
    /// What has no classic equivalent is listed in <paramref name="report"/> and written plain.
    /// </summary>
    public static MapModel ToClassicNamespace(MapModel zdoom, XlatTranslator xlat, string ns, List<NotClassic> report)
    {
        if (zdoom.Namespace != "zdoom") throw new ArgumentException("A map in GZDoom's namespace goes back to a classic one.", nameof(zdoom));
        var m = MapTranslation.Copy(zdoom, ns);
        for (var i = 0; i < m.Lines.Count; i++)
        {
            var line = m.Lines[i];
            var original = zdoom.Lines[i];
            var found = TypeOf(original, xlat);
            if (found is not { } t)
            {
                report.Add(new NotClassic("wall", i, $"its action {ActionCatalog.Get(original.Special)?.Name ?? original.Special.ToString(System.Globalization.CultureInfo.InvariantCulture)} with these settings has no classic type"));
                t = (0, Math.Max(0, original.Id));
            }

            line.Special = t.Type;
            line.Id = t.Tag;
            Array.Clear(line.Args);
            line.SourceFlags = null;
            foreach (var name in ActivationNames) line.Extra.Remove(name);
            foreach (var name in new[] { "repeatspecial", "monsteractivate", "monsteruse", "firstsideonly" }) line.Extra.Remove(name);
        }

        for (var i = 0; i < m.Sectors.Count; i++)
        {
            var sector = m.Sectors[i];
            var found = SectorTypeOf(zdoom.Sectors[i], xlat);
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
}
