using System.Globalization;
using System.IO.Compression;
using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
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
    /// <summary>
    /// The Share check (D22): what the project copied from games others must own (the commercial games in the catalogue; free
    /// ones such as Freedoom and Blasphemer are free to share), counted by game, in a sentence; null when it copied none.
    /// </summary>
    public static string? ShareCheck(IReadOnlyList<CopiedAsset> copied)
    {
        var games = copied
            .Select(c => GameCatalog.Get(c.Game))
            .Where(g => g is { Free: false })
            .GroupBy(g => g!.Title)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} ({g.Count()})"))
            .ToList();
        if (games.Count == 0) return null;
        var list = games.Count == 1 ? games[0] : string.Join(", ", games.Take(games.Count - 1)) + " and " + games[^1];
        return $"Share check: it holds pictures, sounds or objects copied from {list}, which whoever plays it must own. Fine for your own play and friends who own them; before posting it publicly, leave those out.";
    }

    /// <summary>The export for GZDoom (and UZDoom): the project as a .pk3.</summary>
    public const string GZDoom = "gzdoom";

    /// <summary>The project's own bookkeeping, which no engine reads.</summary>
    private static readonly string[] Bookkeeping = [Project.SidecarName, "generator.json"];

    /// <summary>What a project can be exported for, as (id, title, file extension): GZDoom, then each port that plays its game family.</summary>
    public static IReadOnlyList<(string Id, string Title, string Extension)> ChoicesFor(string family) =>
        [(GZDoom, "GZDoom and UZDoom", ".pk3"), .. ClassicPorts.ForFamily(family).Select(p => (p.Id, p.Title, ".wad"))];

    /// <summary>
    /// Writes <paramref name="project"/> exported for <paramref name="exportId"/> (<see cref="GZDoom"/> or a classic port's
    /// id) to <paramref name="outFile"/> and returns what it lost. The engine's data (gzdoom.pk3) is needed for a classic
    /// port: its tables say what each classic type means. <paramref name="game"/> is the base game, when known: its
    /// pictures are checked against, and the project's own pictures it lacks are carried in.
    /// Reads and writes files: call it on a worker.
    /// </summary>
    public static ExportReport Write(Project project, string exportId, string outFile, string? engineData, BaseGame? game = null)
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
            data = Wad(project, port, engineData, game, report);
        }

        // Free downloads (D61): their authors' credits go with the map.
        var downloads = project.CopiedNow().Where(c => c.Kind == "library").Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList();
        if (downloads.Count > 0)
        {
            var names = string.Join(", ", downloads.Take(6)) + (downloads.Count > 6 ? string.Create(CultureInfo.InvariantCulture, $" and {downloads.Count - 6} more") : "");
            var count = downloads.Count == 1 ? "one free download" : string.Create(CultureInfo.InvariantCulture, $"{downloads.Count} free downloads");
            report.Notes.Add(exportId == GZDoom
                ? $"It carries {count} ({names}); their credits are in the .pk3's credits folder, as their authors ask."
                : $"The project has {count} ({names}); their credits are in its credits folder: share them beside the WAD.");
        }

        if (ShareCheck(project.CopiedNow()) is { } check) report.Notes.Add(check);

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
        // A map kept in GZDoom's format is written again by today's writer, so a file an older WadSmith saved (a fractional
        // offsetx GZDoom complains about, 2026-10-13) is clean in the package; a classic map is copied as it is.
        var maps = project.ClassicFormat ? [] : project.Maps().ToDictionary(m => m.File.Replace('\\', '/'), m => m.Slot, StringComparer.OrdinalIgnoreCase);
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
                if (maps.TryGetValue(relative, out var slot))
                {
                    var lumps = MapConversion.ToUdmfLumps(project.LoadMap(slot)).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data));
                    to.Write(WadWriter.Write(WadKind.Pwad, [(slot, ReadOnlyMemory<byte>.Empty), .. lumps]));
                }
                else
                {
                    to.Write(SharedFiles.ReadAllBytes(file));
                }
            }
        }

        foreach (var addOn in project.Info.AddOns)
        {
            report.Add(new ExportLoss(null, "project:add-on", $"Plays over {Path.GetFileName(addOn)}", "file", [], false, "load that file before this one, as WadSmith does"));
        }

        return bytes.ToArray();
    }

    /// <summary>Every map for a classic port, in one .wad, with UMAPINFO and its music for the ports that read it.</summary>
    private static byte[] Wad(Project project, ClassicPort port, string? engineData, BaseGame? game, ExportReport report)
    {
        var family = project.ClassicNamespace;
        if (port.For(family) is null) throw new ArgumentException($"{port.Title} does not play {GameCatalog.Get(project.Info.Game)?.Title ?? project.Info.Game} maps.", nameof(port));
        XlatTranslator? xlat = null;
        if (family != "hexen")
        {
            if (engineData is null || !File.Exists(engineData)) throw new CorruptDataException("Exporting for a classic port needs UZDoom or GZDoom installed: WadSmith reads the engine's own table of what each classic line type does.");
            xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(engineData)]), XlatTranslator.PathFor(family));
        }

        var exMy = GameCatalog.Get(project.Info.Game)?.MapNames == "ExMy";
        var entries = project.Maps().ToList();
        var models = entries.Select(entry =>
        {
            var model = project.LoadMap(entry.Slot);
            return model.Namespace == "zdoom" ? model : MapTranslation.ToZDoom(model, xlat, []);
        }).ToList();

        // The project's own pictures the game lacks, carried in (floors only for the ports that read them from a WAD).
        PictureNames? pictures = null;
        CarriedPictures? carried = null;
        if (game is not null)
        {
            static bool Storable(string n) => n != "-" && n.Length is > 0 and <= 8;
            var walls = models.SelectMany(m => m.Sides).SelectMany(s => new[] { s.Top, s.Bottom, s.Middle }).Where(n => Storable(n) && !game.Pictures.Wall(n));
            var flats = models.SelectMany(m => m.Sectors).SelectMany(s => new[] { s.FloorTexture, s.CeilingTexture }).Where(n => Storable(n) && !game.Pictures.Flat(n));
            carried = PictureCarry.Carry(game, ResourceContainer.Open(project.Folder), walls, flats, flatsToo: port.Id != ClassicPorts.Vanilla);
            var (w, f) = (carried.Walls, carried.Flats);
            pictures = new PictureNames(n => game.Pictures.Wall(n) || w.Contains(n), n => game.Pictures.Flat(n) || f.Contains(n));
            report.Notes.AddRange(carried.Notes);
        }

        var lumps = new List<(string Name, byte[] Data)>();
        for (var i = 0; i < models.Count; i++)
        {
            lumps.AddRange(MapExport.ForPort(models[i], port, family, xlat, report, pictures, exMy, entries[i].Next));

            // A remix carries GZDoom's own repairs to the original (level_compatibility.zs): one may be on the list, and the
            // classic game never needed it (Doom II MAP04's Transfer_Heights, 2026-10-13 sweep).
            if (entries[i].Origin is { Kind: "remix" or "import", Checksum: not null } && report.Losses.Any(l => l.Map == entries[i].Slot))
            {
                report.Notes.Add($"{entries[i].Slot} was remixed from {entries[i].Origin!.Map ?? "a game's map"}: GZDoom's own repairs to the original were made then, and any of them on the list above is a change the classic game never had.");
            }
        }
        if (carried is not null) lumps.AddRange(carried.Lumps);
        MapInfo(project, port, lumps, report, game, engineData);
        ProjectFiles(project, report);
        return WadWriter.Write(WadKind.Pwad, lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
    }

    /// <summary>
    /// The maps' names, music, skies and next maps: a UMAPINFO for the ports that read it (with the music files they
    /// name, when the file is MUS or MIDI), lost for the others.
    /// </summary>
    private static void MapInfo(Project project, ClassicPort port, List<(string Name, byte[] Data)> lumps, ExportReport report, BaseGame? game, string? engineData)
    {
        var maps = project.Maps().Where(m => m.Title.Length > 0 || m.Music is not null || m.Sky is not null || m.Next is not null).ToList();
        if (maps.Count == 0) return;
        if (!port.Umapinfo)
        {
            var played = SlotMusic(project, port, lumps, report, game, engineData, maps);
            foreach (var m in maps)
            {
                var what = new List<string>();
                if (m.Title.Length > 0) what.Add($"name \"{m.Title}\"");
                if (m.Music is not null && !played.Contains(m.Slot)) what.Add($"music {m.Music}");
                if (m.Sky is not null) what.Add($"sky {m.Sky}");
                if (m.Next is not null) what.Add(m.Next == ProjectMapInfo.EndToTitle ? "the game ending after it" : $"next map {m.Next}");
                if (what.Count > 0) report.Add(new ExportLoss(m.Slot, "map:info", $"The map's {string.Join(", ", what)}", "map", [], false, $"{port.Title} has no place for them; the slot's own are used"));
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

    /// <summary>
    /// A map's own music where the port has no place to name it: written under the lump the slot plays by default
    /// (Doom II's MAP01 plays D_RUNNIN), from the project's music file or another of the game's own songs. MUS plays
    /// everywhere; MIDI in every port but the DOS game, which a note says. Returns the slots whose music is carried.
    /// </summary>
    private static HashSet<string> SlotMusic(Project project, ClassicPort port, List<(string Name, byte[] Data)> lumps, ExportReport report, BaseGame? game, string? engineData, IReadOnlyList<ProjectMap> maps)
    {
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (game is null || engineData is null || !File.Exists(engineData) || GameCatalog.Get(project.Info.Game) is not { } definition) return carried;
        var withMusic = maps.Where(m => m.Music is not null).ToList();
        if (withMusic.Count == 0) return carried;
        var definitions = Games.MapInfo.For(definition, game.Stack.Containers, engineData);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in withMusic)
        {
            var standard = definitions.Resolve(definitions.For(m.Slot)?.Text("music"));
            if (standard is null) continue;
            // Lump names are upper case in a WAD; the engines look them up that way.
            var slotLump = game.Lump(standard) is not null ? standard.ToUpperInvariant() : game.Lump("D_" + standard) is not null ? ("D_" + standard).ToUpperInvariant() : null;
            if (slotLump is null) continue;
            var data = MusicFile(project, m.Music!) is { } file ? SharedFiles.ReadAllBytes(file) : game.Lump(m.Music!);
            if (data is null) continue;
            if (!IsMusOrMidi(data))
            {
                report.Add(new ExportLoss(m.Slot, "map:music-format", $"Music {m.Music} is neither MUS nor MIDI", "map", [], false, "the slot's own music plays"));
                carried.Add(m.Slot);
                continue;
            }

            var midi = data[0] == 'M' && data[1] == 'T';
            if (midi && port.VanillaLimits) report.Notes.Add($"{m.Slot}: its music {m.Music} is MIDI: Chocolate Doom and the re-release play it, the DOS game only MUS.");

            if (taken.TryGetValue(slotLump, out var other))
            {
                report.Add(new ExportLoss(m.Slot, "map:music-shared", $"Music {m.Music}: {other} already plays its own under {slotLump}, the music this slot shares with it", "map", [], false, $"{other}'s music plays"));
                carried.Add(m.Slot);
                continue;
            }

            taken[slotLump] = m.Slot;
            lumps.Add((slotLump, data));
            carried.Add(m.Slot);
            report.Notes.Add($"{m.Slot}: its music {m.Music} written as {slotLump}, the music the slot plays.");
        }

        return carried;
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
            string? kind = parts[0].ToLowerInvariant() switch
            {
                // Pictures are carried by the maps' use of them, and each one the game still lacks is listed with its map.
                "textures" or "flats" or "patches" => null,
                "materials" => "Materials (GZDoom's lighting maps)",
                "sprites" => "Pictures of objects brought in from other games or files",
                "sounds" or "sndinfo.txt" => "Sounds",
                "models" or "voxels" or "modeldef.txt" or "voxeldef.txt" => "3D models and voxels",
                "skies" => "Skies from pictures",
                _ => "GZDoom's own definitions",
            };
            if (kind is null) continue;
            (kinds.TryGetValue(kind, out var list) ? list : kinds[kind] = []).Add(relative);
        }

        foreach (var (kind, files) in kinds)
        {
            var shown = string.Join(", ", files.Order(StringComparer.Ordinal).Take(4)) + (files.Count > 4 ? string.Create(CultureInfo.InvariantCulture, $" and {files.Count - 4} more") : "");
            report.Add(new ExportLoss(null, "project:" + kind, kind, "file", [.. Enumerable.Range(0, files.Count)], false, $"not carried into a classic WAD: {shown}"));
        }
    }
}
