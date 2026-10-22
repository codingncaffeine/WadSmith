using System.Security.Cryptography;
using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Preview;

namespace WadSmith.Core.Games;

/// <summary>
/// The picture on a map set's card on the start screen: the title screen of the first of its packs that has one (drawn in
/// the pack's own palette, else the game's), else its first map drawn as the automap shows it. Made once and kept in a folder.
/// </summary>
public static class SetPictures
{
    /// <summary>A card's picture: the PNG file, and whether it is a title screen (it fills the card) or a drawn map (it sits whole).</summary>
    public sealed record Picture(string File, bool TitleScreen);

    /// <summary>How many of a collection's packs are looked in for a title screen: a collection can hold a thousand.</summary>
    public const int Looked = 8;

    /// <summary>
    /// The picture for the set made of <paramref name="packs"/> (by name, the first <see cref="Looked"/>), from the folder when
    /// made before, else made and kept there. Null when no pack has a title screen or a map that can be drawn.
    /// Reads whole packs: call it on a worker.
    /// </summary>
    public static Picture? For(IReadOnlyList<MapFile> packs, string gamePath, string mapNamespace, string folder)
    {
        var looked = packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(Looked).ToList();
        if (looked.Count == 0) return null;
        var key = Key(looked);
        var title = Path.Combine(folder, key + ".title.png");
        var map = Path.Combine(folder, key + ".map.png");
        var none = Path.Combine(folder, key + ".none");
        if (File.Exists(title)) return new Picture(title, true);
        if (File.Exists(map)) return new Picture(map, false);
        if (File.Exists(none)) return null;

        Directory.CreateDirectory(folder);
        var (png, isTitle) = Make(looked, gamePath, mapNamespace);
        if (png is null)
        {
            File.WriteAllBytes(none, []);
            return null;
        }

        var file = isTitle ? title : map;
        var temp = file + ".part";
        File.WriteAllBytes(temp, png);
        File.Move(temp, file, overwrite: true);
        return new Picture(file, isTitle);
    }

    /// <summary>The picture as PNG and whether it is a title screen; null when there is none.</summary>
    public static (byte[]? Png, bool TitleScreen) Make(IReadOnlyList<MapFile> packs, string gamePath, string mapNamespace)
    {
        ResourceContainer? game = null;
        WadContainer? first = null;
        foreach (var pack in packs)
        {
            WadContainer wad;
            try
            {
                wad = MapPacks.Open(pack);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or InvalidDataException)
            {
                continue;
            }

            first ??= wad;
            if (wad.Find("TITLEPIC") is not { } e) continue;
            try
            {
                game ??= ResourceContainer.Open(gamePath);
                var palette = PictureLoader.PaletteOf(new ResourceStack([game, wad]));
                return (ImageCodec.EncodePng(PictureLoader.Load(e.Read(), e.Namespace, palette, "TITLEPIC").Image), true);
            }
            catch (Exception ex) when (ex is IOException or CorruptDataException or InvalidDataException)
            {
                // A title screen that cannot be read: the next pack's, else a drawn map.
            }
        }

        if (first is null) return (null, false);

        // The biggest of its first few maps: an add-on's first can be a small one (Deathkings' MAP33). A pack whose maps are
        // all empty (sounds or graphics with a stand-in MAP01) has no picture.
        MapModel? biggest = null;
        foreach (var lumps in MapLocator.Find(first).Take(DrawnFrom))
        {
            try
            {
                var model = MapConversion.Read(lumps, mapNamespace);
                if (model.Lines.Count > (biggest?.Lines.Count ?? 0)) biggest = model;
            }
            catch (Exception ex) when (ex is CorruptDataException or InvalidDataException)
            {
            }
        }

        return biggest is null ? (null, false) : (ImageCodec.EncodePng(MapPreview.Render(biggest, new MapPreview.Options(Size: 320, Margin: 14))), false);
    }

    /// <summary>How many of a pack's maps are looked at for the one to draw.</summary>
    private const int DrawnFrom = 6;

    /// <summary>The packs' names, sizes and times, hashed: a pack changed or replaced gets a new picture.</summary>
    private static string Key(IReadOnlyList<MapFile> packs)
    {
        var text = new StringBuilder("2");
        foreach (var p in packs)
        {
            var info = new FileInfo(p.Path);
            text.Append('|').Append(p.Path).Append('|').Append(p.Inside).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }

        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
