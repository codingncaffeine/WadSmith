using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WadSmith.App.ViewModels;

/// <summary>A game folder the user added, in the Preferences list.</summary>
internal sealed record GameFolderRow(string Folder);

/// <summary>
/// Preferences: only the settings something in WadSmith reads today (larger interface, tips, the
/// desktop's title bar, game folders). Every change is saved at once; there is no Apply step.
/// </summary>
internal sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action _close;
    private readonly Action<bool> _largeInterfaceChanged;
    private bool _foldersChanged;

    public PreferencesViewModel(AppServices services, Action close, Action<bool> largeInterfaceChanged)
    {
        _services = services;
        _close = close;
        _largeInterfaceChanged = largeInterfaceChanged;
        foreach (var f in services.Settings.Current.GameFolders) GameFolders.Add(new GameFolderRow(f));
    }

    /// <summary>Asks the user for a folder (set by the window); null result when they cancel.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Raised when Preferences close after the game folders changed, so the games are looked for again.</summary>
    public event Action? FoldersChanged;

    public ObservableCollection<GameFolderRow> GameFolders { get; } = [];

    [ObservableProperty]
    private string _tipsNote = "";

    /// <summary>The build, for About: "WadSmith 0.0.1".</summary>
    public static string Version => $"WadSmith {Core.BuildInfo.Version.Split('+')[0]}";

    public bool LargeInterface
    {
        get => _services.Settings.Current.LargeInterface;
        set
        {
            _services.Settings.Current.LargeInterface = value;
            _services.Settings.Save();
            _largeInterfaceChanged(value);
            OnPropertyChanged();
        }
    }

    public bool CoachTips
    {
        get => _services.Settings.Current.CoachTips;
        set
        {
            _services.Settings.Current.CoachTips = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool UseSystemTitleBar
    {
        get => _services.Settings.Current.UseSystemTitleBar;
        set
        {
            _services.Settings.Current.UseSystemTitleBar = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ShowTipsAgain()
    {
        _services.Settings.Current.CoachSeen.Clear();
        _services.Settings.Save();
        TipsNote = "The tips will start again from the first one.";
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder("Choose the folder your games are in").ConfigureAwait(true);
        if (folder is null || _services.Settings.Current.GameFolders.Contains(folder, StringComparer.Ordinal)) return;
        _services.Settings.Current.GameFolders.Add(folder);
        _services.Settings.Save();
        GameFolders.Add(new GameFolderRow(folder));
        _foldersChanged = true;
    }

    [RelayCommand]
    private void RemoveFolder(GameFolderRow row)
    {
        _services.Settings.Current.GameFolders.RemoveAll(f => string.Equals(f, row.Folder, StringComparison.Ordinal));
        _services.Settings.Save();
        GameFolders.Remove(row);
        _foldersChanged = true;
    }

    [RelayCommand]
    private void Close()
    {
        _close();
        if (_foldersChanged) FoldersChanged?.Invoke();
    }
}
