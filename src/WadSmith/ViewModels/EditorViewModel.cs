using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>
/// A map open for editing: the canvas, the Inspector, undo and redo, the save state in plain words,
/// and the status strip, which describes what is under the pointer or, when nothing is, gives the
/// coach's next tip (D33). Tips stop once the user has done what they teach.
/// </summary>
internal sealed partial class EditorViewModel : ObservableObject
{
    private static readonly (string Id, string Text)[] Tips =
    [
        ("zoom", "Tip: scroll to zoom in and out; drag with the right mouse button to move around. Home shows the whole map."),
        ("select", "Tip: click a room, a wall or a thing to see what it is in the Inspector on the right."),
        ("edit", "Tip: change a number in the Inspector, such as a floor height, and watch the map follow."),
        ("undo", "Tip: Ctrl+Z takes back the last change and Ctrl+Y brings it back. Nothing needs saving; WadSmith keeps every change."),
        ("heights", "Tip: Heights as colours shows the high and low parts of the map at a glance."),
    ];

    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private string _hoverText = "";

    public EditorViewModel(AppServices services, EditorSession session, MainViewModel main)
    {
        _services = services;
        _main = main;
        Session = session;
        Inspector = new InspectorViewModel(session);
        session.History.Moved += OnHistoryMoved;
        session.StateChanged += _ => UpdateSaveText();
        session.Selection.Changed += () => Learned("select");
        UpdateSaveText();
        UpdateStatus();
    }

    public EditorSession Session { get; }

    public InspectorViewModel Inspector { get; }

    public string ProjectName => Session.Project.Info.Name;

    public string MapName => Session.Slot;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _saveText = "";

    [ObservableProperty]
    private bool _showHeights;

    [ObservableProperty]
    private string _undoTip = "Nothing to undo";

    [ObservableProperty]
    private string _redoTip = "Nothing to redo";

    partial void OnShowHeightsChanged(bool value)
    {
        if (value) Learned("heights");
    }

    private void OnHistoryMoved()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        UndoTip = Session.History.UndoLabel is { } u ? $"Undo: {u} (Ctrl+Z)" : "Nothing to undo";
        RedoTip = Session.History.RedoLabel is { } r ? $"Redo: {r} (Ctrl+Y)" : "Nothing to redo";
        Learned("edit");
    }

    private void UpdateSaveText()
    {
        SaveText = Session.State switch
        {
            SaveState.Saved => "All changes kept",
            SaveState.Pending or SaveState.Saving => "Keeping your changes…",
            _ => Session.LastError ?? "Your changes could not be kept",
        };
    }

    private bool CanUndo() => Session.History.CanUndo;

    private bool CanRedo() => Session.History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        Session.History.Undo();
        Session.Selection.Prune();
        Learned("undo");
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        Session.History.Redo();
        Session.Selection.Prune();
    }

    [RelayCommand]
    private void KeepVersion()
    {
        Session.SaveVersion("Kept by hand");
        _hoverText = "A copy of the map as it is now was kept in the project's versions.";
        UpdateStatus();
    }

    [RelayCommand]
    private async Task CloseAsync() => await _main.CloseEditorAsync().ConfigureAwait(true);

    /// <summary>Called by the canvas when the pointer moves over something new.</summary>
    public void Hover(Element? element, MapPoint at)
    {
        _hoverText = element switch
        {
            Sector s => string.Create(CultureInfo.InvariantCulture, $"Area · floor {s.FloorHeight:0}, ceiling {s.CeilingHeight:0}, light {s.Light} · {s.FloorTexture}"),
            Line l => string.Create(CultureInfo.InvariantCulture, $"Wall · {(l.Back is null ? "solid" : "between two areas")} · {l.Length:0} long{(l.Special != 0 ? " · has an action" : "")}"),
            Thing t => string.Create(CultureInfo.InvariantCulture, $"Thing · type {t.Type} · facing {t.Angle}°"),
            Vertex v => string.Create(CultureInfo.InvariantCulture, $"Corner at {v.X:0}, {v.Y:0}"),
            _ => "",
        };
        if (element is not null) Learned("zoom");
        UpdateStatus();
    }

    /// <summary>Called by the canvas when the view zooms or pans.</summary>
    public void Navigated() => Learned("zoom");

    private void Learned(string id)
    {
        var seen = _services.Settings.Current.CoachSeen;
        if (!seen.Contains(id))
        {
            seen.Add(id);
            _services.Settings.Save();
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_hoverText.Length > 0)
        {
            Status = _hoverText;
            return;
        }

        var settings = _services.Settings.Current;
        Status = settings.CoachTips ? Tips.FirstOrDefault(t => !settings.CoachSeen.Contains(t.Id)).Text ?? "" : "";
    }
}
