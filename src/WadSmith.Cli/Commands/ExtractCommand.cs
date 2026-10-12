using System.Diagnostics;
using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Assets;
using WadSmith.Core.Containers;

namespace WadSmith.Cli.Commands;

internal sealed class ExtractCommand : ICommand
{
    public string Name => "extract";

    public string Summary => "Turn a WAD or PK3 into folders of PNG, WAV, MIDI and map files.";

    public string Usage => """
        Usage: wadsmith-cli extract FILE --out=FOLDER [--base=BASEGAME] [--only=KINDS]

        Writes every texture, floor, patch, sprite and graphic as PNG, every sound as WAV, music
        as MIDI (or its own format), each map as a WAD, the palette as a picture, and everything
        else as it is, with a manifest.json saying where each file came from.

          --out=FOLDER      Where to write. Created if missing.
          --base=BASEGAME   The base game an add-on builds on, for its palette and borrowed patches.
          --only=KINDS      Comma-separated: textures, floors, patches, sprites, graphics, sounds,
                            music, maps, palette, other.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var outFolder = args.Option("out") ?? throw new UsageException("extract needs --out=FOLDER.");
        var basePath = args.Option("base");
        var onlyText = args.Option("only");
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("extract takes one file or folder.");

        HashSet<AssetKind>? only = null;
        if (onlyText is not null)
        {
            only = [];
            foreach (var part in onlyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<AssetKind>(part, ignoreCase: true, out var kind)) throw new UsageException($"\"{part}\" is not a kind of asset. Known: {string.Join(", ", Enum.GetNames<AssetKind>().Select(n => n.ToLowerInvariant()))}.");
                only.Add(kind);
            }
        }

        var stack = new ResourceStack();
        ResourceContainer source;
        try
        {
            if (basePath is not null) stack.Add(ResourceContainer.Open(basePath));
            source = ResourceContainer.Open(args.Positional[0]);
            stack.Add(source);
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }

        var clock = Stopwatch.StartNew();
        var result = AssetExtractor.Extract(source, stack, outFolder, only);
        var inv = CultureInfo.InvariantCulture;
        foreach (var (kind, count) in result.Counts.OrderBy(k => k.Key)) output.WriteLine(string.Create(inv, $"  {kind,-9} {count,6}"));
        output.WriteLine(string.Create(inv, $"{result.Assets.Count} files written to {outFolder} in {clock.Elapsed.TotalSeconds:0.0} s."));
        foreach (var p in result.Problems) output.WriteLine($"  Problem: {p}");
        return result.Problems.Count == 0 ? ExitCode.Ok : ExitCode.Failed;
    }
}
