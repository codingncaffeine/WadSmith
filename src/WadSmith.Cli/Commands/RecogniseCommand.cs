using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Cli.Commands;

/// <summary>Lists what the editor recognises in every map of a file, and for Doom-format maps how well (precision and recall).</summary>
internal sealed class RecogniseCommand : ICommand
{
    public string Name => "recognise";

    public string Summary => "List the doors, lifts, windows, secrets, switches, teleporters and exits the editor finds in each map.";

    public string Usage => """
        Usage: wadsmith-cli recognise FILE [--map=NAME] [--engine-data=gzdoom.pk3]

        Reads every map (or one) as the editor opens it, classic maps translated as GZDoom reads them,
        and counts what the editor recognises. For Doom-format maps the control is the map as the game
        shipped it: the line and area types that make each kind (windows have none). Prints each map's
        counts and the totals with precision (how much found is right) and recall (how much there is,
        found). Fails when either is below 100 % for any kind.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var only = args.Option("map");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("recognise takes one file.");
        if (enginePath is null)
        {
            error.WriteLine("No engine data file (uzdoom.pk3 or gzdoom.pk3) was found; install UZDoom or GZDoom, or pass --engine-data=PATH.");
            return ExitCode.Failed;
        }

        try
        {
            var container = ResourceContainer.Open(args.Positional[0]);
            var ns = GameCatalog.Identify(container)?.MapNamespace ?? "doom";
            var engine = new ResourceStack([ResourceContainer.Open(enginePath)]);
            var totals = Recognition.Kinds.ToDictionary(k => k, _ => (Found: 0, Expected: 0, Right: 0));
            var controlled = false;
            foreach (var lumps in MapLocator.Find(container).Where(m => only is null || LumpName.Comparer.Equals(m.Name, only)))
            {
                var model = MapConversion.Read(lumps, ns);
                var doom = lumps.Format == MapFormat.Doom;
                var xlat = doom ? XlatTranslator.Load(engine, XlatTranslator.PathFor(model.Namespace)) : null;
                var map = EditorMap.FromModel(MapTranslation.ToZDoom(model, xlat, []));
                var found = Recognition.Find(map, MapGeometry.Build(map.Frame));
                var expected = doom ? Recognition.Expected(model) : null;
                controlled |= doom;
                var cells = Recognition.Kinds.Select(k =>
                {
                    var f = found.ByKind[k];
                    if (expected is null || k == "windows") return string.Create(CultureInfo.InvariantCulture, $"{k} {f.Count}");
                    var e = expected.ByKind[k];
                    var right = f.Count(e.Contains);
                    var t = totals[k];
                    totals[k] = (t.Found + f.Count, t.Expected + e.Count, t.Right + right);
                    return string.Create(CultureInfo.InvariantCulture, $"{k} {f.Count}{(f.Count == e.Count && right == e.Count ? "" : $" ({e.Count} marked, {right} right)")}");
                });
                output.WriteLine($"{lumps.Name,-8} {string.Join("  ", cells)}");
            }

            if (!controlled) return ExitCode.Ok;
            var perfect = true;
            output.WriteLine("totals, against the games' own marks:");
            foreach (var k in Recognition.Kinds.Where(k => k != "windows"))
            {
                var (f, e, r) = totals[k];
                var precision = f == 0 ? 1 : (double)r / f;
                var recall = e == 0 ? 1 : (double)r / e;
                perfect &= precision == 1 && recall == 1;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {k,-12} {f,6} found, {e,6} marked: precision {precision:P1}, recall {recall:P1}"));
            }

            return perfect ? ExitCode.Ok : ExitCode.Failed;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
