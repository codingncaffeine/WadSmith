using System.Globalization;
using SkiaSharp;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>The letters a sign is written in: the game's own (its status-bar font), or a font of the computer's.</summary>
public enum SignLettering
{
    Game,
    Plain,
    Bold,
    Serif,
    Mono,
}

/// <summary>What a sign is written on: the wall's own picture (painted on it), or a plate of its own.</summary>
public enum SignBack
{
    Wall,
    Dark,
    Light,
}

/// <summary>A sign's words and look; the colour is 0xRRGGBB.</summary>
public sealed record SignSettings(string Text, SignLettering Lettering, int Colour, SignBack Back)
{
    public static SignSettings New => new("Your words here", SignLettering.Game, 0xF0D040, SignBack.Wall);
}

/// <summary>A sign written as a texture: its name, the wall size it was made for in map units, and the wall's picture under it.</summary>
public sealed record SignPicture(string Texture, SignSettings Settings, int Width, int Height, string Wall);

/// <summary>
/// Signs on walls (Phase 7, §3.6 "typed text"): words drawn into a picture the shape of the wall, laid out as large as fit
/// on up to six lines, in the game's own lettering or a font of the computer's, painted on the wall's picture or on a plate.
/// The picture is a texture of the user's own (it goes on as a poster); every change makes a new texture, so undo and every
/// view simply show the one named.
/// </summary>
public static class SignPictures
{
    /// <summary>Named colours for the Inspector.</summary>
    public static readonly IReadOnlyList<(string Name, int Colour)> Colours =
    [
        ("Yellow", 0xF0D040), ("White", 0xF0F0F0), ("Red", 0xE03020), ("Green", 0x40D040), ("Blue", 0x5080F0), ("Orange", 0xF08020), ("Black", 0x101010),
    ];

    /// <summary>The picture's longest side, in pixels: a small wall is drawn larger than its size so a computer font stays sharp.</summary>
    public const int Pixels = 512;

    /// <summary>Writes the sign for a wall <paramref name="width"/> × <paramref name="height"/> map units into the project as a new texture. Call it on a worker.</summary>
    public static SignPicture Write(Project project, GameResources into, SignSettings settings, int width, int height, string wall)
    {
        var image = Render(settings, Math.Max(16, width), Math.Max(16, height), settings.Back == SignBack.Wall ? into.Textures.GetWall(wall) : null, into);
        var name = FreeName(into);
        var file = $"textures/{name}.png";
        project.WriteFiles([(file, ImageCodec.EncodePng(image))]);
        into.Textures.Add(ResourceContainer.Open(project.Folder).FindPath(file) ?? throw new CorruptDataException($"{file} was written but could not be read back."));
        var c = CultureInfo.InvariantCulture;
        project.Record(
        [
            new CopiedAsset { Kind = "wall", Game = "picture", Original = "sign", Name = name, File = file },
            new CopiedAsset { Kind = "sign", Game = "picture", Original = settings.Text, Name = string.Join('|', name, settings.Lettering, settings.Colour.ToString("X6", c), settings.Back, width.ToString(c), height.ToString(c), wall), File = file },
        ]);
        return new SignPicture(name, settings, width, height, wall);
    }

    /// <summary>The sign a texture is, or null.</summary>
    public static SignPicture? Of(IReadOnlyList<CopiedAsset> copied, string texture)
    {
        foreach (var c in copied.Where(c => c.Kind == "sign"))
        {
            var p = c.Name.Split('|');
            if (p.Length < 6 || !string.Equals(p[0], texture, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Enum.TryParse<SignLettering>(p[1], out var lettering) || !Enum.TryParse<SignBack>(p[3], out var back)) continue;
            var ci = CultureInfo.InvariantCulture;
            return new SignPicture(p[0], new SignSettings(c.Original, lettering, int.Parse(p[2], NumberStyles.HexNumber, ci), back), int.Parse(p[4], ci), int.Parse(p[5], ci), p.Length > 6 ? p[6] : "");
        }

        return null;
    }

    /// <summary>The sign's picture: <paramref name="width"/> × <paramref name="height"/> map units drawn at up to <see cref="Pixels"/> on the longer side.</summary>
    public static RgbaImage Render(SignSettings s, int width, int height, RgbaImage? wall, GameResources? game)
    {
        var k = Math.Min(4.0, Pixels / (double)Math.Max(width, height));
        var (w, h) = (Math.Max(8, (int)Math.Round(width * k)), Math.Max(8, (int)Math.Round(height * k)));
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        var colour = new SKColor((byte)(s.Colour >> 16), (byte)(s.Colour >> 8), (byte)s.Colour);

        // The background: the wall's own picture at the wall's scale, or a plate with a frame.
        if (wall is not null)
        {
            using var tile = ToSkia(wall);
            using var paint = new SKPaint { Shader = tile.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateScale((float)k, (float)k)) };
            canvas.DrawRect(0, 0, w, h, paint);
        }
        else
        {
            canvas.Clear(new SKColor(0x40, 0x40, 0x40));
        }

        var margin = (float)Math.Max(2, Math.Min(w, h) * 0.06);
        if (s.Back != SignBack.Wall)
        {
            using var plate = new SKPaint { Color = s.Back == SignBack.Dark ? new SKColor(0x18, 0x18, 0x1C) : new SKColor(0xE8, 0xE4, 0xD8), IsAntialias = true };
            using var frame = new SKPaint { Color = s.Back == SignBack.Dark ? new SKColor(0x70, 0x70, 0x78) : new SKColor(0x50, 0x48, 0x40), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, margin / 3), IsAntialias = true };
            var r = new SKRect(margin / 2, margin / 2, w - margin / 2, h - margin / 2);
            canvas.DrawRoundRect(r, margin / 2, margin / 2, plate);
            canvas.DrawRoundRect(r, margin / 2, margin / 2, frame);
        }

        var area = new SKRect(margin * 1.5f, margin * 1.5f, w - margin * 1.5f, h - margin * 1.5f);
        using var glyphs = s.Lettering == SignLettering.Game && game is not null ? GameFont.For(game) : null;
        if (glyphs is not null) DrawGameLetters(canvas, s.Text.ToUpperInvariant(), glyphs, colour, area, s.Back == SignBack.Wall);
        else DrawFontLetters(canvas, s.Text, s.Lettering, colour, area, s.Back == SignBack.Wall);
        canvas.Flush();

        var image = new RgbaImage(w, h);
        var pixels = bitmap.Bytes;
        for (var i = 0; i < w * h; i++) image.Set(i % w, i / w, pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], 255);
        return image;
    }

    /// <summary>The words broken into the number of lines (1 to 6) that lets them be drawn largest in the area, with the scale.</summary>
    public static (IReadOnlyList<string> Lines, float Scale) Layout(string text, Func<string, float> widthOf, float lineHeight, SKRect area)
    {
        var words = text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return ([], 1);
        (IReadOnlyList<string> Lines, float Scale) best = ([], 0);
        for (var n = 1; n <= Math.Min(6, words.Length); n++)
        {
            var target = widthOf(string.Join(' ', words)) / n;
            var lines = new List<string>();
            var line = "";
            foreach (var word in words)
            {
                var tried = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && widthOf(tried) > target * 1.1f && lines.Count < n - 1)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = tried;
            }

            lines.Add(line);
            var scale = Math.Min(area.Width / lines.Max(widthOf), area.Height / (lines.Count * lineHeight * 1.2f));
            if (scale > best.Scale) best = (lines, scale);
        }

        return best;
    }

    private static void DrawFontLetters(SKCanvas canvas, string text, SignLettering lettering, SKColor colour, SKRect area, bool shadow)
    {
        var (family, weight) = lettering switch
        {
            SignLettering.Serif => ("serif", SKFontStyleWeight.Normal),
            SignLettering.Mono => ("monospace", SKFontStyleWeight.Normal),
            SignLettering.Bold or SignLettering.Game => ("sans-serif", SKFontStyleWeight.Bold),
            _ => ("sans-serif", SKFontStyleWeight.Normal),
        };
        using var typeface = SKTypeface.FromFamilyName(family, new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 100) { Edging = SKFontEdging.Antialias };
        var (lines, scale) = Layout(text, t => font.MeasureText(t), font.Spacing, area);
        if (lines.Count == 0) return;
        font.Size = 100 * scale;
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 0xA0), IsAntialias = true };
        var step = font.Spacing * 1.2f;
        var y = area.MidY - step * lines.Count / 2 - font.Metrics.Ascent + (step - font.Spacing) / 2;
        foreach (var line in lines)
        {
            var x = area.MidX - font.MeasureText(line) / 2;
            if (shadow) canvas.DrawText(line, x + font.Size / 16, y + font.Size / 16, font, dark);
            canvas.DrawText(line, x, y, font, paint);
            y += step;
        }
    }

    private static void DrawGameLetters(SKCanvas canvas, string text, GameFont glyphs, SKColor colour, SKRect area, bool shadow)
    {
        float Width(string t) => t.Sum(ch => glyphs.Advance(ch));
        var (lines, scale) = Layout(text, Width, glyphs.Height, area);
        if (lines.Count == 0) return;
        scale = MathF.Max(1, MathF.Floor(scale * 4) / 4);
        var step = glyphs.Height * 1.2f * scale;
        var y = area.MidY - step * lines.Count / 2;
        using var tint = SKColorFilter.CreateBlendMode(colour, SKBlendMode.Modulate);
        using var paint = new SKPaint { ColorFilter = tint };
        using var dark = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, 0xA0), SKBlendMode.SrcIn) };
        foreach (var line in lines)
        {
            var x = area.MidX - Width(line) * scale / 2;
            foreach (var ch in line)
            {
                if (glyphs.Picture(ch) is { } glyph)
                {
                    var at = new SKRect(x, y, x + glyph.Width * scale, y + glyph.Height * scale);
                    if (shadow) canvas.DrawImage(glyph, new SKRect(at.Left + scale, at.Top + scale, at.Right + scale, at.Bottom + scale), new SKSamplingOptions(SKFilterMode.Nearest), dark);
                    canvas.DrawImage(glyph, at, new SKSamplingOptions(SKFilterMode.Nearest), paint);
                }

                x += glyphs.Advance(ch) * scale;
            }

            y += step;
        }
    }

    private static SKImage ToSkia(RgbaImage image) =>
        SKImage.FromPixelCopy(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul), image.Pixels);

    private static string FreeName(GameResources into)
    {
        for (var n = 1; n < 10000; n++)
        {
            var name = "SIGN" + n.ToString("0000", CultureInfo.InvariantCulture);
            if (!into.Textures.Has(name)) return name;
        }

        throw new CorruptDataException("Every sign name is taken in this project.");
    }

    /// <summary>
    /// The game's own lettering: Doom's, Strife's and Chex Quest's status-bar font (STCFN033 to STCFN095: "!" to "_"),
    /// Heretic's and Hexen's (FONTA01 to FONTA59: "!" to "["); capitals only, made white so a colour can tint them.
    /// </summary>
    private sealed class GameFont : IDisposable
    {
        private readonly Dictionary<char, SKImage> _glyphs;
        private readonly int _space;

        private GameFont(Dictionary<char, SKImage> glyphs, int space)
        {
            _glyphs = glyphs;
            _space = space;
            Height = glyphs.Values.Max(g => g.Height);
        }

        public int Height { get; }

        public SKImage? Picture(char ch) => _glyphs.GetValueOrDefault(ch);

        public float Advance(char ch) => _glyphs.TryGetValue(ch, out var g) ? g.Width + 1 : _space;

        public static GameFont? For(GameResources game)
        {
            var raven = game.Game.Engine is "heretic" or "hexen";
            var glyphs = new Dictionary<char, SKImage>();
            for (var code = 33; code <= (raven ? 91 : 95); code++)
            {
                var name = raven ? $"FONTA{code - 32:00}" : $"STCFN{code:000}";
                if ((game.Find(name, ResourceNamespace.Graphics) ?? game.Find(name, ResourceNamespace.Global)) is not { } entry) continue;
                try
                {
                    var picture = PictureLoader.Load(entry.Read(), ResourceNamespace.Graphics, game.Textures.Palette, name).Image;
                    glyphs[(char)code] = Whitened(picture);
                }
                catch (CorruptDataException)
                {
                }
            }

            return glyphs.Count < 26 ? null : new GameFont(glyphs, raven ? 5 : 4);
        }

        /// <summary>The glyph in greys from its brightest channel, so a tint gives any colour with the game's shading.</summary>
        private static SKImage Whitened(RgbaImage glyph)
        {
            var copy = new RgbaImage(glyph.Width, glyph.Height);
            for (var y = 0; y < glyph.Height; y++)
            {
                for (var x = 0; x < glyph.Width; x++)
                {
                    var c = glyph.Get(x, y);
                    var v = (byte)Math.Min(255, Math.Max(c.R, Math.Max(c.G, c.B)) * 1.4);
                    copy.Set(x, y, v, v, v, c.A);
                }
            }

            return ToSkia(copy);
        }

        public void Dispose()
        {
            foreach (var g in _glyphs.Values) g.Dispose();
        }
    }
}
