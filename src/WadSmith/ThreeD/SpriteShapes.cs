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

/// <summary>Object types' 3D models for the scene (Phase 11): the project's props, each read from its files once.</summary>
internal static class ModelShapes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Folder, int Type), PropShape?> Read = new();

    /// <summary>The model a type of <paramref name="session"/>'s project shows; null for every type that is not a prop. Reads files the first time: call it on a worker.</summary>
    public static Func<int, PropShape?> For(EditorSession session)
    {
        var folder = session.Project.Folder;
        var copied = session.Project.CopiedNow();
        var props = copied.Where(c => c.Kind == "actor" && c.Game == "model").Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return type => props.Contains(type.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ? Read.GetOrAdd((folder, type), key => WadSmith.Editor.Editing.ModelProps.Shape(key.Folder, copied, key.Type))
            : null;
    }
}
