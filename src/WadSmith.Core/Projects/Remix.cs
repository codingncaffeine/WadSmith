using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;

namespace WadSmith.Core.Projects;

/// <summary>
/// "Remix a map you know": starts a project from one of a base game's own maps. The base game file
/// is only read, never changed; the map is translated into GZDoom's namespace with the engine's own
/// tables and saved in the new project.
/// </summary>
public static class Remix
{
    /// <param name="projectFolder">Where the new project goes; it must not exist yet.</param>
    /// <param name="baseGamePath">The base game file, only read.</param>
    /// <param name="mapName">The map's slot, such as MAP01.</param>
    /// <param name="engineDataPath">The engine's gzdoom.pk3, whose rules translate the map.</param>
    /// <param name="notes">Gets anything the translation could not carry over.</param>
    /// <param name="projectName">The name the project is shown by; "SLOT remix" when null.</param>
    public static Project Create(string projectFolder, string baseGamePath, string mapName, string engineDataPath, List<string> notes, string? projectName = null)
    {
        var baseGame = ResourceContainer.Open(baseGamePath);
        var game = GameCatalog.Identify(baseGame) ?? throw new CorruptDataException($"{baseGame.DisplayName} is not a base game WadSmith recognises.");
        if (!game.Supported) throw new CorruptDataException($"{game.Title} is recognised, but GZDoom does not run it, so WadSmith cannot remix it.");
        var lumps = MapLocator.Find(baseGame).FirstOrDefault(m => LumpName.Comparer.Equals(m.Name, mapName))
            ?? throw new CorruptDataException($"{game.Title} has no map called {mapName}.");

        var model = MapConversion.Read(lumps, game.MapNamespace);
        var engine = new ResourceStack([ResourceContainer.Open(engineDataPath)]);
        var xlat = lumps.Format == MapFormat.Doom ? XlatTranslator.Load(engine, XlatTranslator.PathFor(model.Namespace)) : null;
        var zdoom = MapTranslation.ToZDoom(model, xlat, notes);

        var project = Project.Create(projectFolder, projectName ?? $"{lumps.Name} remix", game.Id);
        project.SaveMap(zdoom, origin: new MapOrigin { Kind = "remix", Game = game.Id, Map = lumps.Name, Checksum = MapChecksum.Compute(lumps) });
        return project;
    }
}
