using System.Globalization;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Generate;
using WadSmith.Editor.Geometry;

namespace WadSmith.Cli.Commands;

/// <summary>Makes a map with the level generator ("Make me a map", D50), checks it, and writes it as a WAD or a project.</summary>
internal sealed class GenerateCommand : ICommand
{
    public string Name => "generate";

    public string Summary => "Make a map with the level generator, check it, and write it as a WAD or a project.";

    public string Usage => """
        Usage: wadsmith-cli generate (--out=FILE.wad | --project=FOLDER) [--game=doom2] [--seed=misty-cavern]
               [--size=small|medium|large|huge] [--challenge=0..1] [--keys=0..3] [--bosses=0..3]
               [--outdoors=none|some|lots] [--without=secrets,stairs,lifts,teleporters,dark,ambushes,decorations]
               [--script]

        Plans a map from the seed and the settings, builds it with the editor's own tools (an edit script,
        printed with --script), checks its shapes and Problems, and writes it: as a WAD holding the map in
        its slot, or as a WadSmith project (the map, and generator.json with the settings). The same seed
        and settings make the same map on any machine. --game is an id from data/games/games.json.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var outPath = args.Option("out");
        var projectPath = args.Option("project");
        var gameId = args.Option("game") ?? "doom2";
        var game = GameCatalog.Get(gameId) ?? throw new UsageException($"No game has the id {gameId}.");
        if (!GeneratorSettings.CanMake(game)) throw new UsageException($"Maps cannot be made for {game.Title} yet.");
        var s = GeneratorSettings.ForGame(game);
        if (args.Option("seed") is { } seed) s = s with { Seed = SeedWords.Tidy(seed) };
        if (args.Option("size") is { } size) s = s with { Size = Enum.TryParse<MapSize>(size, true, out var z) ? z : throw new UsageException("--size is small, medium, large or huge.") };
        if (args.Option("outdoors") is { } outdoors) s = s with { Outdoors = Enum.TryParse<OutdoorShare>(outdoors, true, out var o) ? o : throw new UsageException("--outdoors is none, some or lots.") };
        if (args.Option("challenge") is { } challenge) s = s with { Challenge = double.TryParse(challenge, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 1 ? c : throw new UsageException("--challenge is a number from 0 to 1.") };
        if (args.Option("keys") is { } keys) s = s with { Keys = int.TryParse(keys, out var k) && k is >= 0 and <= 3 ? k : throw new UsageException("--keys is 0 to 3.") };
        if (args.Option("bosses") is { } bosses) s = s with { Bosses = int.TryParse(bosses, out var b) && b is >= 0 and <= 3 ? b : throw new UsageException("--bosses is 0 to 3.") };
        foreach (var without in (args.Option("without") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            s = without.Trim().ToLowerInvariant() switch
            {
                "secrets" => s with { Secrets = false },
                "stairs" => s with { Stairs = false },
                "lifts" => s with { Lifts = false },
                "teleporters" => s with { Teleporters = false },
                "dark" => s with { DarkPlaces = false },
                "ambushes" => s with { Ambushes = false },
                "decorations" => s with { Decorations = false },
                var other => throw new UsageException($"--without takes secrets, stairs, lifts, teleporters, dark, ambushes and decorations, not {other}."),
            };
        }

        var printScript = args.Flag("script");
        args.RejectUnread();
        if (args.Positional.Count != 0) throw new UsageException("generate takes no file names; use --out or --project.");
        if ((outPath is null) == (projectPath is null)) throw new UsageException("generate needs one of --out=FILE.wad or --project=FOLDER.");

        var plan = Planner.Plan(s);
        MadeMap made;
        if (projectPath is not null) made = MadeProjects.Create(projectPath, plan).Made;
        else made = MapMaker.Build(plan);
        if (printScript) output.Write(made.Script);
        foreach (var f in made.Failed) output.WriteLine($"{f.Line,4}  FAIL  {f.Text}  →  {f.Said}");

        var faults = Invariants.Check(made.Map);
        foreach (var f in faults) output.WriteLine($"shape: {f}");
        var geometry = MapGeometry.Build(made.Map.Frame);
        var problems = Problems.Check(geometry, null, Look.For(s.Family), s.Family);
        foreach (var p in problems) output.WriteLine($"problem: {p.Severity}: {p.Sentence}");
        var walk = Progression.Walk(geometry, s.Family);

        var counts = plan.MonsterCounts;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{SeedWords.Title(s.Seed)} for {game.Title}: {plan.Rooms.Count} rooms, {plan.Links.Count(l => l.Key is not null)} locked doors, {plan.Rooms.Count(r => r.Role == RoomRole.Secret)} secrets, monsters {counts.Easy}/{counts.Normal}/{counts.Hard} (easy/normal/hard), about {plan.Minutes} minutes; the exit {(walk.ExitReached ? "can" : "CANNOT")} be reached."));
        if (outPath is not null)
        {
            var model = made.Map.Frame.ToModel();
            var lumps = new List<(string, ReadOnlyMemory<byte>)> { (s.Slot, Array.Empty<byte>()) };
            lumps.AddRange(MapConversion.ToUdmfLumps(model).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
            File.WriteAllBytes(outPath, WadWriter.Write(WadKind.Pwad, lumps));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{s.Slot}: {model.Sectors.Count} areas, {model.Lines.Count} walls, {model.Things.Count} things written to {outPath}."));
        }
        else
        {
            output.WriteLine($"project: {Path.GetFullPath(projectPath!)} ({s.Slot})");
        }

        return made.Failed.Count == 0 && faults.Count == 0 && problems.Count == 0 && walk.ExitReached ? ExitCode.Ok : ExitCode.Failed;
    }
}
