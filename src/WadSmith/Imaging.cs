using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WadSmith.Core.Graphics;

namespace WadSmith.App;

internal static class Imaging
{
    /// <summary>An RGBA picture as an Avalonia bitmap (premultiplied BGRA, as Avalonia stores it).</summary>
    public static Bitmap ToBitmap(RgbaImage img)
    {
        var bitmap = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        unsafe
        {
            var dst = (byte*)fb.Address;
            for (var y = 0; y < img.Height; y++)
            {
                var row = dst + y * fb.RowBytes;
                for (var x = 0; x < img.Width; x++)
                {
                    var i = (y * img.Width + x) * 4;
                    var a = img.Pixels[i + 3];
                    row[x * 4] = (byte)(img.Pixels[i + 2] * a / 255);
                    row[x * 4 + 1] = (byte)(img.Pixels[i + 1] * a / 255);
                    row[x * 4 + 2] = (byte)(img.Pixels[i] * a / 255);
                    row[x * 4 + 3] = a;
                }
            }
        }

        return bitmap;
    }
}
