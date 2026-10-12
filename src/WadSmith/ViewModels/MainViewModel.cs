using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>The window's content: the welcome screen, or a map being edited.</summary>
internal sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Services = services;
        Welcome = new WelcomeViewModel(services, this);
        _page = Welcome;

        // "wadsmith PROJECT_FOLDER" opens that project once the window is up.
        if (services.Options.OpenPath is { } open) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = Welcome.OpenFolderAsync(open));
    }

    public AppServices Services { get; }

    public WelcomeViewModel Welcome { get; }

    [ObservableProperty]
    private object _page;

    [ObservableProperty]
    private string _title = "WadSmith";

    /// <summary>Preferences while they are open, shown over the page; null when closed.</summary>
    [ObservableProperty]
    private PreferencesViewModel? _preferences;

    /// <summary>Asks the user for a folder (set by the window).</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Raised when the Large interface preference changes, so the window rescales at once.</summary>
    public event Action<bool>? LargeInterfaceChanged;

    [RelayCommand]
    private void OpenPreferences()
    {
        if (Preferences is not null) return;
        var prefs = new PreferencesViewModel(Services, () => Preferences = null, large => LargeInterfaceChanged?.Invoke(large)) { PickFolder = PickFolder };
        prefs.FoldersChanged += () => _ = Welcome.RescanAsync();
        prefs.VeteranChanged += () => (Page as EditorViewModel)?.VeteranChanged();
        Preferences = prefs;
    }

    /// <summary>Runs an action on the UI thread (what sessions use to report back).</summary>
    public static void PostToUi(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    /// <summary>What a session gets from the app: the dispatcher, the game, version pictures drawn as the editor shows the map, and the engine's data for the game's own map settings.</summary>
    public static SessionHooks HooksFor(GameResources? resources, string? engineData)
    {
        var look = new Canvas.MapLook(resources);
        return new SessionHooks(PostToUi, resources, frame => Canvas.MapThumbnail.Render(frame, look, 384, 256), engineData);
    }

    /// <summary>Reads a project's map on a worker and opens it in the editor.</summary>
    public async Task OpenEditorAsync(Project project, string slot)
    {
        var session = await Task.Run(() => EditorSession.Open(project, slot, HooksFor(Services.ResourcesFor(project), Services.EngineData))).ConfigureAwait(true);
        OpenEditor(session);
    }

    /// <summary>Shows an opened session in the editor; called on the UI thread.</summary>
    public void OpenEditor(EditorSession session, string? message = null, bool showVersions = false)
    {
        var editor = new EditorViewModel(Services, session, this, message, showVersions);
        Page = editor;
        Title = $"{session.Project.Info.Name} · WadSmith";
        Remember(session.Project.Folder);
        Log.Info($"Opened {session.Project.Folder} map {session.Slot}");
    }

    /// <summary>Back to the welcome screen, after the open map's changes reach the disk.</summary>
    public async Task CloseEditorAsync()
    {
        if (Page is EditorViewModel editor)
        {
            await editor.Session.FlushAsync().ConfigureAwait(true);
            editor.Session.Dispose();
        }

        Page = Welcome;
        Title = "WadSmith";
        Welcome.RefreshRecent();
    }

    private void Remember(string folder)
    {
        var recent = Services.Settings.Current.Recent;
        recent.RemoveAll(r => string.Equals(r, folder, StringComparison.Ordinal));
        recent.Insert(0, folder);
        if (recent.Count > 12) recent.RemoveRange(12, recent.Count - 12);
        Services.Settings.Save();
    }
}
