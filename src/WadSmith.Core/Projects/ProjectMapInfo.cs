using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Core.Projects;

/// <summary>
/// The project's <c>zmapinfo.txt</c>, written from each map's settings (title, music, sky, next map) and a remix's
/// engine switches (D40): one block for each map that differs from the base game, starting from the base game's own
/// definition, since GZDoom replaces a map's whole definition with a project's block (measured 2026-10-12). A file
/// WadSmith did not write is never touched.
/// </summary>
public static class ProjectMapInfo
{
    public const string FileName = "zmapinfo.txt";

    /// <summary>The first line of a file WadSmith wrote.</summary>
    public const string Mark = "// Written by WadSmith from the maps' settings: change them in WadSmith, which writes this file again.";

    /// <summary>The value of <see cref="ProjectMap.Next"/> that ends the game after the map and goes back to the title screen.</summary>
    public const string EndToTitle = "EndTitle";

    /// <summary>The project's own hubs are clusters from this number up, clear of every game's own (Hexen's are 1 to 5).</summary>
    public const int FirstOwnHub = 100;

    /// <summary>Whether the map needs a block: a setting of its own, or switches its original ran with.</summary>
    public static bool Needed(ProjectMap map, IReadOnlyList<string> switches) =>
        map.Title.Length > 0 || map.Music is not null || map.Sky is not null || map.Next is not null || map.Hub is not null || switches.Count > 0;

    /// <summary>One map's block: the base definition (when the slot has one) with the map's settings and switches over it.</summary>
    public static string Block(ProjectMap map, MapDefinition? definition, IReadOnlyList<string> switches)
    {
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (map.Title.Length > 0) replaced.UnionWith(["titlepatch", "levelname"]);
        if (map.Music is not null) replaced.Add("music");
        if (map.Sky is not null) replaced.Add("sky1");
        if (map.Next is not null) replaced.Add("next");
        if (map.Hub is not null) replaced.Add("cluster");
        replaced.UnionWith(switches.Select(s => "compat_" + s));

        var header = map.Title.Length > 0 ? MapInfo.Quote(map.Title) : definition?.Header is { Length: > 0 } h ? h : MapInfo.Quote(map.Slot);
        var b = new StringBuilder().Append("map ").Append(map.Slot).Append(' ').Append(header).Append("\n{\n");
        foreach (var p in definition?.Properties ?? [])
        {
            if (!replaced.Contains(MapDefinition.KeyOf(p))) b.Append('\t').Append(p).Append('\n');
        }

        if (map.Music is { } music) b.Append("\tmusic = ").Append(MapInfo.Quote(music)).Append('\n');
        if (map.Sky is { } sky) b.Append("\tsky1 = ").Append(MapInfo.Quote(sky)).Append('\n');
        if (map.Next is { } next) b.Append("\tnext = ").Append(MapInfo.Quote(next)).Append('\n');
        if (map.Hub is { } hub) b.Append("\tcluster = ").Append(hub.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        foreach (var s in switches) b.Append("\tcompat_").Append(s).Append(" = 1\n");
        return b.Append("}\n").ToString();
    }

    /// <summary>The file for the project's maps; null when none needs a block.</summary>
    public static string? Text(IEnumerable<ProjectMap> maps, MapDefinitions definitions, Func<ProjectMap, IReadOnlyList<string>> switches, IEnumerable<CopiedAsset>? copies = null)
    {
        var blocks = maps.Select(m => (Map: m, Switches: switches(m))).Where(x => Needed(x.Map, x.Switches)).Select(x => Block(x.Map, definitions.For(x.Map.Slot), x.Switches)).ToList();

        // The project's own hubs (a number below them is the game's cluster, which a block here would define afresh).
        foreach (var hub in maps.Select(m => m.Hub).OfType<int>().Where(h => h >= FirstOwnHub && !definitions.HubClusters.Contains(h)).Distinct().Order())
        {
            blocks.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"cluster {hub}\n{{\n\thub\n}}\n"));
        }

        // Objects carried in from other games (D55): the numbers the map places them by.
        var actors = (copies ?? []).Where(c => c.Kind == "actor").ToList();
        if (actors.Count > 0) blocks.Add("DoomEdNums\n{\n" + string.Concat(actors.Select(a => $"    {a.Name} = {a.Original}\n")) + "}\n");

        // Event handlers WadSmith wrote (camera screens): registered for every map.
        var handlers = (copies ?? []).Where(c => c.Kind == "eventhandler").Select(c => MapInfo.Quote(c.Original)).Distinct(StringComparer.Ordinal).ToList();
        if (handlers.Count > 0) blocks.Add("GameInfo\n{\n    AddEventHandlers = " + string.Join(", ", handlers) + "\n}\n");
        return blocks.Count == 0 ? null : Mark + "\n\n" + string.Join("\n", blocks);
    }

    /// <summary>
    /// Writes the project's file from its maps' settings and, for remixes, the switches their originals run with. The game's
    /// files are read for the base definitions (none without the engine's data: a block then holds the settings alone).
    /// Reads files: call it on a worker. Returns false when the project holds a file WadSmith did not write, left alone.
    /// <paramref name="gameFiles"/> are the game's files in load order: the base game, then an add-on over it.
    /// </summary>
    public static bool Refresh(Project project, GameDefinition game, IReadOnlyList<string> gameFiles, string? engineDataPath)
    {
        var maps = project.Maps();
        if (engineDataPath is null || !File.Exists(engineDataPath) || gameFiles.Count == 0) return Write(project, Text(maps, MapDefinitions.None, _ => [], project.Info.Copied));
        var containers = gameFiles.Select(ResourceContainer.Open).ToList();
        var definitions = MapInfo.For(game, containers, engineDataPath);
        var switches = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return Write(project, Text(maps, definitions, m => SwitchesFor(m, containers[0], engineDataPath, switches), project.Info.Copied));
    }

    /// <summary>The switches a remix runs with (its original's), none for a map made here or imported; one look-up per checksum.</summary>
    public static IReadOnlyList<string> SwitchesFor(ProjectMap map, ResourceContainer baseGame, string engineDataPath, Dictionary<string, IReadOnlyList<string>> cache)
    {
        if (map.Origin is not { Kind: "remix", Checksum: { Length: > 0 } sum }) return [];
        if (!cache.TryGetValue(sum, out var found)) cache[sum] = found = RemixCompat.For(baseGame, sum, engineDataPath).Carried;
        return found;
    }

    /// <summary>Whether the project holds a <c>zmapinfo.txt</c> it did not get from WadSmith (then Map settings leave it alone).</summary>
    public static bool HasForeign(Project project)
    {
        var path = Path.Combine(project.Folder, FileName);
        if (!File.Exists(path)) return false;
        using var reader = new StreamReader(path);
        return reader.ReadLine() != Mark;
    }

    /// <summary>
    /// Writes <paramref name="text"/> as the project's file, or removes WadSmith's file when it is null. Returns false,
    /// writing nothing, when the project holds a file WadSmith did not write.
    /// </summary>
    public static bool Write(Project project, string? text)
    {
        if (HasForeign(project)) return false;
        var path = Path.Combine(project.Folder, FileName);
        if (text is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return true;
        project.WriteFile(FileName, bytes);
        return true;
    }
}
