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

/// <summary>A map opened to learn from: a game's own map at a landmark (D28), or the feature tour (D32, no landmark); its banner, its map, and the editor to go back to.</summary>
internal sealed record PeekInfo(string Heading, string Says, Editor.Smart.Landmark? Landmark, Core.Games.FoundGame Game, EditorViewModel? Back, string Slot)
{
    /// <summary>The banner's first line: a game's own map, or WadSmith's map of examples.</summary>
    public string Kicker => Landmark is null ? "A MAP OF EXAMPLES · READ ONLY" : "LEARNING FROM A REAL MAP · READ ONLY";
}

/// <summary>A line of "Explain this map" with its Show button's command (none for a plain fact).</summary>
internal sealed record ReportItem(string Text, System.Windows.Input.ICommand? Show);

/// <summary>A section of "Explain this map".</summary>
internal sealed record ReportSectionView(string Heading, IReadOnlyList<ReportItem> Items);

/// <summary>"Explain this map" as the card shows it.</summary>
internal sealed record ReportView(string Summary, IReadOnlyList<ReportSectionView> Sections);
