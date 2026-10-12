using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;

namespace WadSmith.Cli.Commands;

internal sealed class TranslateCommand : ICommand
{
    public string Name => "translate";

    public string Summary => "Convert a classic map to GZDoom's UDMF format, optionally checking it against the engine.";

    public string Usage => """
        Usage: wadsmith-cli translate FILE --map=NAME [--out=FILE.wad] [--compare=ENGINE.log]
                                      [--game=ID] [--engine-data=gzdoom.pk3]

        Converts a Doom- or Hexen-format map to UDMF in GZDoom's own namespace, giving every line
        and sector the meaning the engine gives it, using the engine's own translation tables.

          --map=NAME          The map to convert (MAP01, E1M1 …).
          --out=FILE.wad      Write the converted map as a WAD.
          --repairs           Also apply GZDoom's own repairs for this original map, as a remix does.
          --compare=LOG       Compare with an engine dump (tools/engine/dump-map.sh) and list
                              every difference; fails when there is one.
          --game=ID           The base game whose rules apply; normally worked out from the file.
          --engine-data=PATH  The engine's gzdoom.pk3; normally found in the usual places.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var mapName = args.Option("map") ?? throw new UsageException("translate needs --map=NAME.");
        var outPath = args.Option("out");
        var comparePath = args.Option("compare");
        var withRepairs = args.Flag("repairs");
        var gameId = args.Option("game");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("translate takes one file.");
        if (enginePath is null)
        {
            error.WriteLine("GZDoom's data file (gzdoom.pk3) was not found; install GZDoom or pass --engine-data=PATH.");
            return ExitCode.Failed;
        }

        try
        {
            var container = ResourceContainer.Open(args.Positional[0]);
            var game = gameId is null ? GameCatalog.Identify(container) : GameCatalog.Get(gameId) ?? throw new UsageException($"There is no game called \"{gameId}\".");
            var ns = game?.MapNamespace ?? "doom";
            var lumps = MapLocator.Find(container).FirstOrDefault(m => LumpName.Comparer.Equals(m.Name, mapName));
            if (lumps is null)
            {
                error.WriteLine($"{container.DisplayName} has no map called {mapName}.");
                return ExitCode.Failed;
            }

            var model = MapConversion.Read(lumps, ns);
            var xlat = lumps.Format == MapFormat.Doom ? XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(enginePath)]), XlatTranslator.PathFor(model.Namespace)) : null;
            var notes = new List<string>();
            var zdoom = MapTranslation.ToZDoom(model, xlat, notes);
            output.WriteLine($"{lumps.Name}: {InspectCommand.FormatName(lumps.Format)} ({model.Namespace}) to UDMF (zdoom), {zdoom.Lines.Count} lines, {zdoom.Sectors.Count} sectors.");
            var engineFiles = new ResourceStack([ResourceContainer.Open(enginePath)]);
            var compat = EngineCompatibility.Load(engineFiles);
            var checksum = MapChecksum.Compute(lumps);
            var patched = (compat.HasRepairs(checksum) && !withRepairs) || compat.HasBehaviourSwitches(checksum);
            if (withRepairs && CompatibilityRepairs.Load(engineFiles)?.Apply(zdoom, checksum, notes) == true) output.WriteLine("  GZDoom's own repairs for this map were applied.");
            if (compat.HasRepairs(checksum) && !withRepairs) output.WriteLine($"  Note: GZDoom repairs this map ({checksum}) when it loads the original; the translation does not include those repairs.");
            if (compat.HasBehaviourSwitches(checksum)) output.WriteLine($"  Note: GZDoom switches some behaviour for this map ({checksum}); the translation does not carry those switches.");
            foreach (var n in notes) output.WriteLine($"  Note: {n}");

            if (outPath is not null)
            {
                var wadLumps = new List<(string, ReadOnlyMemory<byte>)> { (lumps.Name, Array.Empty<byte>()) };
                wadLumps.AddRange(MapConversion.ToUdmfLumps(zdoom).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
                File.WriteAllBytes(outPath, WadWriter.Write(WadKind.Pwad, wadLumps));
                output.WriteLine($"  Written to {outPath}.");
            }

            if (comparePath is null) return ExitCode.Ok;
            var diffs = EngineDump.Parse(File.ReadAllText(comparePath)).Compare(zdoom, EngineCompatibility.GameDefaultLineFlags(engineFiles, game?.Engine ?? "doom"));
            foreach (var d in diffs.Take(60)) output.WriteLine($"  {d}");
            if (diffs.Count > 60) output.WriteLine($"  … and {diffs.Count - 60} more.");
            if (diffs.Count == 0) output.WriteLine("  Matches the engine exactly.");
            else if (patched) output.WriteLine($"  {diffs.Count} difference(s) from the engine, on a map the engine patches.");
            else output.WriteLine($"  {diffs.Count} difference(s) from the engine.");
            return diffs.Count == 0 ? ExitCode.Ok : patched ? 3 : ExitCode.Failed;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
