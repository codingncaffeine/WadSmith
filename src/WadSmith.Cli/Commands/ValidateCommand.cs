using WadSmith.Core;
using WadSmith.Core.Checks;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;

namespace WadSmith.Cli.Commands;

internal sealed class ValidateCommand : ICommand
{
    public string Name => "validate";

    public string Summary => "Check every map in a file for broken references and other structural faults.";

    public string Usage => """
        Usage: wadsmith-cli validate FILE [--warnings]

        Lists every structural fault (a line ending at a missing corner, a side in a missing room,
        a line with no front side). Fails when there is one.

          --warnings   Also list things that load but are probably not meant (lines with no
                       length, two-sided lines not marked two-sided).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var warnings = args.Flag("warnings");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("validate takes one file or folder.");
        try
        {
            var container = ResourceContainer.Open(args.Positional[0]);
            var ns = GameCatalog.Identify(container)?.MapNamespace ?? "doom";
            var errors = 0;
            foreach (var lumps in MapLocator.Find(container))
            {
                var problems = MapValidator.Check(MapConversion.Read(lumps, ns));
                var shown = problems.Where(p => warnings || p.Severity == Severity.Error).ToList();
                errors += problems.Count(p => p.Severity == Severity.Error);
                output.WriteLine($"{lumps.Name,-8} {(shown.Count == 0 ? "ok" : $"{shown.Count} problem(s)")}");
                foreach (var p in shown.Take(20)) output.WriteLine($"    {p.Severity}: {p.Message}");
                if (shown.Count > 20) output.WriteLine($"    … and {shown.Count - 20} more.");
            }

            return errors == 0 ? ExitCode.Ok : ExitCode.Failed;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
