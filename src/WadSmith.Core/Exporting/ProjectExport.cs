using System.Globalization;
using System.IO.Compression;
using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;

namespace WadSmith.Core.Exporting;

/// <summary>
/// A whole project exported as one file (Phase 12): for GZDoom a .pk3 holding the project as GZDoom reads it, for a
/// classic port a .wad holding every map in the classic format with its nodes — and, for the ports that read it
/// (Boom, MBF21), a UMAPINFO with each map's name, music, sky and next map and the music it needs. What the export
/// cannot carry is in the report, map by map and for the project.
/// </summary>
public static class ProjectExport
{
    /// <summary>The export for GZDoom (and UZDoom): the project as a .pk3.</summary>
    public const string GZDoom = "gzdoom";

    /// <summary>The project's own bookkeeping, which no engine reads.</summary>
    private static readonly string[] Bookkeeping = [Project.SidecarName, "generator.json"];

    /// <summary>What a project can be exported for, as (id, title, file extension): GZDoom, then each port that plays its game family.</summary>
    public static IReadOnlyList<(string Id, string Title, string Extension)> ChoicesFor(string family) =>
        [(GZDoom, "GZDoom and UZDoom", ".pk3"), .. ClassicPorts.ForFamily(family).Select(p => (p.Id, p.Title, ".wad"))];

    /// <summary>The picture names a base game holds (its own walls and floors), for <see cref="MapExport"/>'s check.</summary>
    public static PictureNames PicturesOf(ResourceStack baseGame)
    {
        var library = new TextureLibrary(baseGame);
        var walls = new HashSet<string>(library.Walls.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        var flats = new HashSet<string>(library.Flats.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return new PictureNames(walls.Contains, flats.Contains);
    }

    /// <summary>
    /// Writes <paramref name="project"/> exported for <paramref name="exportId"/> (<see cref="GZDoom"/> or a classic port's
    /// id) to <paramref name="outFile"/> and returns what it lost. The engine's data (gzdoom.pk3) is needed for a classic
    /// port: its tables say what each classic type means. <paramref name="pictures"/> are the base game's, when known.
    /// Reads and writes files: call it on a worker.
    /// </summary>
    public static ExportReport Write(Project project, string exportId, string outFile, string? engineData, PictureNames? pictures = null)
    {
        var report = new ExportReport();
        byte[] data;
        if (exportId == GZDoom)
        {
            data = Pk3(project, report);
        }
        else
        {
            var port = ClassicPorts.Get(exportId) ?? throw new ArgumentException($"{exportId} is not a port WadSmith exports for.", nameof(exportId));
            data = Wad(project, port, engineData, pictures, report);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(outFile))!;
        Directory.CreateDirectory(folder);
        var temp = outFile + ".part";
        File.WriteAllBytes(temp, data);
        SharedFiles.Replace(temp, outFile);
        return report;
    }

    /// <summary>The project folder as a .pk3: everything GZDoom reads, without WadSmith's own records and versions.</summary>
    private static byte[] Pk3(Project project, ExportReport report)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(project.Folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(project.Folder, file).Replace('\\', '/');
                var top = Path.GetRelativePath(project.Folder, file).Split(Path.DirectorySeparatorChar)[0];
                if (top.StartsWith('.') || top == Project.DeletedFolderName || Bookkeeping.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                using var to = entry.Open();
                to.Write(SharedFiles.ReadAllBytes(file));
            }
        }

        foreach (var addOn in project.Info.AddOns)
        {
            report.Add(new ExportLoss(null, "project:add-on", $"Plays over {Path.GetFileName(addOn)}", "file", [], false, "load that file before this one, as WadSmith does"));
        }

        return bytes.ToArray();
    }

    /// <summary>Every map for a classic port, in one .wad, with UMAPINFO and its music for the ports that read it.</summary>
    private static byte[] Wad(Project project, ClassicPort port, string? engineData, PictureNames? pictures, ExportReport report)
    {
        var family = project.ClassicNamespace;
        if (port.For(family) is null) throw new ArgumentException($"{port.Title} does not play {GameCatalog.Get(project.Info.Game)?.Title ?? project.Info.Game} maps.", nameof(port));
        XlatTranslator? xlat = null;
        if (family != "hexen")
        {
            if (engineData is null || !File.Exists(engineData)) throw new CorruptDataException("Exporting for a classic port needs GZDoom installed: WadSmith reads the engine's own table of what each classic line type does.");
            xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(engineData)]), XlatTranslator.PathFor(family));
        }

        var exMy = GameCatalog.Get(project.Info.Game)?.MapNames == "ExMy";
        var lumps = new List<(string Name, byte[] Data)>();
        foreach (var entry in project.Maps())
        {
            var model = project.LoadMap(entry.Slot);
            if (model.Namespace != "zdoom") model = MapTranslation.ToZDoom(model, xlat, []);
            lumps.AddRange(MapExport.ForPort(model, port, family, xlat, report, pictures, exMy));
        }

        MapInfo(project, port, lumps, report);
        ProjectFiles(project, report);
        return WadWriter.Write(WadKind.Pwad, lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
    }

    /// <summary>
    /// The maps' names, music, skies and next maps: a UMAPINFO for the ports that read it (with the music files they
    /// name, when the file is MUS or MIDI), lost for the others.
    /// </summary>
    private static void MapInfo(Project project, ClassicPort port, List<(string Name, byte[] Data)> lumps, ExportReport report)
    {
        var maps = project.Maps().Where(m => m.Title.Length > 0 || m.Music is not null || m.Sky is not null || m.Next is not null).ToList();
        if (maps.Count == 0) return;
        if (!port.Umapinfo)
        {
            foreach (var m in maps)
            {
                var what = new List<string>();
                if (m.Title.Length > 0) what.Add($"name \"{m.Title}\"");
                if (m.Music is not null) what.Add($"music {m.Music}");
                if (m.Sky is not null) what.Add($"sky {m.Sky}");
                if (m.Next is not null) what.Add($"next map {m.Next}");
                report.Add(new ExportLoss(m.Slot, "map:info", $"The map's {string.Join(", ", what)}", "map", [], false, $"{port.Title} has no place for them; the slot's own are used"));
            }

            return;
        }

        var text = new StringBuilder();
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in maps)
        {
            text.Append("MAP ").Append(m.Slot).Append("\n{\n");
            if (m.Title.Length > 0) text.Append("\tlevelname = ").Append(Quote(m.Title)).Append('\n');
            if (m.Music is { } music)
            {
                if (MusicFile(project, music) is { } file)
                {
                    var data = SharedFiles.ReadAllBytes(file);
                    if (IsMusOrMidi(data))
                    {
                        if (carried.Add(music)) lumps.Add((music.ToUpperInvariant(), data));
                    }
                    else
                    {
                        report.Add(new ExportLoss(m.Slot, "map:music-format", $"Music {music} is not MUS or MIDI", "map", [], false, "the game plays no music for the map"));
                    }
                }

                text.Append("\tmusic = ").Append(Quote(music)).Append('\n');
            }

            if (m.Sky is { } sky) text.Append("\tskytexture = ").Append(Quote(sky)).Append('\n');
            if (m.Next == ProjectMapInfo.EndToTitle) text.Append("\tendgame = true\n");
            else if (m.Next is { } next) text.Append("\tnext = ").Append(Quote(next)).Append('\n');
            text.Append("}\n\n");
        }

        lumps.Add(("UMAPINFO", Encoding.ASCII.GetBytes(text.ToString())));
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>The project's music file of that lump name, if it carries one.</summary>
    private static string? MusicFile(Project project, string lump)
    {
        var folder = Path.Combine(project.Folder, "music");
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(lump, StringComparison.OrdinalIgnoreCase)) : null;
    }

    private static bool IsMusOrMidi(byte[] d) => d.Length >= 4 && ((d[0] == 'M' && d[1] == 'U' && d[2] == 'S' && d[3] == 0x1A) || (d[0] == 'M' && d[1] == 'T' && d[2] == 'h' && d[3] == 'd'));

    /// <summary>The project's own files a classic WAD does not carry, by kind (music the maps name is carried by <see cref="MapInfo"/>).</summary>
    private static void ProjectFiles(Project project, ExportReport report)
    {
        var kinds = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(project.Folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project.Folder, file).Replace('\\', '/');
            var parts = Path.GetRelativePath(project.Folder, file).Split(Path.DirectorySeparatorChar);
            if (parts[0].StartsWith('.') || parts[0] == Project.DeletedFolderName || parts[0] is "maps" or "music" || Bookkeeping.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            if (relative.Equals(ProjectMapInfo.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            var kind = parts[0].ToLowerInvariant() switch
            {
                "textures" or "flats" or "patches" or "materials" => "Your own wall and floor pictures",
                "sprites" => "Pictures of objects brought in from other games or files",
                "sounds" or "sndinfo.txt" => "Sounds",
                "models" or "voxels" or "modeldef.txt" or "voxeldef.txt" => "3D models and voxels",
                "skies" => "Skies from pictures",
                _ => "GZDoom's own definitions",
            };
            (kinds.TryGetValue(kind, out var list) ? list : kinds[kind] = []).Add(relative);
        }

        foreach (var (kind, files) in kinds)
        {
            var shown = string.Join(", ", files.Order(StringComparer.Ordinal).Take(4)) + (files.Count > 4 ? string.Create(CultureInfo.InvariantCulture, $" and {files.Count - 4} more") : "");
            report.Add(new ExportLoss(null, "project:" + kind, kind, "file", [.. Enumerable.Range(0, files.Count)], false, $"not carried into a classic WAD: {shown}"));
        }
    }
}
