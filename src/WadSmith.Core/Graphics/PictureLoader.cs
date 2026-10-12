using WadSmith.Core.Containers;

namespace WadSmith.Core.Graphics;

/// <summary>What a lump turned out to be, when read as a picture.</summary>
public enum PictureFormat
{
    Png,
    OtherImage,
    DoomPicture,
    Flat,
    Fullscreen,
}

/// <summary>Reads any lump that holds a picture, deciding the format by content the way the engine does.</summary>
public static class PictureLoader
{
    /// <summary>
    /// The picture in a lump. Image files are tried first by their signature; then, in a flats
    /// group, raw floor data; elsewhere a Doom picture, falling back to a 64000-byte raw screen.
    /// </summary>
    public static (RgbaImage Image, PictureFormat Format, PalettedImage? Paletted) Load(byte[] data, ResourceNamespace ns, Palette palette, string name = "picture")
    {
        if (ImageCodec.IsPng(data)) return (ImageCodec.Decode(data, name), PictureFormat.Png, null);
        if (ImageCodec.IsKnownImage(data)) return (ImageCodec.Decode(data, name), PictureFormat.OtherImage, null);

        if (ns == ResourceNamespace.Flats)
        {
            var flat = RawPicture.DecodeFlat(data);
            return (flat.ToRgba(palette), PictureFormat.Flat, flat);
        }

        if (DoomPicture.IsValid(data))
        {
            var pic = DoomPicture.Decode(data, name);
            return (pic.ToRgba(palette), PictureFormat.DoomPicture, pic);
        }

        if (RawPicture.FullscreenWidth(data.Length) > 0)
        {
            var raw = RawPicture.DecodeFullscreen(data);
            return (raw.ToRgba(palette), PictureFormat.Fullscreen, raw);
        }

        if (data.Length >= 4096 && data.Length < 64000)
        {
            var flat = RawPicture.DecodeFlat(data);
            return (flat.ToRgba(palette), PictureFormat.Flat, flat);
        }

        throw new CorruptDataException($"{name} is not a picture in any format WadSmith knows.");
    }

    /// <summary>The game palette from a resource stack (the last PLAYPAL), or a grey ramp when there is none.</summary>
    public static Palette PaletteOf(ResourceStack stack)
    {
        var playpal = stack.Find("PLAYPAL");
        return playpal is not null && playpal.Size >= 768 ? new Palette(playpal.Read()) : Palette.Greyscale();
    }
}
