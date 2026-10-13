using SkiaSharp;

namespace WadSmith.Core.Graphics;

/// <summary>One picture of an animation and how long it shows, in milliseconds.</summary>
public sealed record AnimationFrame(RgbaImage Image, int Milliseconds);

/// <summary>
/// Animated pictures (D15): GIF, animated WebP and APNG frames through Skia, each frame drawn over the one it builds on as
/// the format says, with its delay. A delay of 10 ms or less shows for 100 ms, as browsers show it (GIFs made for the web
/// count on that).
/// </summary>
public static class AnimatedPicture
{
    /// <summary>The picture's frames; a still picture is one frame.</summary>
    public static IReadOnlyList<AnimationFrame> Read(byte[] data, string name = "picture")
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new CorruptDataException($"{name} is not a picture WadSmith can read.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var count = Math.Max(1, codec.FrameCount);
        var frames = new List<AnimationFrame>(count);
        // Frames built on an earlier one need that one's finished picture; the last few are kept for it.
        var kept = new Dictionary<int, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var pixels = new byte[info.BytesSize];
            var frameInfo = count > 1 ? codec.FrameInfo[i] : default;
            var required = count > 1 ? frameInfo.RequiredFrame : -1;
            if (required >= 0)
            {
                if (!kept.TryGetValue(required, out var prior)) throw new CorruptDataException($"{name}: frame {i + 1} builds on frame {required + 1}, which could not be kept.");
                prior.CopyTo(pixels, 0);
            }

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    var result = codec.GetPixels(info, (nint)p, new SKCodecOptions(i, required));
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new CorruptDataException($"{name}: frame {i + 1} could not be decoded ({result}).");
                }
            }

            kept[i] = pixels;
            foreach (var old in kept.Keys.Where(k => k < i - 4).ToList()) kept.Remove(old);
            var image = new RgbaImage(info.Width, info.Height);
            pixels.CopyTo(image.Pixels, 0);
            var ms = count > 1 ? frameInfo.Duration : 0;
            frames.Add(new AnimationFrame(image, ms <= 10 ? 100 : ms));
        }

        return frames;
    }

    /// <summary>Game tics (35 a second) for a frame's milliseconds, at least one.</summary>
    public static int Tics(int milliseconds) => Math.Max(1, (int)Math.Round(milliseconds * 35 / 1000.0));

    /// <summary>
    /// At most <paramref name="most"/> frames: every n-th kept, each showing as long as the ones it stands for together, so
    /// the animation runs as long as before.
    /// </summary>
    public static IReadOnlyList<AnimationFrame> Thin(IReadOnlyList<AnimationFrame> frames, int most)
    {
        if (frames.Count <= most) return frames;
        var step = (int)Math.Ceiling(frames.Count / (double)most);
        var thinned = new List<AnimationFrame>();
        for (var i = 0; i < frames.Count; i += step) thinned.Add(frames[i] with { Milliseconds = frames.Skip(i).Take(step).Sum(f => f.Milliseconds) });
        return thinned;
    }
}
