using System.Globalization;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Cli.Commands;

/// <summary>Builds a map from an edit script with the editor's own tools, checks it, and writes it for the game.</summary>
internal sealed class ScriptCommand : ICommand
{
    public string Name => "script";

    public string Summary => "Build a map from an edit script with the editor's tools, check it, and write it as a WAD.";

    public string Usage => $"""
        Usage: wadsmith-cli script SCRIPT --out=FILE.wad [--game=doom|heretic|hexen|strife]

        Runs SCRIPT, one command a line (# starts a comment), on an empty MAP01 in GZDoom's UDMF, each as
        the editor would do it; then checks the map's shapes (every area closed, no walls crossing, every
        wall facing its area) and lists its Problems. Writes the map to FILE.wad. Fails when a line could
        not be done or a shape rule is broken.

        Commands (coordinates in map units; a point picks the room it is in or the wall nearest it):
        {EditScript.Commands}
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var outPath = args.Option("out") ?? throw new UsageException("script needs --out=FILE.wad.");
        var engine = args.Option("game") ?? "doom";
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("script takes one script file.");
        if (engine is not ("doom" or "heretic" or "hexen" or "strife")) throw new UsageException("--game is doom, heretic, hexen or strife.");
        string text;
        try
        {
            text = File.ReadAllText(args.Positional[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }

        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var steps = EditScript.Run(map, text, Look.For(engine), engine);
        foreach (var s in steps) output.WriteLine($"{s.Line,4}  {(s.Done ? "ok  " : "FAIL")}  {s.Text}  →  {s.Said}");

        var faults = Invariants.Check(map);
        output.WriteLine(faults.Count == 0 ? "shapes: every area closed, no walls crossing, every wall facing its area" : $"shapes: {faults.Count} rule(s) broken");
        foreach (var f in faults) output.WriteLine($"    {f}");

        var problems = Problems.Check(MapGeometry.Build(map.Frame), null, Look.For(engine), engine);
        output.WriteLine(problems.Count == 0 ? "problems: none" : $"problems: {problems.Count}");
        foreach (var p in problems) output.WriteLine($"    {p.Severity}: {p.Sentence}");

        var model = map.ToModel();
        var lumps = new List<(string, ReadOnlyMemory<byte>)> { ("MAP01", Array.Empty<byte>()) };
        lumps.AddRange(MapConversion.ToUdmfLumps(model).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
        File.WriteAllBytes(outPath, WadWriter.Write(WadKind.Pwad, lumps));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"MAP01: {model.Sectors.Count} areas, {model.Lines.Count} walls, {model.Things.Count} things written to {outPath}."));
        return steps.All(s => s.Done) && faults.Count == 0 ? ExitCode.Ok : ExitCode.Failed;
    }
}
