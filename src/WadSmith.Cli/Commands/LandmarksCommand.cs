using System.Globalization;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Smart;

namespace WadSmith.Cli.Commands;

/// <summary>Lists the features of one map nearest its start, as candidates for the landmark index (D28), and checks the index.</summary>
internal sealed class LandmarksCommand : ICommand
{
    public string Name => "landmarks";

    public string Summary => "List a map's doors, lifts, stairs, secrets, switches, exits and keys nearest the start, for the landmark index.";

    public string Usage => """
        Usage: wadsmith-cli landmarks FILE --map=NAME [--each=3] [--engine-data=gzdoom.pk3]

        Opens the map as the editor does and prints, for each kind of feature, the first few the player
        meets (nearest the start), with the spot to put in data/landmarks/landmarks.json and its distance.
        Then checks every index entry for this file's game and map: the spot must show its feature.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var slot = args.Option("map") ?? throw new UsageException("landmarks needs --map=NAME.");
        var each = int.Parse(args.Option("each") ?? "3", CultureInfo.InvariantCulture);
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("landmarks takes one file.");
        if (enginePath is null)
        {
            error.WriteLine("GZDoom's data file (gzdoom.pk3) was not found; install GZDoom or pass --engine-data=PATH.");
            return ExitCode.Failed;
        }

        var file = args.Positional[0];
        var game = GameCatalog.Identify(ResourceContainer.Open(file));
        if (Landmarks.Read(file, slot, enginePath, game?.MapNamespace ?? "doom") is not { } map)
        {
            error.WriteLine($"{file} has no map {slot}.");
            return ExitCode.Failed;
        }

        var things = ThingCatalog.Load(ResourceContainer.Open(enginePath), game?.Engine ?? "doom");
        bool IsKey(int type) => things.Find(type)?.Category == ThingCategory.Key;
        var geometry = MapGeometry.Build(map.Frame);
        foreach (var group in Landmarks.Find(map, geometry, IsKey).GroupBy(f => f.Kind).OrderBy(g => Array.IndexOf(Landmarks.Kinds, g.Key)))
        {
            foreach (var f in group.Take(each))
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f.Kind,-12} x {f.At.X,7:0} y {f.At.Y,7:0}   {f.FromStart,6:0} from the start"));
            }
        }

        var failed = 0;
        foreach (var l in Landmarks.All.Where(l => l.Game == game?.Id && LumpName.Comparer.Equals(l.Map, slot)))
        {
            var shows = Landmarks.Shows(l.Kind, l.At, map, geometry, IsKey);
            if (!shows) failed++;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"index: {l.Kind} at {l.X:0}, {l.Y:0} {(shows ? "shows it" : "DOES NOT SHOW IT")}"));
        }

        return failed == 0 ? ExitCode.Ok : ExitCode.Failed;
    }
}
