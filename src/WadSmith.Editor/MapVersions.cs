using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Preview;
using WadSmith.Core.Projects;

namespace WadSmith.Editor;

/// <summary>A kept copy of a map: its file, its picture, when it was kept and why.</summary>
public sealed record MapVersion(string File, string? Picture, DateTimeOffset When, string Reason);

/// <summary>
/// The copies of a map kept in the project's <c>.wadsmith/versions/SLOT/</c> folder (D24): one when the
/// map opens, one for each "Keep a version", one for each Play (Phase 4), and the map as it was before
/// going back to an older copy. Each is a WAD with the map, a small picture and a line saying why.
/// Everything here reads or writes files: call it on a worker.
/// </summary>
public static class MapVersions
{
    private const string Stamp = "yyyyMMdd-HHmmss-fff";
    private const string OldStamp = "yyyyMMdd-HHmmss";

    public static string Folder(Project project, string slot) => Path.Combine(project.Folder, ".wadsmith", "versions", slot);

    /// <summary>The kept copies, newest first.</summary>
    public static IReadOnlyList<MapVersion> List(Project project, string slot)
    {
        var folder = Folder(project, slot);
        if (!Directory.Exists(folder)) return [];
        var list = new List<MapVersion>();
        foreach (var wad in Directory.EnumerateFiles(folder, "*.wad"))
        {
            var name = Path.GetFileNameWithoutExtension(wad);
            if (!DateTime.TryParseExact(name, [Stamp, OldStamp], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)) continue;
            var png = Path.ChangeExtension(wad, ".png");
            var txt = Path.ChangeExtension(wad, ".txt");
            var reason = File.Exists(txt) ? File.ReadAllText(txt).Trim() : "";
            list.Add(new MapVersion(wad, File.Exists(png) ? png : null, new DateTimeOffset(when), reason));
        }

        return [.. list.OrderByDescending(v => v.When)];
    }

    /// <summary>
    /// Keeps a copy of <paramref name="model"/> with its picture (<paramref name="picture"/> as PNG, or
    /// an automap drawing when none is given) and reason. Returns null, keeping nothing, when the
    /// newest copy already holds exactly this map.
    /// </summary>
    public static MapVersion? Write(Project project, string slot, MapModel model, string reason, DateTimeOffset now, byte[]? picture = null)
    {
        var folder = Folder(project, slot);
        Directory.CreateDirectory(folder);
        var wad = WadWriter.Write(WadKind.Pwad, new[] { (model.Name, (ReadOnlyMemory<byte>)Array.Empty<byte>()) }.Concat(MapConversion.ToUdmfLumps(model).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))));
        if (List(project, slot).FirstOrDefault() is { } newest && File.Exists(newest.File) && File.ReadAllBytes(newest.File).AsSpan().SequenceEqual(wad)) return null;

        var stamp = now.LocalDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
        var file = Path.Combine(folder, $"{stamp}.wad");
        var picturePath = Path.Combine(folder, $"{stamp}.png");
        File.WriteAllBytes(file, wad);
        File.WriteAllBytes(picturePath, picture ?? Core.Graphics.ImageCodec.EncodePng(MapPreview.Render(model, new MapPreview.Options(256, 8))));
        File.WriteAllText(Path.Combine(folder, $"{stamp}.txt"), reason);
        return new MapVersion(file, picturePath, now, reason);
    }

    /// <summary>The map a kept copy holds.</summary>
    public static MapModel Load(MapVersion version)
    {
        var wad = WadContainer.Open(version.File);
        var lumps = MapLocator.Find(wad).FirstOrDefault() ?? throw new CorruptDataException($"{Path.GetFileName(version.File)} holds no map.");
        return MapConversion.Read(lumps);
    }
}
