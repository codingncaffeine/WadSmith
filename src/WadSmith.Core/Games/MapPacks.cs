using System.IO.Compression;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Games;

/// <summary>A map pack matched to the base games it is for: the file, and the games among those found that it fits.</summary>
public sealed record PackFit(MapFile File, IReadOnlyList<FoundGame> Games);

/// <summary>
/// Map packs (D52): the map files found near the user's games, matched to the base game each is for — by its maps' names
/// (MAPxx against ExMy), their namespace, and the textures they use that the game has — so each is listed under that game's
/// tile, one click from editing. A pack that fits several games as well as each other is listed under each.
/// </summary>
public static partial class MapPacks
{
    /// <summary>
    /// The packs among <paramref name="packs"/> that fit a base game among <paramref name="games"/>, with the games each
    /// fits. <paramref name="namesOf"/> gives the texture and flat names a game file holds (see <see cref="TextureNames"/>);
    /// cache it, a game is asked for once per pack. With a <paramref name="cache"/>, a pack read before is not read again
    /// while its file and the games judged among are the same. A pack that cannot be read is left out.
    /// </summary>
    public static IReadOnlyList<PackFit> Fit(IEnumerable<MapFile> packs, IReadOnlyList<FoundGame> games, Func<string, IReadOnlySet<string>> namesOf, PackCache? cache = null, CancellationToken cancel = default)
    {
        var bases = games.Where(g => g.Usable && g.Game.IsBaseGame && g.Game.Supported && g.Game.MapNames is not null).GroupBy(g => g.Game.Id).Select(g => g.First()).ToList();
        var fits = new List<PackFit>();
        if (bases.Count == 0) return fits;
        var among = PackCache.AmongOf(bases);
        var byId = bases.ToDictionary(g => g.Game.Id, StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(pack.Path);
                var (length, written) = file.Exists ? (file.Length, file.LastWriteTimeUtc) : (-1L, DateTime.MinValue);
                IReadOnlyList<FoundGame> found;
                if (cache is not null && cache.TryGet(pack, length, written, among, out var ids))
                {
                    found = [.. ids.Select(id => byId.GetValueOrDefault(id)).OfType<FoundGame>()];
                }
                else
                {
                    found = GamesFor(pack, bases, namesOf);
                    cache?.Put(pack, length, written, among, found.Select(g => g.Game.Id));
                }

                if (found.Count > 0) fits.Add(new PackFit(pack, found));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or InvalidDataException)
            {
                // A pack that cannot be read is not offered.
            }
        }

        return fits;
    }

    /// <summary>The base games among <paramref name="bases"/> that <paramref name="pack"/> fits; empty when none.</summary>
    public static IReadOnlyList<FoundGame> GamesFor(MapFile pack, IReadOnlyList<FoundGame> bases, Func<string, IReadOnlySet<string>> namesOf)
    {
        var container = Open(pack);
        var maps = MapLocator.Find(container);
        if (maps.Count == 0) return [];

        // By the maps' names: a MAPxx pack is for a MAPxx game, an ExMy pack for an ExMy game.
        var patterns = maps.Select(m => Pattern(m.Name)).OfType<string>().Distinct().ToList();
        var candidates = bases.Where(g => patterns.Contains(g.Game.MapNames!)).ToList();
        if (candidates.Count == 0) return [];

        // By the maps' namespace: a map in the doom, heretic, hexen or strife namespace names its family; one in GZDoom's own
        // (zdoom) can be for any of them, and says nothing.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in maps)
        {
            var model = MapConversion.Read(m, candidates[0].Game.MapNamespace);
            var family = model.Namespace.ToLowerInvariant();
            if (m.Format == MapFormat.Udmf && family is "doom" or "heretic" or "hexen" or "strife") families.Add(family);
            foreach (var side in model.Sides)
            {
                Note(used, side.Top);
                Note(used, side.Middle);
                Note(used, side.Bottom);
            }

            foreach (var sector in model.Sectors)
            {
                Note(used, sector.FloorTexture);
                Note(used, sector.CeilingTexture);
            }
        }

        if (families.Count > 0) candidates = candidates.Where(g => families.Contains(g.Game.MapNamespace)).ToList();
        if (candidates.Count <= 1) return candidates;

        // By the textures: the games that have the most of what the maps use; what the pack brings itself counts for every game.
        used.ExceptWith(TextureNames(container));
        if (used.Count == 0) return candidates;
        var scored = candidates.Select(g => (Game: g, Covered: used.Count(namesOf(g.Path).Contains))).ToList();
        var best = scored.Max(s => s.Covered);
        return [.. scored.Where(s => s.Covered == best).Select(s => s.Game)];
    }

    /// <summary>The wall texture and flat names a file holds: its TEXTURE1 and TEXTURE2, its flats, and its own textures (TX_START, textures/).</summary>
    public static IReadOnlySet<string> TextureNames(ResourceContainer container)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pnames = container.Find("PNAMES");
        IReadOnlyList<NameField>? patchNames = null;
        foreach (var lumpName in new[] { "TEXTURE1", "TEXTURE2" })
        {
            if (container.Find(lumpName) is not { } lump) continue;
            try
            {
                patchNames ??= pnames is null ? [] : TextureLump.ReadPatchNames(pnames.Read(), $"{container.DisplayName} PNAMES");
                foreach (var t in TextureLump.Read(lump.Read(), patchNames, $"{container.DisplayName} {lumpName}").Textures) names.Add(t.Name);
            }
            catch (CorruptDataException)
            {
                // A texture list that cannot be read adds no names.
            }
        }

        foreach (var e in container.Entries)
        {
            if (!e.IsMarker && e.Namespace is ResourceNamespace.Flats or ResourceNamespace.Textures or ResourceNamespace.HiRes) names.Add(e.Name);
        }

        return names;
    }

    /// <summary>The texture and flat names the game file at <paramref name="path"/> holds (see <see cref="TextureNames"/>).</summary>
    public static IReadOnlySet<string> TextureNamesOf(string path) => TextureNames(ResourceContainer.Open(path));

    /// <summary>
    /// The pack as a file on disk the engine can load: the file itself, or, for one inside an archive, a copy taken out
    /// under <paramref name="extractTo"/> (once; a copy already there is kept).
    /// </summary>
    public static string Materialise(MapFile pack, string extractTo)
    {
        if (pack.Inside is null) return pack.Path;
        var dest = Path.Combine(extractTo, Path.GetFileNameWithoutExtension(pack.Path), Path.GetFileName(pack.Inside.Replace('\\', '/'))); // archive path
        using var zip = ZipFile.OpenRead(pack.Path);
        var entry = zip.GetEntry(pack.Inside) ?? throw new FileNotFoundException($"{pack.Inside} is no longer in {pack.Path}.", pack.Path);
        if (!(File.Exists(dest) && new FileInfo(dest).Length == entry.Length))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }

        return dest;
    }

    /// <summary>The pack's WAD, read from disk or out of its archive.</summary>
    public static WadContainer Open(MapFile pack)
    {
        if (pack.Inside is null) return WadContainer.Open(pack.Path);
        using var zip = ZipFile.OpenRead(pack.Path);
        var entry = zip.GetEntry(pack.Inside) ?? throw new FileNotFoundException($"{pack.Inside} is no longer in {pack.Path}.", pack.Path);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return WadContainer.FromBytes(memory.ToArray(), pack.Name);
    }

    /// <summary>"MAPxx" or "ExMy" for a map's name, as the game catalogue names them; null for a name of another shape.</summary>
    public static string? Pattern(string mapName) => MapxxName().IsMatch(mapName) ? "MAPxx" : ExMyName().IsMatch(mapName) ? "ExMy" : null;

    private static void Note(HashSet<string> into, string? texture)
    {
        if (!string.IsNullOrEmpty(texture) && texture != "-") into.Add(texture);
    }

    [GeneratedRegex("^MAP[0-9][0-9]$", RegexOptions.IgnoreCase)]
    private static partial Regex MapxxName();

    [GeneratedRegex("^E[0-9]M[0-9]$", RegexOptions.IgnoreCase)]
    private static partial Regex ExMyName();
}
