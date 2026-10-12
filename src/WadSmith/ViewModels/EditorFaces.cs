using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>A control's label and tooltip, in the names and keys Preferences chose.</summary>
internal sealed record Face(string Label, string Tip);

/// <summary>The faces of the editor's controls whose names or keys follow Preferences (D3, D14).</summary>
internal sealed record EditorFaces(Face Select, Face Room, Face Door, Face Window, Face Teleport, Face Light, Face Stairs, Face Measure, Face Objects, Face Play, Face Player, Face ThreeD);

/// <summary>Which of the editor's controls the Guide outlines now (one at most).</summary>
internal sealed record GuideMarks(GuideTarget Target)
{
    public bool Room => Target == GuideTarget.RoomTool;
    public bool ThreeD => Target == GuideTarget.ThreeD;
    public bool Door => Target == GuideTarget.DoorTool;
    public bool Textures => Target == GuideTarget.Textures;
    public bool Objects => Target == GuideTarget.Objects;
    public bool Inspector => Target == GuideTarget.Inspector;
    public bool Play => Target == GuideTarget.Play;
    public bool Stairs => Target == GuideTarget.StairsTool;
    public bool Light => Target == GuideTarget.LightTool;
    public bool Heights => Target == GuideTarget.Heights;
    public bool Walk => Target == GuideTarget.Walk;
    public bool Preferences => Target == GuideTarget.Preferences;

    public static GuideMarks For(GuideTarget target) => new(target);
}
