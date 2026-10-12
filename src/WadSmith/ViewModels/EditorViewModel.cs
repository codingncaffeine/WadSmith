using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.App;
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
        ("heights", "Tip: the view buttons at the top show floors, ceilings, heights as colours, or just the walls."),
    ];

    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private string _hoverText = "";
    private bool _goingBack;

    public EditorViewModel(AppServices services, EditorSession session, MainViewModel main, string? message = null, bool showVersions = false)
    {
        _services = services;
        _main = main;
        Session = session;
        _hoverText = message ?? "";
        _showVersions = showVersions;
        if (showVersions) _ = RefreshVersionsAsync();
        Inspector = new InspectorViewModel(session);
        session.History.Moved += OnHistoryMoved;
        session.StateChanged += _ => UpdateSaveText();
        session.Selection.Changed += () => Learned("select");
        session.GeometryChanged += UpdateHeights;
        UpdateHeights();
        UpdateSaveText();
        UpdateStatus();
    }

    public EditorSession Session { get; }

    public InspectorViewModel Inspector { get; }

    /// <summary>The kept copies of the map, newest first, while the Versions panel is open.</summary>
    public ObservableCollection<VersionItem> Versions { get; } = [];

    /// <summary>Whether the right-hand panel shows the versions instead of the Inspector.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private bool _showVersions;

    public bool ShowInspector
    {
        get => !ShowVersions;
        set
        {
            if (value) ShowVersions = false;
            else OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string _versionsNote = "";

    partial void OnShowVersionsChanged(bool value)
    {
        if (value) _ = RefreshVersionsAsync();
    }

    private async Task RefreshVersionsAsync()
    {
        var project = Session.Project;
        var slot = Session.Slot;
        await Session.OpeningVersion.ConfigureAwait(true);
        var items = await Task.Run(() => MapVersions.List(project, slot).Select(v => new VersionItem(v, Picture(v.Picture), GoBackAsync)).ToList()).ConfigureAwait(true);
        Versions.Clear();
        foreach (var item in items) Versions.Add(item);
        VersionsNote = items.Count == 0
            ? "No versions yet. WadSmith keeps one each time you open the map and whenever you press Keep a version."
            : "WadSmith keeps one each time you open the map and whenever you press Keep a version. Going back keeps the map as it is now first, so nothing is lost.";
    }

    private static Bitmap? Picture(string? path)
    {
        if (path is null) return null;
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Makes a kept copy the map again: the map as it is now is kept first, then the copy opens in its place.</summary>
    private async Task GoBackAsync(VersionItem item)
    {
        if (_goingBack) return;
        _goingBack = true;
        var project = Session.Project;
        var slot = Session.Slot;
        var hooks = Session.Hooks;
        _hoverText = $"Going back to the version from {item.WhenText}…";
        UpdateStatus();
        try
        {
            await Session.FlushAsync().ConfigureAwait(true);
            await Session.SaveVersion($"Before going back to the version from {EditorSession.Describe(item.Version.When)}").ConfigureAwait(true);
            Session.Dispose();
            var restored = await Task.Run(() => EditorSession.Restore(project, slot, item.Version, hooks)).ConfigureAwait(true);
            _main.OpenEditor(restored, $"Went back to the version from {item.WhenText}. The map as it was is kept as a version too.", showVersions: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
        {
            Log.Warn($"Going back to {item.Version.File} failed: {ex.Message}");
            await _main.OpenEditorAsync(project, slot).ConfigureAwait(true);
        }
        finally
        {
            _goingBack = false;
        }
    }

    public string ProjectName => Session.Project.Info.Name;

    public string MapName => Session.Slot;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _saveText = "";

    /// <summary>How the map is painted (D45); floors by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeights), nameof(IsFloors), nameof(IsCeilings), nameof(IsLines))]
    private Canvas.MapViewMode _viewMode;

    public bool ShowHeights
    {
        get => ViewMode == Canvas.MapViewMode.Heights;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Heights;
            else OnPropertyChanged();
        }
    }

    public bool IsFloors
    {
        get => ViewMode == Canvas.MapViewMode.Floors;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Floors;
            else OnPropertyChanged();
        }
    }

    public bool IsCeilings
    {
        get => ViewMode == Canvas.MapViewMode.Ceilings;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Ceilings;
            else OnPropertyChanged();
        }
    }

    public bool IsLines
    {
        get => ViewMode == Canvas.MapViewMode.Lines;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Lines;
            else OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string _undoTip = "Nothing to undo";

    [ObservableProperty]
    private string _heightsLow = "";

    [ObservableProperty]
    private string _heightsHigh = "";

    [ObservableProperty]
    private string _heightsNote = "";

    [ObservableProperty]
    private string _redoTip = "Nothing to redo";

    partial void OnViewModeChanged(Canvas.MapViewMode value)
    {
        if (value != Canvas.MapViewMode.Floors) Learned("heights");
    }

    private void OnHistoryMoved()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        UndoTip = Session.History.UndoLabel is { } u ? $"Undo: {u} (Ctrl+Z)" : "Nothing to undo";
        RedoTip = Session.History.RedoLabel is { } r ? $"Redo: {r} (Ctrl+Y)" : "Nothing to redo";
        Learned("edit");
    }

    /// <summary>The heights legend's words, from the shapes the worker built (no scan of the map here).</summary>
    private void UpdateHeights()
    {
        const double PlayerHeight = 56;
        if (Session.Geometry.FloorRange is not { } r)
        {
            (HeightsLow, HeightsHigh, HeightsNote) = ("", "", "No areas yet.");
            return;
        }

        HeightsLow = string.Create(CultureInfo.InvariantCulture, $"Lowest {r.Min:0}");
        HeightsHigh = string.Create(CultureInfo.InvariantCulture, $"Highest {r.Max:0}");
        var span = r.Max - r.Min;
        HeightsNote = span == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Every floor is at {r.Min:0}.")
            : string.Create(CultureInfo.InvariantCulture, $"{span:0} units apart, about {span / PlayerHeight:0.#} times the player's height.");
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
    private async Task KeepVersionAsync()
    {
        var kept = await Session.SaveVersion("Kept by hand").ConfigureAwait(true);
        _hoverText = kept is null ? "The newest version already holds the map as it is now." : "A copy of the map as it is now was kept in the project's versions.";
        UpdateStatus();
        if (ShowVersions) await RefreshVersionsAsync().ConfigureAwait(true);
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
            Thing t => string.Create(CultureInfo.InvariantCulture, $"{ThingName(t.Type)} · facing {t.Angle}°"),
            Vertex v => string.Create(CultureInfo.InvariantCulture, $"Corner at {v.X:0}, {v.Y:0}"),
            _ => "",
        };
        if (element is not null) Learned("zoom");
        UpdateStatus();
    }

    /// <summary>The object's name from the game's own definitions, or its number when the game does not define it.</summary>
    public string ThingName(int type) => Session.Resources?.Things.Find(type)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"Object type {type}");

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
