using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>Runs an action on the UI thread (what sessions use to report back).</summary>
    public static void PostToUi(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    /// <summary>Reads a project's map on a worker and opens it in the editor.</summary>
    public async Task OpenEditorAsync(Project project, string slot)
    {
        var session = await Task.Run(() => EditorSession.Open(project, slot, PostToUi, Services.ResourcesFor(project))).ConfigureAwait(true);
        OpenEditor(session);
    }

    /// <summary>Shows an opened session in the editor; called on the UI thread.</summary>
    public void OpenEditor(EditorSession session)
    {
        var editor = new EditorViewModel(Services, session, this);
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
