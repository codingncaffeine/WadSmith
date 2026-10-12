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
    }

    public AppServices Services { get; }

    public WelcomeViewModel Welcome { get; }

    [ObservableProperty]
    private object _page;

    [ObservableProperty]
    private string _title = "WadSmith";

    /// <summary>Opens a project's map in the editor; called on the UI thread.</summary>
    public void OpenEditor(Project project, string slot)
    {
        var session = EditorSession.Open(project, slot, action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        var editor = new EditorViewModel(Services, session, this);
        Page = editor;
        Title = $"{project.Info.Name} · WadSmith";
        Remember(project.Folder);
        Log.Info($"Opened {project.Folder} map {slot}");
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
