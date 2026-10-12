using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WadSmith.Core.Audio;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Assets;

/// <summary>The folders the extractor writes, one per kind of asset.</summary>
public enum AssetKind
{
    Textures,
    Floors,
    Patches,
    Sprites,
    Graphics,
    Sounds,
    Music,
    Maps,
    Palette,
    Other,
}

/// <summary>One extracted file and the lump it came from.</summary>
public sealed record ExtractedAsset(string Path, AssetKind Kind, string Lump, string Group, string Container);

public sealed class ExtractResult
{
    public List<ExtractedAsset> Assets { get; } = [];

    public Dictionary<AssetKind, int> Counts => Assets.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Things that could not be extracted as what they claimed to be; each was saved raw instead, or skipped with a reason.</summary>
    public List<string> Problems { get; } = [];
}

/// <summary>
/// Turns a WAD, PK3 or folder into plain files: textures, floors, patches, sprites and graphics as
/// PNG (Doom offsets kept in the grAb chunk), sounds as WAV, MUS music as MIDI (other formats
/// copied), each map as its own WAD, the palette as a swatch, and everything else raw. Nothing is
/// silently dropped: a lump that is not what it seems is saved raw and listed in the problems. A
/// <c>manifest.json</c> records where every file came from.
/// </summary>
public static class AssetExtractor
{
    /// <param name="source">The file to extract.</param>
    /// <param name="context">Everything loaded with it, base game first; the source must be the last container. The palette and any patches its textures borrow come from here.</param>
    /// <param name="outputFolder">Where to write; created if missing.</param>
    /// <param name="only">Limit to these kinds, or null for all.</param>
    /// <param name="progress">Told how many of the files are written so far.</param>
    /// <param name="cancel">Stops the extraction between files.</param>
    public static ExtractResult Extract(ResourceContainer source, ResourceStack context, string outputFolder, IReadOnlySet<AssetKind>? only = null, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        if (context.Containers.Count == 0 || context.Containers[^1] != source) throw new ArgumentException("The source must be the last container of the context.", nameof(context));
        Directory.CreateDirectory(outputFolder);
        var result = new ExtractResult();
        var palette = PictureLoader.PaletteOf(context);
        var work = new List<(AssetKind Kind, Func<ExtractedAsset?> Run)>();
        bool Want(AssetKind k) => only is null || only.Contains(k);
        var problems = new ConcurrentBag<string>();
        var claimed = new HashSet<ResourceEntry>();

        string Out(AssetKind kind, string relative)
        {
            var full = Path.Combine(outputFolder, kind.ToString(), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return full;
        }

        ExtractedAsset Record(AssetKind kind, string fullPath, ResourceEntry? e, string lump) =>
            new(Path.GetRelativePath(outputFolder, fullPath).Replace('\\', '/'), kind, lump, e?.Namespace.ToString() ?? "", source.DisplayName);

        // Maps: each as a WAD holding its marker and lumps.
        var mapLumpEntries = new HashSet<ResourceEntry>();
        var maps = MapLocator.Find(source);
        if (source is WadContainer)
        {
            var entries = source.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                if (!maps.Any(m => LumpName.Comparer.Equals(m.Name, entries[i].Name))) continue;
                var map = maps.First(m => LumpName.Comparer.Equals(m.Name, entries[i].Name));
                mapLumpEntries.Add(entries[i]);
                for (var j = 1; j <= map.Lumps.Count && i + j < entries.Count; j++) mapLumpEntries.Add(entries[i + j]);
            }
        }
        else
        {
            foreach (var e in source.Entries.Where(e => e.Namespace == ResourceNamespace.Maps)) mapLumpEntries.Add(e);
        }

        claimed.UnionWith(mapLumpEntries);
        if (Want(AssetKind.Maps))
        {
            foreach (var m in maps)
            {
                var map = m;
                work.Add((AssetKind.Maps, () =>
                {
                    var path = Out(AssetKind.Maps, SafeName(map.Name) + ".wad");
                    var lumps = new List<(string, ReadOnlyMemory<byte>)> { (map.Name, Array.Empty<byte>()) };
                    lumps.AddRange(map.Lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
                    File.WriteAllBytes(path, WadWriter.Write(WadKind.Pwad, lumps));
                    return Record(AssetKind.Maps, path, null, map.Name);
                }));
            }
        }

        // Textures defined in this file, composed with everything loaded.
        var library = new TextureLibrary(context);
        foreach (var t in library.Walls.Where(t => t.Source == source))
        {
            if (t.Entry is not null) claimed.Add(t.Entry);
            if (!Want(AssetKind.Textures)) continue;
            var tex = t;
            work.Add((AssetKind.Textures, () =>
            {
                var img = library.GetWall(tex.Name);
                if (img is null)
                {
                    problems.Add($"Texture {tex.Name} could not be built.");
                    return null;
                }

                var path = Out(AssetKind.Textures, SafeName(tex.Name) + ".png");
                File.WriteAllBytes(path, ImageCodec.EncodePng(img));
                return Record(AssetKind.Textures, path, tex.Entry, tex.Name);
            }));
        }

        foreach (var name in new[] { "TEXTURE1", "TEXTURE2", "PNAMES", "TEXTURES" })
        {
            foreach (var e in source.Entries.Where(e => e.Namespace == ResourceNamespace.Global && LumpName.Comparer.Equals(e.Name, name)))
            {
                // The definitions themselves are kept too, so the textures can be rebuilt exactly.
                AddRaw(e);
                claimed.Add(e);
            }
        }

        // Pictures by group; global pictures are only those that read as one.
        foreach (var e in source.Entries)
        {
            if (e.IsMarker || claimed.Contains(e)) continue;
            var entry = e;
            switch (e.Namespace)
            {
                case ResourceNamespace.Flats:
                    claimed.Add(e);
                    if (Want(AssetKind.Floors)) AddPicture(AssetKind.Floors, entry, SafeName(entry.Name) + ".png");
                    break;
                case ResourceNamespace.Patches:
                    claimed.Add(e);
                    if (Want(AssetKind.Patches)) AddPicture(AssetKind.Patches, entry, SafeName(entry.Name) + ".png");
                    break;
                case ResourceNamespace.Sprites:
                    claimed.Add(e);
                    if (Want(AssetKind.Sprites))
                    {
                        var folder = SpriteFrameName.TryParse(entry.Name, out var n) ? n.Sprite : "Other";
                        AddPicture(AssetKind.Sprites, entry, $"{SafeName(folder)}/{SafeName(entry.Name)}.png");
                    }

                    break;
                case ResourceNamespace.Textures:
                case ResourceNamespace.HiRes:
                    claimed.Add(e);
                    if (Want(AssetKind.Textures)) AddPicture(AssetKind.Textures, entry, (e.Namespace == ResourceNamespace.HiRes ? "HiRes/" : "") + SafeName(entry.Name) + ".png");
                    break;
            }
        }

        // Palette and colormap.
        foreach (var e in source.Entries.Where(e => e.Namespace == ResourceNamespace.Global && (LumpName.Comparer.Equals(e.Name, "PLAYPAL") || LumpName.Comparer.Equals(e.Name, "COLORMAP"))))
        {
            claimed.Add(e);
            if (!Want(AssetKind.Palette)) continue;
            var entry = e;
            work.Add((AssetKind.Palette, () =>
            {
                var data = entry.Read();
                var isPal = LumpName.Comparer.Equals(entry.Name, "PLAYPAL");
                var img = isPal ? PaletteSwatch(data) : ColormapImage(data, palette);
                var path = Out(AssetKind.Palette, (isPal ? "palette" : "colormap") + ".png");
                File.WriteAllBytes(path, ImageCodec.EncodePng(img));
                return Record(AssetKind.Palette, path, entry, entry.Name);
            }));
            AddRaw(entry);
        }

        // Everything left: sounds, music, global pictures, raw.
        foreach (var e in source.Entries)
        {
            if (e.IsMarker || claimed.Contains(e)) continue;
            var entry = e;
            work.Add((AssetKind.Other, () => ClassifyAndWrite(entry)));
        }

        // Run everything in parallel; each item writes its own file.
        var done = 0;
        var outputs = new ConcurrentBag<ExtractedAsset>();
        Parallel.ForEach(work, new ParallelOptions { CancellationToken = cancel }, item =>
        {
            try
            {
                var asset = item.Run();
                if (asset is not null) outputs.Add(asset);
            }
            catch (Exception ex) when (ex is CorruptDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add(ex.Message);
            }

            progress?.Report((Interlocked.Increment(ref done), work.Count));
        });

        result.Assets.AddRange(outputs.OrderBy(a => a.Path, StringComparer.Ordinal));
        result.Problems.AddRange(problems.OrderBy(p => p, StringComparer.Ordinal));
        result.Problems.AddRange(library.Notes.Where(n => n.Contains(source.DisplayName, StringComparison.Ordinal)).Distinct());
        File.WriteAllText(Path.Combine(outputFolder, "manifest.json"), JsonSerializer.Serialize(new { source = source.DisplayName, assets = result.Assets, problems = result.Problems }, ManifestJson));
        return result;

        void AddPicture(AssetKind kind, ResourceEntry entry, string relative)
        {
            work.Add((kind, () =>
            {
                var data = entry.Read();
                try
                {
                    var (img, _, _) = PictureLoader.Load(data, entry.Namespace, palette, $"{source.DisplayName} {entry.FullPath}");
                    var path = Out(kind, relative);
                    File.WriteAllBytes(path, ImageCodec.EncodePng(img));
                    return Record(kind, path, entry, entry.Name);
                }
                catch (CorruptDataException ex)
                {
                    problems.Add($"{ex.Message} It was saved as it is in Other.");
                    return WriteRaw(entry, data);
                }
            }));
        }

        void AddRaw(ResourceEntry entry) => work.Add((AssetKind.Other, () => WriteRaw(entry, entry.Read())));

        ExtractedAsset WriteRaw(ResourceEntry entry, byte[] data)
        {
            var ext = Path.GetExtension(entry.FullPath);
            if (string.IsNullOrEmpty(ext) || source is WadContainer) ext = LooksLikeText(data) ? ".txt" : ".lmp";
            var path = Out(AssetKind.Other, SafeName(entry.Name) + Suffix(entry) + ext);
            File.WriteAllBytes(path, data);
            return Record(AssetKind.Other, path, entry, entry.Name);
        }

        // Lumps with the same name in one WAD: only the last is used by the engine, but none is dropped.
        string Suffix(ResourceEntry entry)
        {
            var same = source.Entries.Where(x => !x.IsMarker && x.Namespace == entry.Namespace && LumpName.Comparer.Equals(x.Name, entry.Name)).ToList();
            return same.Count > 1 && same[^1] != entry ? $" (copy {same.IndexOf(entry) + 1})" : "";
        }

        ExtractedAsset? ClassifyAndWrite(ResourceEntry entry)
        {
            var data = entry.Read();
            if (data.Length == 0) return null;
            var audio = AudioFormats.Detect(data);
            var isMusic = audio is AudioKind.Mus or AudioKind.Midi or AudioKind.Module
                || (audio is AudioKind.Ogg or AudioKind.Mp3 or AudioKind.Flac && (entry.Namespace == ResourceNamespace.Music || entry.Name.StartsWith("D_", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith("MUS_", StringComparison.OrdinalIgnoreCase)));

            if (isMusic && Want(AssetKind.Music))
            {
                var bytes = audio == AudioKind.Mus ? MusToMidi.Convert(data, entry.Name) : data;
                var path = Out(AssetKind.Music, SafeName(entry.Name) + Suffix(entry) + AudioFormats.Extension(audio, data));
                File.WriteAllBytes(path, bytes);
                return Record(AssetKind.Music, path, entry, entry.Name);
            }

            if (!isMusic && audio is AudioKind.DmxSound or AudioKind.Wav or AudioKind.Ogg or AudioKind.Mp3 or AudioKind.Flac && Want(AssetKind.Sounds))
            {
                var bytes = audio == AudioKind.DmxSound ? AudioFormats.DmxToWav(data) : data;
                var sub = entry.Namespace == ResourceNamespace.Voices ? "Voices/" : "";
                var path = Out(AssetKind.Sounds, sub + SafeName(entry.Name) + Suffix(entry) + AudioFormats.Extension(audio, data));
                File.WriteAllBytes(path, bytes);
                return Record(AssetKind.Sounds, path, entry, entry.Name);
            }

            if (audio == AudioKind.None && entry.Namespace is ResourceNamespace.Global or ResourceNamespace.Graphics && (ImageCodec.IsKnownImage(data) || DoomPicture.IsValid(data) || (RawPicture.FullscreenWidth(data.Length) > 0 && entry.Namespace == ResourceNamespace.Global)))
            {
                if (!Want(AssetKind.Graphics)) return null;
                try
                {
                    var (img, _, _) = PictureLoader.Load(data, entry.Namespace, palette, entry.FullPath);
                    var path = Out(AssetKind.Graphics, SafeName(entry.Name) + Suffix(entry) + ".png");
                    File.WriteAllBytes(path, ImageCodec.EncodePng(img));
                    return Record(AssetKind.Graphics, path, entry, entry.Name);
                }
                catch (CorruptDataException)
                {
                    // Falls through to raw.
                }
            }

            return Want(AssetKind.Other) ? WriteRaw(entry, data) : null;
        }
    }

    /// <summary>A lump name as a file name: GZDoom's ^ for the \ some sprite frames use, and _ for characters file systems refuse.</summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c switch
            {
                '\\' => '^',
                '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => '_',
                _ when c < ' ' => '_',
                _ => c,
            });
        }

        return sb.ToString();
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;
        foreach (var b in data[..Math.Min(data.Length, 4096)])
        {
            if (b is < 0x20 and not (0x09 or 0x0A or 0x0D) || b == 0x7F) return false;
        }

        return true;
    }

    /// <summary>The first palette as a 16×16 grid of 16-pixel swatches, index 0 at the top left.</summary>
    private static RgbaImage PaletteSwatch(byte[] playpal)
    {
        var pal = new Palette(playpal);
        var img = new RgbaImage(256, 256);
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = pal[i];
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++) img.Set(i % 16 * 16 + x, i / 16 * 16 + y, r, g, b);
            }
        }

        return img;
    }

    /// <summary>The light-level tables as rows: one row per table, 256 colours across.</summary>
    private static RgbaImage ColormapImage(byte[] colormap, Palette palette)
    {
        var rows = Math.Max(1, colormap.Length / 256);
        var img = new RgbaImage(256, rows);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var (r, g, b) = palette[colormap[y * 256 + x]];
                img.Set(x, y, r, g, b);
            }
        }

        return img;
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
