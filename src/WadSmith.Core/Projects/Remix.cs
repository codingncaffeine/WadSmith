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
    /// <param name="target">What the project plays in (see <see cref="Targets"/>); a classic target keeps the map in the classic format.</param>
    public static Project Create(string projectFolder, string baseGamePath, string mapName, string engineDataPath, List<string> notes, string? projectName = null, string target = Targets.GZDoom)
    {
        var (zdoom, game, name, checksum) = Original(baseGamePath, mapName, engineDataPath, notes);
        var project = Project.Create(projectFolder, projectName ?? $"{name} remix", game.Id, target);
        project.SaveMap(ClassicTranslation.ForProject(project, zdoom, engineDataPath), origin: new MapOrigin { Kind = "remix", Game = game.Id, Map = name, Checksum = checksum });

        // The behaviour switches GZDoom gives the original (shorttex, light …) are asked for in the project's MAPINFO (D40).
        try
        {
            ProjectMapInfo.Refresh(project, game, [baseGamePath], engineDataPath);
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException)
        {
            notes.Add($"The engine's switches for the original could not be read, so the remix plays without them: {ex.Message}");
        }

        return project;
    }

    /// <summary>
    /// Starts a project from a map in a map pack (a WAD that is not a base game) made for <paramref name="game"/>: the map
    /// comes in as a remix does, and the pack goes with the project as an add-on, so its own textures, sounds and things
    /// show in the editor and play with it. The pack is only read, never changed.
    /// </summary>
    /// <param name="projectFolder">Where the new project goes; it must not exist yet.</param>
    /// <param name="packPath">The pack, a WAD on disk (see <see cref="Games.MapPacks.Materialise"/>).</param>
    /// <param name="mapName">The map's slot in the pack, such as MAP01.</param>
    /// <param name="game">The base game the pack is for.</param>
    /// <param name="engineDataPath">The engine's gzdoom.pk3, whose rules translate the map.</param>
    /// <param name="notes">Gets anything the translation could not carry over.</param>
    /// <param name="projectName">The name the project is shown by; "PACK SLOT" when null.</param>
    /// <param name="target">What the project plays in (see <see cref="Targets"/>); a classic target keeps the map in the classic format.</param>
    public static Project CreateFromPack(string projectFolder, string packPath, string mapName, GameDefinition game, string engineDataPath, List<string> notes, string? projectName = null, string target = Targets.GZDoom)
    {
        var pack = ResourceContainer.Open(packPath);
        var lumps = MapLocator.Find(pack).FirstOrDefault(m => LumpName.Comparer.Equals(m.Name, mapName))
            ?? throw new CorruptDataException($"{pack.DisplayName} has no map called {mapName}.");
        var model = MapConversion.Read(lumps, game.MapNamespace);
        var engine = new ResourceStack([ResourceContainer.Open(engineDataPath)]);
        var xlat = lumps.Format == MapFormat.Doom ? XlatTranslator.Load(engine, XlatTranslator.PathFor(model.Namespace)) : null;
        var zdoom = MapTranslation.ToZDoom(model, xlat, notes);
        var checksum = MapChecksum.Compute(lumps);
        if (CompatibilityRepairs.Load(engine)?.Apply(zdoom, checksum, notes) == true) notes.Add($"GZDoom's own repairs for {lumps.Name} were applied, as the engine applies them to the original.");

        var project = Project.Create(projectFolder, projectName ?? $"{Path.GetFileNameWithoutExtension(packPath)} {lumps.Name}", game.Id, target);
        project.AddAddOn(Path.GetFullPath(packPath));
        project.SaveMap(ClassicTranslation.ForProject(project, zdoom, engineDataPath), origin: new MapOrigin { Kind = "import", Game = game.Id, Map = lumps.Name, Checksum = checksum, File = Path.GetFullPath(packPath) });
        return project;
    }

    /// <summary>
    /// A base game's map as a remix starts from it: in GZDoom's format, with the engine's own repairs. Also how the
    /// editor sees what a remix was before it was changed.
    /// </summary>
    /// <returns>The map, the game, the map's name and GZDoom's checksum of the original.</returns>
    public static (MapModel Map, GameDefinition Game, string Name, string Checksum) Original(string baseGamePath, string mapName, string engineDataPath, List<string> notes)
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

        // The fixes GZDoom gives the original map (D40); an edited copy has a new checksum, so they come along here.
        var checksum = MapChecksum.Compute(lumps);
        if (CompatibilityRepairs.Load(engine)?.Apply(zdoom, checksum, notes) == true) notes.Add($"GZDoom's own repairs for {lumps.Name} were applied, as the engine applies them to the original.");
        return (zdoom, game, lumps.Name, checksum);
    }
}
