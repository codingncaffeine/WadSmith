using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Preview;

namespace WadSmith.Cli.Commands;

internal sealed class Render2dCommand : ICommand
{
    public string Name => "render2d";

    public string Summary => "Draw a map from above in the automap's colours, as a PNG.";

    public string Usage => """
        Usage: wadsmith-cli render2d FILE --map=NAME --out=PICTURE.png [--size=PIXELS] [--things] [--openings]

          --size=PIXELS   Width and height of the picture (default 1024).
          --things        Mark every thing with a green dot.
          --openings      Also draw two-sided lines with no height change (grey).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var mapName = args.Option("map") ?? throw new UsageException("render2d needs --map=NAME.");
        var outPath = args.Option("out") ?? throw new UsageException("render2d needs --out=PICTURE.png.");
        var sizeText = args.Option("size");
        var things = args.Flag("things");
        var openings = args.Flag("openings");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("render2d takes one file.");
        var size = 1024;
        if (sizeText is not null && (!int.TryParse(sizeText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out size) || size is < 64 or > 16384))
            throw new UsageException("--size must be a whole number from 64 to 16384.");

        try
        {
            var container = ResourceContainer.Open(args.Positional[0]);
            var lumps = MapLocator.Find(container).FirstOrDefault(m => LumpName.Comparer.Equals(m.Name, mapName));
            if (lumps is null)
            {
                error.WriteLine($"{container.DisplayName} has no map called {mapName}.");
                return ExitCode.Failed;
            }

            var map = MapConversion.Read(lumps, GameCatalog.Identify(container)?.MapNamespace ?? "doom");
            File.WriteAllBytes(outPath, ImageCodec.EncodePng(MapPreview.Render(map, new MapPreview.Options(size, Things: things, Openings: openings))));
            output.WriteLine($"{lumps.Name} drawn to {outPath}.");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
