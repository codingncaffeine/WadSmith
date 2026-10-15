using CommunityToolkit.Mvvm.Input;

namespace WadSmith.App.ViewModels;

/// <summary>
/// One step of this session's undo list in the Versions panel (D65, Ultimate Doom Builder's undo list): what it did, when,
/// and whether it is the map now, done before it, or taken back. Picking it puts the map as it was right after that step.
/// </summary>
internal sealed partial class StepItem(string label, string when, StepState state, Action go)
{
    public string Label { get; } = label;

    public string When { get; } = when;

    public StepState State { get; } = state;

    /// <summary>The map is as this step left it.</summary>
    public bool IsNow => State == StepState.Now;

    /// <summary>Taken back with Undo: Redo (or a click) brings it back.</summary>
    public bool IsUndone => State == StepState.Undone;

    public string Tip => State switch
    {
        StepState.Now => "The map is as this step left it.",
        StepState.Undone => "Taken back. Click to bring the map back to just after this step (as Redo does, step by step).",
        _ => "Click to put the map as it was just after this step (as Undo does, step by step); the steps after it can be brought back.",
    };

    [RelayCommand]
    private void Go() => go();
}

/// <summary>Where a step stands against the map as it is now.</summary>
internal enum StepState
{
    Done,
    Now,
    Undone,
}
