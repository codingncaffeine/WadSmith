using WadSmith.Editor;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>Object types' sprite shapes for the 3D scene, from the game's pictures and the engine's scales.</summary>
internal static class SpriteShapes
{
    public static Func<int, SpriteShape?> For(GameResources? resources) => type =>
    {
        if (resources?.SpriteFor(type) is not { } image) return null;
        var info = resources.Things.Find(type);
        return new SpriteShape(image.Width, image.Height, image.OffsetX, image.OffsetY, info?.ScaleX ?? 1, info?.ScaleY ?? 1);
    };
}
