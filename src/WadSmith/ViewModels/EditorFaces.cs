namespace WadSmith.App.ViewModels;

/// <summary>A control's label and tooltip, in the names and keys Preferences chose.</summary>
internal sealed record Face(string Label, string Tip);

/// <summary>The faces of the editor's controls whose names or keys follow Preferences (D3, D14).</summary>
internal sealed record EditorFaces(Face Select, Face Room, Face Door, Face Window, Face Teleport, Face Light, Face Stairs, Face Measure, Face Objects, Face Play, Face Player, Face ThreeD);

/// <summary>Which of the editor's controls the guided first map outlines now (one at most).</summary>
internal sealed record GuideMarks(bool Room, bool ThreeD, bool Door, bool Textures, bool Objects, bool Inspector, bool Play)
{
    public static GuideMarks For(Editor.App.GuideTarget target) => new(
        target == Editor.App.GuideTarget.RoomTool,
        target == Editor.App.GuideTarget.ThreeD,
        target == Editor.App.GuideTarget.DoorTool,
        target == Editor.App.GuideTarget.Textures,
        target == Editor.App.GuideTarget.Objects,
        target == Editor.App.GuideTarget.Inspector,
        target == Editor.App.GuideTarget.Play);
}
