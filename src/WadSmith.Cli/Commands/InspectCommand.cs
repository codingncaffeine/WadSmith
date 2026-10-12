using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;

namespace WadSmith.Cli.Commands;

internal sealed class InspectCommand : ICommand
{
    public string Name => "inspect";

    public string Summary => "Show what a WAD, PK3 or folder holds: game, maps, warnings.";

    public string Usage => """
        Usage: wadsmith-cli inspect FILE [--lumps]

        Shows the kind of file, the base game it is (judged by its lumps), every map with its
        format and size, and any problems found while reading it.

          --lumps   Also list every lump or file with its group and size.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var lumps = args.Flag("lumps");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("inspect takes one file or folder.");

        ResourceContainer container;
        try
        {
            container = ResourceContainer.Open(args.Positional[0]);
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }

        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(container.DisplayName);
        var kind = container switch
        {
            WadContainer w => $"WAD ({(w.Kind == WadKind.Iwad ? "IWAD" : "PWAD")} header)",
            ZipContainer => "PK3/ZIP archive",
            _ => "folder",
        };
        output.WriteLine(string.Create(inv, $"  Kind:      {kind}, {container.Entries.Count} entries"));

        var game = GameCatalog.Identify(container);
        output.WriteLine($"  Game:      {(game is null ? "not a base game" : game.Supported ? game.Title : game.Title + " (not supported: GZDoom does not run this game)")}");

        IReadOnlyList<MapLumps> maps;
        try
        {
            maps = game?.Supported == false ? [] : MapLocator.Find(container);
        }
        catch (CorruptDataException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }

        output.WriteLine(string.Create(inv, $"  Maps:      {maps.Count}"));
        var failed = false;
        foreach (var m in maps)
        {
            try
            {
                var model = MapConversion.Read(m, game?.MapNamespace ?? "doom");
                output.WriteLine(string.Create(inv, $"    {m.Name,-8} {FormatName(m.Format),-13} {model.Lines.Count,6} lines {model.Sectors.Count,5} sectors {model.Things.Count,5} things"));
            }
            catch (CorruptDataException ex)
            {
                output.WriteLine($"    {m.Name,-8} {FormatName(m.Format),-13} cannot be read: {ex.Message}");
                failed = true;
            }
        }

        output.WriteLine(container.Warnings.Count == 0 ? "  Warnings:  none" : "  Warnings:");
        foreach (var w in container.Warnings) output.WriteLine($"    {w}");

        if (lumps)
        {
            output.WriteLine("  Entries:");
            foreach (var e in container.Entries)
            {
                output.WriteLine(string.Create(inv, $"    {e.Index,5}  {e.FullPath,-40} {(e.IsMarker ? "marker" : e.Namespace.ToString()),-10} {e.Size,10}"));
            }
        }

        return failed || container.Warnings.Count > 0 ? ExitCode.Failed : ExitCode.Ok;
    }

    internal static string FormatName(MapFormat f) => f switch
    {
        MapFormat.Doom => "Doom format",
        MapFormat.Hexen => "Hexen format",
        _ => "UDMF",
    };
}
