using SkiaSharp;
using WadSmith.Editor;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>Renders a scene once to a PNG, on the calling thread with its own context (tests, captures, golden pictures).</summary>
internal static unsafe class Offscreen3D
{
    public static byte[] RenderPng(MapScene scene, GameResources? resources, Camera3D camera, int width, int height, bool inside = false)
    {
        using var context = EglContext.Create();
        context.MakeCurrent();
        var gl = new Gl(EglContext.ProcAddress);
        using var renderer = new SceneRenderer(gl);
        renderer.Upload(scene, resources);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        renderer.Render(camera, width, height, (byte*)bitmap.GetPixels(), inside);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A camera that looks at the whole scene from above and to the south-west, like a model on a table.</summary>
    public static Camera3D Overview(MapScene scene, double aspect = 1.6)
    {
        var (x0, y0, z0, x1, y1, z1) = scene.Bounds;
        var size = Math.Max(x1 - x0, y1 - y0);
        return Camera3D.Orbit((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2, 50, -48, Camera3D.FitDistance(size, aspect));
    }
}
