using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>A prefab kept in a project: its name, and the file it is kept in.</summary>
public sealed record Prefab(string Name, string Path);

/// <summary>
/// Prefabs: rooms and things saved to use again, in any map of the project. A prefab is what Copy copies (the
/// rooms with their walls and the things in them), kept as a small UDMF map in the project's prefabs folder,
/// texture names only; using one pastes it as Paste does.
/// </summary>
public static class Prefabs
{
    public const string FolderName = "prefabs";
    public const string Extension = ".udmf";

    /// <summary>The project's prefabs, by name.</summary>
    public static IReadOnlyList<Prefab> In(string projectFolder)
    {
        var folder = System.IO.Path.Combine(projectFolder, FolderName);
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*" + Extension).Select(p => new Prefab(System.IO.Path.GetFileNameWithoutExtension(p), p)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The prefab's text for what is selected (as Copy copies it), named in <paramref name="nameSpace"/>; null when nothing would be kept.</summary>
    public static string? Text(MapGeometry geometry, IReadOnlyList<Element> selection, string nameSpace, Look look)
    {
        var clip = Copying.Copy(geometry, selection);
        if (clip.IsEmpty) return null;

        // Pasted into an empty map at its middle's old place, then written out as that map.
        var map = EditorMap.FromModel(new MapModel { Name = "PREFAB", Namespace = nameSpace });
        var empty = MapGeometry.Build(map.Frame);
        new History(map).Record("Prefab", e => Copying.Paste(e, empty, clip, clip.Origin, look));
        return UdmfText.WriteString(MapConversion.ToUdmf(map.ToModel()));
    }

    /// <summary>Saves what is selected as a prefab in the project, named <paramref name="name"/> or "Prefab N"; returns it, or null when nothing would be kept.</summary>
    public static Prefab? Save(string projectFolder, MapGeometry geometry, IReadOnlyList<Element> selection, string nameSpace, Look look, string? name = null)
    {
        if (Text(geometry, selection, nameSpace, look) is not { } text) return null;
        var folder = System.IO.Path.Combine(projectFolder, FolderName);
        Directory.CreateDirectory(folder);
        name = Clean(name) ?? Enumerable.Range(1, 10000).Select(n => $"Prefab {n}").First(n => !File.Exists(System.IO.Path.Combine(folder, n + Extension)));
        var path = System.IO.Path.Combine(folder, name + Extension);
        File.WriteAllText(path, text);
        return new Prefab(name, path);
    }

    /// <summary>What a prefab holds, ready to paste: every room and thing in it.</summary>
    public static MapClip Load(Prefab prefab)
    {
        var map = EditorMap.FromModel(MapConversion.FromUdmf("PREFAB", UdmfText.Parse(File.ReadAllText(prefab.Path))));
        var geometry = MapGeometry.Build(map.Frame);
        return Copying.Copy(geometry, [.. map.Sectors.Where(s => s.OnMap), .. map.Things.Where(t => t.OnMap)]);
    }

    /// <summary>A name made safe for a file: no path or reserved characters, trimmed; null when nothing is left.</summary>
    private static string? Clean(string? name)
    {
        if (name is null) return null;
        var bad = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string([.. name.Where(c => !bad.Contains(c) && !char.IsControl(c))]).Trim().Trim('.');
        return clean.Length == 0 ? null : clean;
    }
}
