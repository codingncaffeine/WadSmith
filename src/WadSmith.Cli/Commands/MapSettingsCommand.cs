using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Cli.Commands;

/// <summary>A project map's settings (name, music, sky, next map) read or changed, and the project's zmapinfo.txt written from them.</summary>
internal sealed class MapSettingsCommand : ICommand
{
    public string Name => "map-settings";

    public string Summary => "Show or change a map's name, music, sky and next map; write the project's zmapinfo.txt.";

    public string Usage => """
        Usage: wadsmith-cli map-settings PROJECT_FOLDER [--map=SLOT] [--title=TEXT] [--music=NAME] [--sky=NAME]
                                         [--next=SLOT|EndTitle] [--hub=N|none|game] [--games=FOLDER] [--engine-data=gzdoom.pk3]

        Without a setting, prints the map's settings and the file. With one, changes it ("game" or an empty
        value goes back to the game's own) and writes wadsmith.json and zmapinfo.txt: one block for each map
        that differs from the game, starting from the game's own definition of the slot, and the behaviour
        switches a remix's original runs with. A zmapinfo.txt WadSmith did not write is left alone. --hub puts the
        map in a hub: one of the game's (Hexen's 1 to 5), the project's own (numbered from 100), none, or the game's again.
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var slot = args.Option("map");
        var title = args.Option("title");
        var music = args.Option("music");
        var sky = args.Option("sky");
        var next = args.Option("next");
        var hub = args.Option("hub");
        var games = args.Option("games");
        var enginePath = args.Option("engine-data") ?? EngineData.Find();
        args.RejectUnread();
        if (args.Positional.Count != 1) throw new UsageException("map-settings takes one project folder.");

        try
        {
            var project = Project.Open(args.Positional[0]);
            slot ??= project.Info.Maps.FirstOrDefault()?.Slot ?? throw new CorruptDataException("The project has no maps.");
            static string? Value(string? v) => v is null || v.Length == 0 || v.Equals("game", StringComparison.OrdinalIgnoreCase) ? null : v;
            int? Hub(int? was) => hub is null ? was : Value(hub) is null ? null : hub.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
                : int.TryParse(hub, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : throw new UsageException("--hub takes a number, none or game.");
            var game = GameCatalog.Get(project.Info.Game) ?? throw new CorruptDataException($"The project's game ({project.Info.Game}) is not one WadSmith knows.");
            var found = GameLocator.Scan([.. GameLocator.StandardFolders(Environment.GetEnvironmentVariable), .. games is null ? Array.Empty<string>() : [games]]);
            var files = GameLocator.FilesFor(found, game.Id);

            // A hub below the project's own numbers is one of the game's: any other cluster of the game's is no hub.
            if (Hub(null) is > 0 and < ProjectMapInfo.FirstOwnHub and var wanted)
            {
                var hubs = files.Count > 0 && enginePath is not null && File.Exists(enginePath) ? MapInfo.For(game, [.. files.Select(ResourceContainer.Open)], enginePath).HubClusters : new HashSet<int>();
                if (!hubs.Contains(wanted)) throw new UsageException($"--hub={wanted}: {game.Title} has no hub {wanted}{(hubs.Count > 0 ? $" (its hubs are {string.Join(", ", hubs.Order())})" : "")}; the project's own hubs are numbered from {ProjectMapInfo.FirstOwnHub}.");
            }

            if (title is not null || music is not null || sky is not null || next is not null || hub is not null)
            {
                project.UpdateMap(slot, m => m with
                {
                    Title = title is null ? m.Title : Value(title) ?? "",
                    Music = music is null ? m.Music : Value(music),
                    Sky = sky is null ? m.Sky : Value(sky),
                    Next = next is null ? m.Next : Value(next),
                    Hub = Hub(m.Hub),
                });
            }

            if (files.Count == 0) output.WriteLine($"Note: {game.Title} was not found on this computer, so the blocks start from the settings alone, not the game's definitions.");
            var written = ProjectMapInfo.Refresh(project, game, files, enginePath);
            var entry = project.Map(slot) ?? throw new CorruptDataException($"The project has no map {slot}.");
            output.WriteLine($"{entry.Slot}: title \"{entry.Title}\", music {entry.Music ?? "(the game's)"}, sky {entry.Sky ?? "(the game's)"}, next {entry.Next ?? "(the game's)"}, hub {(entry.Hub is { } h ? h == 0 ? "none" : h.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(the game's)")}");
            var path = Path.Combine(project.Folder, ProjectMapInfo.FileName);
            if (!written) output.WriteLine($"{ProjectMapInfo.FileName} is the project's own (not written by WadSmith): left alone.");
            else if (File.Exists(path)) output.WriteLine(File.ReadAllText(path));
            else output.WriteLine($"No {ProjectMapInfo.FileName}: every map plays with the game's own settings.");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is CorruptDataException or FileNotFoundException or IOException or KeyNotFoundException)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Failed;
        }
    }
}
