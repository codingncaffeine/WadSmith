using System.Collections.Concurrent;
using WadSmith.Core.Containers;
using WadSmith.Core.Exporting;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.App;

/// <summary>One line of an export's report, with the elements of the map being edited it is about (none for another map or the project).</summary>
public sealed record ExportRow(ExportLoss Loss, IReadOnlyList<Element> Show);

/// <summary>An export already written into WadSmith's cache: what it lost, row by row, and the file the user saves.</summary>
public sealed record PreparedExport(string ExportId, string File, string Extension, ExportReport Report, IReadOnlyList<ExportRow> Rows);

/// <summary>
/// Export from the editor (Phase 12): the project is saved, exported into the cache on a worker, and its report shown —
/// so the file the user saves is the one the report was read from. Each line about the open map carries that map's own
/// walls, rooms and objects, for the Show button.
/// </summary>
public static class Exports
{
    /// <summary>The base game, read once per set of game files.</summary>
    private static readonly ConcurrentDictionary<string, BaseGame> Games = new(StringComparer.Ordinal);

    /// <summary>The base game of <paramref name="files"/> (the IWAD, then an add-on over it), or null without them.</summary>
    public static BaseGame? GameOf(IReadOnlyList<string> files) =>
        files.Count == 0 ? null : Games.GetOrAdd(string.Join('|', files), _ => new BaseGame(new ResourceStack(files.Select(ResourceContainer.Open))));

    /// <summary>
    /// Saves the map, then exports the project for <paramref name="exportId"/> into <paramref name="folder"/> on a worker.
    /// Call on the UI thread (it takes the map's frame there).
    /// </summary>
    public static async Task<PreparedExport> PrepareAsync(EditorSession session, string exportId, string folder)
    {
        var frame = session.Map.Frame;
        var slot = session.Slot;
        await session.FlushAsync().ConfigureAwait(false);
        var project = session.Project;
        var engineData = session.Hooks.EngineData;
        var files = session.Hooks.Resources?.Files ?? [];
        var extension = ProjectExport.ChoicesFor(project.ClassicNamespace).FirstOrDefault(c => c.Id == exportId).Extension ?? ".wad";
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "export-" + exportId + extension);
            var report = ProjectExport.Write(project, exportId, file, engineData, exportId == ProjectExport.GZDoom ? null : GameOf(files));
            return new PreparedExport(exportId, file, extension, report, Rows(report, frame, slot));
        }).ConfigureAwait(false);
    }

    /// <summary>The report's lines with the open map's elements: walls, rooms and objects by their place in the saved map, a side by its wall.</summary>
    public static IReadOnlyList<ExportRow> Rows(ExportReport report, MapFrame frame, string slot)
    {
        var lines = frame.Lines.Where(l => l.Element is not null).Select(l => (Element)l.Element!).ToList();
        var sectors = frame.Sectors.Where(s => s.Element is not null).Select(s => (Element)s.Element!).ToList();
        var things = frame.Things.Where(t => t.Element is not null).Select(t => (Element)t.Element!).ToList();
        Dictionary<int, List<Element>>? sideLines = null;
        IReadOnlyList<Element> Show(ExportLoss loss)
        {
            if (!string.Equals(loss.Map, slot, StringComparison.OrdinalIgnoreCase)) return [];
            static IEnumerable<Element> At(List<Element> list, IEnumerable<int> indexes) => indexes.Where(i => i >= 0 && i < list.Count).Select(i => list[i]);
            switch (loss.On)
            {
                case "wall": return [.. At(lines, loss.Indexes)];
                case "room": return [.. At(sectors, loss.Indexes)];
                case "object": return [.. At(things, loss.Indexes)];
                case "side":
                    sideLines ??= SideLines(frame, lines);
                    return [.. loss.Indexes.SelectMany(i => sideLines.GetValueOrDefault(i) ?? []).Distinct()];
                default: return [];
            }
        }

        return [.. report.Losses.Select(l => new ExportRow(l, Show(l)))];
    }

    /// <summary>Each side of the saved map and the walls it belongs to (the saved map's sides are the frame's, packed).</summary>
    private static Dictionary<int, List<Element>> SideLines(MapFrame frame, List<Element> lines)
    {
        var model = frame.ToModel();
        var map = new Dictionary<int, List<Element>>();
        for (var i = 0; i < model.Lines.Count && i < lines.Count; i++)
        {
            foreach (var side in new[] { model.Lines[i].Front, model.Lines[i].Back }.Where(s => s >= 0))
            {
                (map.TryGetValue(side, out var list) ? list : map[side] = []).Add(lines[i]);
            }
        }

        return map;
    }
}
