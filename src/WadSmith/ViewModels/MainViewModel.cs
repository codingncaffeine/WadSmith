using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

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

    /// <summary>Asks the user for a game file (set by the window).</summary>
    public Func<string, Task<string?>>? PickFile { get; set; }

    /// <summary>Raised when the Large interface preference changes, so the window rescales at once.</summary>
    public event Action<bool>? LargeInterfaceChanged;

    [RelayCommand]
    private void OpenPreferences()
    {
        if (Preferences is not null) return;
        var prefs = new PreferencesViewModel(Services, () =>
        {
            Preferences = null;
            Welcome.TeachingChanged();
        }, large => LargeInterfaceChanged?.Invoke(large)) { PickFolder = PickFolder, PickFile = PickFile };
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
        return new SessionHooks(PostToUi, resources, frame => Canvas.MapThumbnail.Render(frame, look, 384, 256), engineData, QuakePalette);
    }

    /// <summary>Quake's palette from the user's own Quake (its PAK0), for Quake models; null when no Quake was found. Reads a file: call it on a worker.</summary>
    private static byte[]? QuakePalette()
    {
        if (WadSmith.App.App.Services?.Games?.FirstOrDefault(g => g.Game.Engine == "quake") is not { } quake) return null;
        try
        {
            return Core.Quake.PakArchive.Open(quake.Path).Read("gfx/palette.lmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
        {
            WadSmith.Editor.App.Log.Warn($"Quake's palette could not be read from {quake.Path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads a project's map on a worker and opens it in the editor.</summary>
    public async Task OpenEditorAsync(Project project, string slot)
    {
        var session = await Task.Run(() => EditorSession.Open(project, slot, HooksFor(Services.ResourcesFor(project), Services.EngineData))).ConfigureAwait(true);
        OpenEditor(session);
    }

    /// <summary>Shows an opened session in the editor; called on the UI thread.</summary>
    public void OpenEditor(EditorSession session, string? message = null, bool showVersions = false, bool guide = false)
    {
        var editor = new EditorViewModel(Services, session, this, message, showVersions);
        Page = editor;
        if (guide) editor.StartGuideCommand.Execute(null);
        Title = $"{session.Project.Info.Name} · WadSmith";
        Remember(session.Project.Folder);
        Log.Info($"Opened {session.Project.Folder} map {session.Slot}");
    }

    /// <summary>
    /// Opens a game's own map to learn from at a landmark (D28): a throwaway copy in the cache, read-only, centred on the
    /// spot with the feature selected, and a way back to <paramref name="back"/> (kept open as it is).
    /// </summary>
    public async Task PeekAsync(Landmark landmark, FoundGame game, EditorViewModel? back)
    {
        if (Services.EngineData is not { } engine)
        {
            back?.Hint("Opening the game's own maps needs GZDoom installed: WadSmith reads the engine's own rules for what each line does.");
            return;
        }

        var folder = Path.Combine(Services.Paths.Cache, "peek", $"{game.Game.Id}-{landmark.Map}");
        EditorSession session;
        try
        {
            session = await Task.Run(() =>
            {
                // The copy from the last look at this map is replaced: it never holds anything of the user's.
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                var project = Remix.Create(folder, game.Path, landmark.Map, engine, [], game.Game.Title);
                return EditorSession.Open(project, landmark.Map, HooksFor(Services.ResourcesFor(project), engine), "Opened to learn from");
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            back?.Hint($"{game.Game.Title} {landmark.Map} could not be opened: {ex.Message}");
            Log.Warn(ex.Message);
            return;
        }

        session.History.ReadOnly = true;
        var editor = new EditorViewModel(Services, session, this)
        {
            Peek = new PeekInfo($"{game.Game.Title} · {landmark.Map}", landmark.Says, landmark, game, back, landmark.Map),
            PendingPlace = (landmark.At, 640),
        };
        if (LandmarkElement(session, landmark) is { } feature) session.Selection.Set(feature);
        // A look from inside another look replaces it.
        if (Page is EditorViewModel { Peek: not null } leaving) leaving.Session.Dispose();
        Page = editor;
        Title = $"{game.Game.Title} {landmark.Map} · WadSmith";
        Log.Info($"Opened {game.Game.Title} {landmark.Map} to learn from: {landmark.Kind}");
    }

    /// <summary>What a landmark points at, for selecting it: the area there, or the wall or object of its kind nearest the spot.</summary>
    private static Element? LandmarkElement(EditorSession session, Landmark landmark)
    {
        var g = session.Geometry;
        if (landmark.Kind is "switch" or "teleporter" or "exit") return g.LineNear(landmark.At, Landmarks.Near);
        if (landmark.Kind == "key") return g.ThingNear(landmark.At, Landmarks.Near);
        return g.SectorAt(landmark.At);
    }

    /// <summary>Leaves a map opened to learn from: back to the map it was opened from, or the start screen.</summary>
    public void BackFromPeek(EditorViewModel peek)
    {
        peek.Session.Dispose();
        if (peek.Peek?.Back is { } back)
        {
            Page = back;
            Title = $"{back.Session.Project.Info.Name} · WadSmith";
            return;
        }

        Page = Welcome;
        Title = "WadSmith";
    }

    /// <summary>Makes the user's own copy of a map opened to learn from, as Remix on the start screen does; the map they came from is kept and closed first.</summary>
    public async Task RemixPeekAsync(EditorViewModel peek)
    {
        if (peek.Peek is not { } info) return;
        peek.Session.Dispose();
        if (info.Back is { } back)
        {
            await back.Session.FlushAsync().ConfigureAwait(true);
            back.Session.Dispose();
        }

        Page = Welcome;
        Title = "WadSmith";
        if (info.Landmark is null)
        {
            await CopyTourAsync(peek.Session.Project.Folder, info.Slot).ConfigureAwait(true);
            return;
        }

        await Welcome.RemixMapAsync(info.Game, info.Game.Game.Title, info.Slot, "").ConfigureAwait(true);
    }

    /// <summary>
    /// Opens WadSmith's feature tour (D32) for <paramref name="game"/>: built in the cache from its edit script, read-only, with
    /// the way back to <paramref name="back"/>; Play walks it with each room's sign shown in the game.
    /// </summary>
    public async Task OpenTourAsync(FoundGame game, EditorViewModel? back)
    {
        var folder = Path.Combine(Services.Paths.Cache, "tour", game.Game.Id);
        var slot = game.Game.MapNames == "ExMy" ? "E1M1" : "MAP01";
        EditorSession session;
        try
        {
            session = await Task.Run(() =>
            {
                // Built again each time: it never holds anything of the user's.
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                var project = FeatureTour.Create(folder, game.Game.Id, game.Game.Engine, slot);
                return EditorSession.Open(project, slot, HooksFor(Services.ResourcesFor(project), Services.EngineData), "Opened to learn from");
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            back?.Hint($"The feature tour could not be made: {ex.Message}");
            Log.Warn(ex.Message);
            return;
        }

        session.History.ReadOnly = true;
        var editor = new EditorViewModel(Services, session, this)
        {
            Peek = new PeekInfo($"WadSmith's feature tour · {game.Game.Title}", "A room for each feature, each with a sign. Press Play to walk round it: the game shows each room's sign as you walk in.", null, game, back, slot),
        };
        if (Page is EditorViewModel { Peek: not null } leaving) leaving.Session.Dispose();
        Page = editor;
        Title = $"Feature tour · WadSmith";
        Log.Info($"Opened the feature tour for {game.Game.Title}");
    }

    // The user's own copy of the tour, signs and all, in their projects.
    private async Task CopyTourAsync(string from, string slot)
    {
        var folder = await Task.Run(() =>
        {
            Directory.CreateDirectory(Services.Paths.Projects);
            var to = Path.Combine(Services.Paths.Projects, "Feature tour");
            for (var n = 2; Directory.Exists(to); n++) to = Path.Combine(Services.Paths.Projects, $"Feature tour {n}");
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            return to;
        }).ConfigureAwait(true);
        await OpenEditorAsync(Project.Open(folder), slot).ConfigureAwait(true);
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

    /// <summary>Opens another map of the project being edited in place of this one, after keeping this one's changes.</summary>
    public async Task SwitchMapAsync(Project project, string slot)
    {
        if (Page is EditorViewModel { } editor)
        {
            if (string.Equals(editor.Session.Slot, slot, StringComparison.OrdinalIgnoreCase)) return;
            await editor.Session.FlushAsync().ConfigureAwait(true);
            editor.Session.Dispose();
        }

        await OpenEditorAsync(project, slot).ConfigureAwait(true);
    }

    /// <summary>Back to the start screen from a page that is not the editor (Make me a map).</summary>
    public void ShowWelcome()
    {
        Page = Welcome;
        Title = "WadSmith";
    }

    /// <summary>Opens "Make me a map" (D50) for a game found on this computer, for a map that plays in <paramref name="target"/>.</summary>
    public void OpenMakeMap(FoundGame game, string target) => _ = OpenMakeMapAsync(game, target);

    /// <summary>Opens "Make me a map", closing the map being edited first; <paramref name="like"/> starts it from a made map's choices.</summary>
    public async Task OpenMakeMapAsync(FoundGame game, string target, WadSmith.Editor.Generate.GeneratorSettings? like = null)
    {
        if (Page is EditorViewModel) await CloseEditorAsync().ConfigureAwait(true);
        Page = new MakeMapViewModel(Services, this, game, target, like);
        Title = $"Make me a map · {game.Game.Title} · WadSmith";
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
