using System.Collections.Concurrent;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Graphics;

public enum TextureKind
{
    /// <summary>Built from patches (TEXTURE1/TEXTURE2 or TEXTURES).</summary>
    Composite,
    /// <summary>A single picture: TX_START markers, the textures/ directory, hi-res images.</summary>
    Single,
    /// <summary>A floor or ceiling picture.</summary>
    Flat,
}

/// <summary>A texture or flat the loaded files make available.</summary>
public sealed record TextureInfo(string Name, TextureKind Kind, int Width, int Height, string Origin, ResourceContainer Source)
{
    internal CompositeTexture? Composite { get; init; }

    internal ResourceEntry? Entry { get; init; }

    public bool IsNull => Composite?.IsNull == true;
}

/// <summary>
/// Every wall texture and flat a resource stack provides, with the engine's precedence: per file in
/// load order, TEXTURE1/TEXTURE2 first, then single-picture textures, then TEXTURES definitions; a
/// later definition of a name replaces an earlier one. Pictures are composed on first use and cached.
/// </summary>
public sealed class TextureLibrary
{
    private readonly ResourceStack _stack;
    private readonly Palette _palette;
    // Concurrent: pictures are drawn on workers while a texture copied into the project is added (Add).
    private readonly ConcurrentDictionary<string, TextureInfo> _walls = new(LumpName.Comparer);
    private readonly ConcurrentDictionary<string, TextureInfo> _flats = new(LumpName.Comparer);
    private readonly ConcurrentDictionary<(bool Flat, string Name), RgbaImage?> _cache = new();
    private readonly ConcurrentDictionary<string, RgbaImage?> _patchCache = new(LumpName.Comparer);

    public TextureLibrary(ResourceStack stack)
    {
        _stack = stack;
        _palette = PictureLoader.PaletteOf(stack);
        foreach (var container in stack.Containers) AddContainer(container);
    }

    public Palette Palette => _palette;

    public IReadOnlyCollection<TextureInfo> Walls => (IReadOnlyCollection<TextureInfo>)_walls.Values;

    public IReadOnlyCollection<TextureInfo> Flats => (IReadOnlyCollection<TextureInfo>)_flats.Values;

    /// <summary>Problems met while reading definitions: unreadable lumps, missing patches, effects not drawn.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>
    /// Adds one picture file (from the textures/ or flats/ folder of the project) that was written after the library was
    /// read, such as a texture copied in from another game. Safe while other threads draw.
    /// </summary>
    public void Add(ResourceEntry entry)
    {
        if (entry.Namespace == ResourceNamespace.Textures) _walls[entry.Name] = SingleInfo(entry, TextureKind.Single);
        else if (entry.Namespace == ResourceNamespace.Flats) _flats[entry.Name] = SingleInfo(entry, TextureKind.Flat);
        else throw new ArgumentException($"{entry.FullPath} is not in a textures or flats folder.", nameof(entry));
    }

    /// <summary>Whether a wall texture or a flat has this name.</summary>
    public bool Has(string name) => _walls.ContainsKey(name) || _flats.ContainsKey(name);

    public TextureInfo? FindWall(string name) => _walls.GetValueOrDefault(name);

    public TextureInfo? FindFlat(string name) => _flats.GetValueOrDefault(name);

    /// <summary>A wall texture's picture; GZDoom also lets a flat stand in when no wall texture has the name.</summary>
    public RgbaImage? GetWall(string name)
    {
        if (name is "" or "-") return null;
        if (_walls.TryGetValue(name, out var info)) return _cache.GetOrAdd((false, info.Name), _ => Render(info));
        return _flats.TryGetValue(name, out var flat) ? _cache.GetOrAdd((true, flat.Name), _ => Render(flat)) : null;
    }

    /// <summary>A flat's picture; a wall texture stands in when no flat has the name, as in GZDoom.</summary>
    public RgbaImage? GetFlat(string name)
    {
        if (name is "" or "-") return null;
        if (_flats.TryGetValue(name, out var info)) return _cache.GetOrAdd((true, info.Name), _ => Render(info));
        return _walls.TryGetValue(name, out var wall) ? _cache.GetOrAdd((false, wall.Name), _ => Render(wall)) : null;
    }

    /// <summary>
    /// Whether a wall texture has a column that two or more patches cover: vanilla Doom builds such a column as one
    /// composite, which a see-through middle cannot draw (the Medusa effect). False for an unknown texture or a single
    /// picture.
    /// </summary>
    public bool StacksPatches(string name) => _walls.TryGetValue(name, out var info) && info.Composite is { Patches.Count: >= 2 } t && _stacks.GetOrAdd(info.Name, _ =>
    {
        var covered = new byte[Math.Max(1, t.Width)];
        foreach (var p in t.Patches)
        {
            if (GetPatch(p.Patch) is not { } patch) continue;
            for (var x = Math.Max(0, p.X); x < Math.Min(covered.Length, p.X + patch.Width); x++)
            {
                if (++covered[x] >= 2) return true;
            }
        }

        return false;
    });

    /// <summary>Whether a wall texture's picture has any see-through pixel (on a solid wall vanilla Doom draws what is behind it as noise).</summary>
    public bool HasHoles(string name) => _walls.TryGetValue(name, out var info) && _holes.GetOrAdd(info.Name, _ =>
    {
        if (GetWall(name) is not { } image) return false;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] < 128) return true;
        }

        return false;
    });

    private readonly ConcurrentDictionary<string, bool> _stacks = new(LumpName.Comparer);
    private readonly ConcurrentDictionary<string, bool> _holes = new(LumpName.Comparer);

    private void AddContainer(ResourceContainer c)
    {
        // TEXTURE1 / TEXTURE2 against this file's PNAMES (or the nearest earlier one).
        var pnamesEntry = c.Find("PNAMES") ?? _stack.Containers.TakeWhile(x => x != c).Select(x => x.Find("PNAMES")).LastOrDefault(e => e is not null);
        IReadOnlyList<NameField>? pnames = null;
        var first = true;
        foreach (var lumpName in new[] { "TEXTURE1", "TEXTURE2" })
        {
            var lump = c.Find(lumpName);
            if (lump is null) continue;
            try
            {
                pnames ??= pnamesEntry is null ? [] : TextureLump.ReadPatchNames(pnamesEntry.Read(), $"{pnamesEntry.Container.DisplayName} PNAMES");
                var defs = TextureLump.Read(lump.Read(), pnames, $"{c.DisplayName} {lumpName}");
                foreach (var t in defs.Textures)
                {
                    // The first texture of the first TEXTURE1 is a placeholder the engines never draw.
                    if (first && lumpName == "TEXTURE1" && t == defs.Textures[0]) t.IsNull = true;
                    _walls[t.Name] = new TextureInfo(t.Name, TextureKind.Composite, t.Width, t.Height, t.Origin, c) { Composite = t };
                }

                first = false;
            }
            catch (CorruptDataException ex)
            {
                Notes.Add(ex.Message);
            }
        }

        foreach (var e in c.Entries)
        {
            if (e.IsMarker || e.Size == 0) continue;
            if (e.Namespace == ResourceNamespace.Textures)
            {
                _walls[e.Name] = SingleInfo(e, TextureKind.Single);
            }
            else if (e.Namespace == ResourceNamespace.Flats)
            {
                _flats[e.Name] = SingleInfo(e, TextureKind.Flat);
            }
        }

        foreach (var lump in c.Entries.Where(e => !e.IsMarker && e.Namespace == ResourceNamespace.Global && LumpName.Comparer.Equals(e.Name, "TEXTURES")))
        {
            try
            {
                var parsed = TexturesLump.Parse(lump.Read(), $"{c.DisplayName} {lump.FullPath}");
                Notes.AddRange(parsed.Notes);
                foreach (var (use, t) in parsed.Definitions)
                {
                    var info = new TextureInfo(t.Name, TextureKind.Composite, t.Width, t.Height, t.Origin, c) { Composite = t };
                    if (use == TextureUse.Flat) _flats[t.Name] = info;
                    else if (use == TextureUse.Wall) _walls[t.Name] = info;
                }
            }
            catch (CorruptDataException ex)
            {
                Notes.Add(ex.Message);
            }
        }
    }

    private TextureInfo SingleInfo(ResourceEntry e, TextureKind kind)
    {
        // The size is only known once the picture is read; it is filled in on first render.
        return new TextureInfo(e.Name, kind, 0, 0, $"{e.Container.DisplayName} {e.FullPath}", e.Container) { Entry = e };
    }

    private RgbaImage? Render(TextureInfo info)
    {
        try
        {
            if (info.Entry is { } entry)
            {
                return PictureLoader.Load(entry.Read(), entry.Namespace, _palette, info.Origin).Image;
            }

            if (info.Composite is { } comp) return Compose(comp);
        }
        catch (CorruptDataException ex)
        {
            lock (Notes) Notes.Add(ex.Message);
        }

        return null;
    }

    /// <summary>Draws the patches onto a transparent canvas, clipped to it, later patches over earlier ones.</summary>
    public RgbaImage Compose(CompositeTexture t)
    {
        var canvas = new RgbaImage(Math.Max(1, t.Width), Math.Max(1, t.Height));
        foreach (var p in t.Patches)
        {
            var patch = GetPatch(p.Patch);
            if (patch is null)
            {
                lock (Notes) Notes.Add($"{t.Name} ({t.Origin}) uses the patch {(p.Patch.Length == 0 ? "(unnamed)" : p.Patch)}, which no loaded file holds.");
                continue;
            }

            Blit(patch, canvas, p);
        }

        return canvas;
    }

    private RgbaImage? GetPatch(string name) => name.Length == 0 ? null : _patchCache.GetOrAdd(name, n =>
    {
        foreach (var ns in new[] { ResourceNamespace.Patches, ResourceNamespace.Global, ResourceNamespace.Textures, ResourceNamespace.Graphics, ResourceNamespace.Sprites, ResourceNamespace.Flats })
        {
            var e = _stack.Find(n, ns);
            if (e is null || e.Size == 0) continue;
            try
            {
                return PictureLoader.Load(e.Read(), e.Namespace, _palette, $"{e.Container.DisplayName} {e.FullPath}").Image;
            }
            catch (CorruptDataException)
            {
            }
        }

        return null;
    });

    private static void Blit(RgbaImage src, RgbaImage dst, PatchPlacement p)
    {
        var rotated = p.Rotate is 90 or 270;
        var w = rotated ? src.Height : src.Width;
        var h = rotated ? src.Width : src.Height;
        for (var y = 0; y < h; y++)
        {
            var dy = p.Y + y;
            if (dy < 0 || dy >= dst.Height) continue;
            for (var x = 0; x < w; x++)
            {
                var dx = p.X + x;
                if (dx < 0 || dx >= dst.Width) continue;
                var fx = p.FlipX ? w - 1 - x : x;
                var fy = p.FlipY ? h - 1 - y : y;
                var (sx, sy) = p.Rotate switch
                {
                    90 => (fy, src.Height - 1 - fx),
                    180 => (src.Width - 1 - fx, src.Height - 1 - fy),
                    270 => (src.Width - 1 - fy, fx),
                    _ => (fx, fy),
                };
                var si = (sy * src.Width + sx) * 4;
                if (src.Pixels[si + 3] == 0) continue;
                var di = (dy * dst.Width + dx) * 4;
                Array.Copy(src.Pixels, si, dst.Pixels, di, 4);
            }
        }
    }
}
