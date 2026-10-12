namespace WadSmith.App.ViewModels;

/// <summary>A control's label and tooltip, in the names and keys Preferences chose.</summary>
internal sealed record Face(string Label, string Tip);

/// <summary>The faces of the editor's controls whose names or keys follow Preferences (D3, D14).</summary>
internal sealed record EditorFaces(Face Select, Face Room, Face Door, Face Window, Face Teleport, Face Light, Face Stairs, Face Measure, Face Objects, Face Play, Face Player, Face ThreeD);
