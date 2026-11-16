using System.IO.Compression;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Games;

/// <summary>A map pack matched to the base games it is for: the file, and the games among those found that it fits.</summary>
public sealed record PackFit(MapFile File, IReadOnlyList<FoundGame> Games)
{
    /// <summary>
    /// The pack beside it that this one is loaded over: a fix for one of another pack's maps, or more maps for it, shows
    /// that pack's textures and is nothing without it. Null for a pack that stands on its own.
    /// </summary>
    public MapFile? Needs { get; init; }
}

/// <summary>
/// Map packs (D52): the map files found near the user's games, matched to the base game each is for — by its maps' names
/// (MAPxx against ExMy), their namespace, and the textures they use that the game has — so each is listed under that game's
/// tile, one click from editing. A pack that fits several games as well as each other is listed under each.
/// </summary>
public static partial class MapPacks
{
    /// <summary>How a pack is judged, as a number raised with every change to it: what was remembered under another is judged again.</summary>
    public const int Rules = 3;

    /// <summary>
    /// How many names in no game and not its own a pack's maps must show (and a tenth of all they show) before it is
    /// taken to lean on another pack. A pack on its own leaves a stray name or two unexplained, and a pack beside it
    /// may happen to hold them.
    /// </summary>
    private const int Leaning = 8;

    /// <summary>
    /// The packs among <paramref name="packs"/> that fit a base game among <paramref name="games"/>, with the games each
    /// fits. <paramref name="namesOf"/> gives the texture and flat names a game file holds (see <see cref="TextureNames"/>);
    /// cache it, a game is asked for once per pack. With a <paramref name="cache"/>, a pack read before is not read again
    /// while its file and the games judged among are the same. A pack that cannot be read is left out. A pack made to be
    /// loaded over another found beside it (in its folder, or in its archive) is matched with that one's textures counted
    /// as brought, and says which it needs (<see cref="PackFit.Needs"/>).
    /// </summary>
    public static IReadOnlyList<PackFit> Fit(IEnumerable<MapFile> packs, IReadOnlyList<FoundGame> games, Func<string, IReadOnlySet<string>> namesOf, PackCache? cache = null, CancellationToken cancel = default)
    {
        var bases = games.Where(g => g.Usable && g.Game.IsBaseGame && g.Game.Supported && g.Game.MapNames is not null).GroupBy(g => g.Game.Id).Select(g => g.First()).ToList();
        var fits = new List<PackFit>();
        if (bases.Count == 0) return fits;
        var among = PackCache.AmongOf(bases);
        var byId = bases.ToDictionary(g => g.Game.Id, StringComparer.Ordinal);

        // The packs beside each other, and what each brings: read only for a pack that leans on names it does not hold.
        var all = packs as IReadOnlyList<MapFile> ?? [.. packs];
        var places = all.GroupBy(Place, WindowsFolders.PathComparer).ToDictionary(g => g.Key, g => (IReadOnlyList<MapFile>)[.. g], WindowsFolders.PathComparer);
        var byKey = new Dictionary<string, MapFile>(StringComparer.Ordinal);
        foreach (var p in all) byKey.TryAdd(PackCache.KeyOf(p), p);
        var brought = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        IReadOnlySet<string> Brings(MapFile other)
        {
            var key = PackCache.KeyOf(other);
            if (brought.TryGetValue(key, out var names)) return names;
            try
            {
                names = TextureNames(Open(other));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or InvalidDataException)
            {
                names = new HashSet<string>();
            }

            return brought[key] = names;
        }

        foreach (var pack in all)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(pack.Path);
                var (length, written) = file.Exists ? (file.Length, file.LastWriteTimeUtc) : (-1L, DateTime.MinValue);
                var beside = places[Place(pack)];
                IReadOnlyList<FoundGame> found;
                MapFile? needs = null;
                if (cache is not null && cache.TryGet(pack, length, written, among, () => PackCache.BesideOf(beside, pack), out var record))
                {
                    found = [.. record.Games.Select(id => byId.GetValueOrDefault(id)).OfType<FoundGame>()];
                    if (record.Needs is { } key) needs = byKey.GetValueOrDefault(key);
                }
                else
                {
                    (found, needs, var leans) = Judge(pack, bases, namesOf, [.. beside.Where(b => !ReferenceEquals(b, pack))], Brings);
                    cache?.Put(pack, length, written, among, found.Select(g => g.Game.Id), needs is null ? null : PackCache.KeyOf(needs), leans ? PackCache.BesideOf(beside, pack) : null);
                }

                if (found.Count > 0) fits.Add(new PackFit(pack, found) { Needs = needs });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or InvalidDataException)
            {
                // A pack that cannot be read is not offered.
            }
        }

        return fits;
    }

    /// <summary>Where a pack lies, for telling which packs are beside it: its folder, or the archive it is inside.</summary>
    private static string Place(MapFile pack) => pack.Inside is null ? Path.GetDirectoryName(pack.Path) ?? "" : pack.Path;

    /// <summary>The base games among <paramref name="bases"/> that <paramref name="pack"/> fits, judged on its own; empty when none.</summary>
    public static IReadOnlyList<FoundGame> GamesFor(MapFile pack, IReadOnlyList<FoundGame> bases, Func<string, IReadOnlySet<string>> namesOf) =>
        Judge(pack, bases, namesOf, [], _ => new HashSet<string>()).Games;

    /// <summary>
    /// The base games among <paramref name="bases"/> that <paramref name="pack"/> fits, and the pack among those
    /// <paramref name="beside"/> it that it is loaded over, when it is made for one: much of what its maps show is then
    /// neither its own nor any game's, and that one pack brings nearly all of it (<paramref name="brings"/> gives the
    /// names a pack holds). Leans says the maps show that much unexplained, whether or not a pack beside it explains it:
    /// such a pack is judged again when the packs beside it change.
    /// </summary>
    public static (IReadOnlyList<FoundGame> Games, MapFile? Needs, bool Leans) Judge(MapFile pack, IReadOnlyList<FoundGame> bases, Func<string, IReadOnlySet<string>> namesOf, IReadOnlyList<MapFile> beside, Func<MapFile, IReadOnlySet<string>> brings)
    {
        var container = Open(pack);
        var maps = MapLocator.Find(container);
        if (maps.Count == 0) return ([], null, false);

        // By the maps' names: a MAPxx pack is for a MAPxx game, an ExMy pack for an ExMy game.
        var patterns = maps.Select(m => Pattern(m.Name)).OfType<string>().Distinct().ToList();
        var candidates = bases.Where(g => patterns.Contains(g.Game.MapNames!)).ToList();
        if (candidates.Count == 0) return ([], null, false);

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
        if (candidates.Count == 0) return ([], null, false);

        // What the pack brings itself counts for every game.
        var everything = used.ToList();
        used.ExceptWith(TextureNames(container));

        // A pack made to be loaded over another (a fix for one of its maps, more maps for it) shows that pack's textures.
        // When much of what its maps show is neither its own nor any game's, and one pack beside it brings nearly all of
        // that, it needs that pack, and what that pack brings counts as brought too.
        MapFile? needs = null;
        var unexplained = used.Where(n => !candidates.Any(g => namesOf(g.Path).Contains(n))).ToList();
        var leans = unexplained.Count >= Math.Max(Leaning, (everything.Count + 9) / 10);
        if (leans && beside.Count > 0)
        {
            var (other, held) = beside.Select(b => (File: b, Held: unexplained.Count(brings(b).Contains))).MaxBy(b => b.Held);
            if (held * 10 >= unexplained.Count * 9)
            {
                needs = other;
                used.ExceptWith(brings(other));
            }
        }

        if (candidates.Count == 1) return (candidates, needs, leans);

        // By the textures: the games that have the most of what the maps use.
        var scored = candidates.Select(g => (Game: g, Covered: used.Count(namesOf(g.Path).Contains))).ToList();
        var best = scored.Max(s => s.Covered);
        return (best == 0 ? Family(candidates, everything, namesOf) : [.. scored.Where(s => s.Covered == best).Select(s => s.Game)], needs, leans);
    }

    /// <summary>
    /// The games of a pack's family, when nothing its maps use is left to tell the games apart (it is all the pack's own, or
    /// no game's): a pack that adds to a game's textures repeats that game's list, so its maps go on showing that game's
    /// names. The games holding at least half as many of <paramref name="names"/> as the one holding most; every game
    /// when none holds any.
    /// </summary>
    private static IReadOnlyList<FoundGame> Family(IReadOnlyList<FoundGame> candidates, IReadOnlyList<string> names, Func<string, IReadOnlySet<string>> namesOf)
    {
        var scored = candidates.Select(g => (Game: g, Held: names.Count(namesOf(g.Path).Contains))).ToList();
        var most = scored.Max(s => s.Held);
        return most == 0 ? candidates : [.. scored.Where(s => s.Held * 2 >= most).Select(s => s.Game)];
    }

    /// <summary>
    /// The names a file holds that a wall, floor or ceiling can show: its TEXTURE1 and TEXTURE2, its flats, its own textures
    /// (TX_START, textures/), and its patches: GZDoom makes a wall texture of every patch (those between P_START and P_END
    /// or under patches/, and those PNAMES lists that the file holds), so a map may show one by its own name.
    /// </summary>
    public static IReadOnlySet<string> TextureNames(ResourceContainer container)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<NameField> patchNames = [];
        if (container.Find("PNAMES") is { } pnames)
        {
            try
            {
                patchNames = TextureLump.ReadPatchNames(pnames.Read(), $"{container.DisplayName} PNAMES");
            }
            catch (CorruptDataException)
            {
                // A patch list that cannot be read names no patches.
            }
        }

        foreach (var lumpName in new[] { "TEXTURE1", "TEXTURE2" })
        {
            if (container.Find(lumpName) is not { } lump) continue;
            try
            {
                foreach (var t in TextureLump.Read(lump.Read(), patchNames, $"{container.DisplayName} {lumpName}").Textures) names.Add(t.Name);
            }
            catch (CorruptDataException)
            {
                // A texture list that cannot be read adds no names.
            }
        }

        foreach (var e in container.Entries)
        {
            if (!e.IsMarker && e.Namespace is ResourceNamespace.Flats or ResourceNamespace.Textures or ResourceNamespace.HiRes or ResourceNamespace.Patches) names.Add(e.Name);
        }

        // A patch a WAD keeps outside the markers is found by its name in PNAMES.
        foreach (var p in patchNames)
        {
            if (p.Text.Length > 0 && container.Find(p.Text) is { IsMarker: false, Size: > 0 }) names.Add(p.Text);
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
