using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Cli.Commands;

/// <summary>The receipt for the map layer: every map read and written back, checked lump by lump.</summary>
internal sealed class RoundTripCommand : ICommand
{
    public string Name => "roundtrip";

    public string Summary => "Check that every map in a file reads and writes back unchanged.";

    public string Usage => """
        Usage: wadsmith-cli roundtrip FILE [--game=ID]

        Reads every map and writes it back three ways, reporting each map that changes:
          classic maps   through their own format (byte for byte), through WadSmith's map model
                         (byte for byte), and out to UDMF text and back (same meaning);
          UDMF maps      out to text and back (same text).

          --game=ID   The base game whose map rules apply (doom, heretic, hexen, strife …).
                      Normally worked out from the file; add-on WADs default to doom.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var gameId = args.Option("game");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("roundtrip takes one file or folder.");

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

        string ns;
        if (gameId is not null)
        {
            ns = GameCatalog.Get(gameId)?.MapNamespace ?? throw new UsageException($"There is no game called \"{gameId}\". Known: {string.Join(", ", GameCatalog.All.Select(g => g.Id))}.");
        }
        else
        {
            var game = GameCatalog.Identify(container);
            ns = game?.MapNamespace ?? "doom";
            if (game is null) output.WriteLine("Not a base game; using Doom's map rules (pass --game=ID to change).");
        }

        var maps = MapLocator.Find(container);
        var bad = 0;
        foreach (var m in maps)
        {
            string? problem;
            try
            {
                problem = Check(m, ns);
            }
            catch (CorruptDataException ex)
            {
                problem = ex.Message;
            }

            output.WriteLine($"{m.Name,-8} {InspectCommand.FormatName(m.Format),-13} {problem ?? "ok"}");
            if (problem is not null) bad++;
        }

        output.WriteLine(bad == 0 ? $"All {maps.Count} map(s) round-trip." : $"{bad} of {maps.Count} map(s) changed.");
        return bad == 0 ? ExitCode.Ok : ExitCode.Failed;
    }

    private static string? Check(MapLumps m, string ns)
    {
        if (m.Format == MapFormat.Udmf)
        {
            var model = MapConversion.Read(m, ns);
            var t1 = UdmfText.WriteString(MapConversion.ToUdmf(model));
            var t2 = UdmfText.WriteString(MapConversion.ToUdmf(MapConversion.FromUdmf(m.Name, UdmfText.Parse(t1))));
            return t1 == t2 ? null : "reads back differently from its own UDMF text";
        }

        var classic = ClassicMap.Read(m);
        var direct = classic.WriteLumps();
        var differs = FirstDifference(m, direct);
        if (differs is not null) return $"{differs} changes when written back in its own format";

        var model1 = MapConversion.FromClassic(classic, MapConversion.ClassicNamespace(m.Format, ns == "hexen" ? "hexen" : ns));
        differs = FirstDifference(m, MapConversion.ToClassic(model1, m.Format).WriteLumps());
        if (differs is not null) return $"{differs} changes through the map model";

        var text1 = UdmfText.WriteString(MapConversion.ToUdmf(model1));
        var reread = MapConversion.FromUdmf(m.Name, UdmfText.Parse(text1));
        var text2 = UdmfText.WriteString(MapConversion.ToUdmf(MapConversion.FromClassic(MapConversion.ToClassic(reread, m.Format), model1.Namespace)));
        return text1 == text2 ? null : "means something else after UDMF and back";
    }

    private static string? FirstDifference(MapLumps original, IReadOnlyList<(string Name, byte[] Data)> written)
    {
        for (var i = 0; i < original.Lumps.Count; i++)
        {
            if (i >= written.Count || !original.Lumps[i].Data.AsSpan().SequenceEqual(written[i].Data)) return original.Lumps[i].Name;
        }

        return null;
    }
}
