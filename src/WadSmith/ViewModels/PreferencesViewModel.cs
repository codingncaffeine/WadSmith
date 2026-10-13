using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor.App;

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

    /// <summary>Asks the user for a game file (set by the window); null result when they cancel.</summary>
    public Func<string, Task<string?>>? PickFile { get; set; }

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

    public bool CoverPictures
    {
        get => _services.Settings.Current.CoverPictures;
        set
        {
            _services.Settings.Current.CoverPictures = value;
            _services.Settings.Save();
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

    public bool ShelfSounds
    {
        get => _services.Settings.Current.ShelfSounds;
        set
        {
            _services.Settings.Current.ShelfSounds = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the Guide invites (the "Guided first map" button on each game, D34).</summary>
    public bool GuidePrompts
    {
        get => _services.Settings.Current.GuidePrompts;
        set
        {
            if (value == GuidePrompts) return;
            _services.Settings.Current.GuidePrompts = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>How much a tooltip says (D34): 0 all of it, 1 its first sentence, 2 nothing.</summary>
    public int Tooltips
    {
        get => (int)_services.Settings.Current.Tooltips;
        set
        {
            if (value < 0 || value == Tooltips) return;
            _services.Settings.Current.Tooltips = (TooltipLength)value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether firsts are celebrated: the card after the first Play (D33).</summary>
    public bool Celebrations
    {
        get => _services.Settings.Current.Celebrations;
        set
        {
            if (value == Celebrations) return;
            _services.Settings.Current.Celebrations = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Raised when the names or the keys change, so an open map shows the change at once.</summary>
    public event Action? VeteranChanged;

    /// <summary>Labels show the classic names (sector, linedef, thing); the tooltips name the friendly ones (D3).</summary>
    public bool ClassicNames
    {
        get => _services.Settings.Current.ClassicNames;
        set
        {
            if (value == ClassicNames) return;
            _services.Settings.Current.ClassicNames = value;
            _services.Words.Classic = value;
            _services.Settings.Save();
            OnPropertyChanged();
            VeteranChanged?.Invoke();
        }
    }

    /// <summary>The keys of Ultimate Doom Builder's defaults instead of WadSmith's (D14).</summary>
    public bool DoomBuilderKeys
    {
        get => _services.Settings.Current.DoomBuilderKeys;
        set
        {
            if (value == DoomBuilderKeys) return;
            _services.Settings.Current.DoomBuilderKeys = value;
            _services.Settings.Save();
            OnPropertyChanged();
            VeteranChanged?.Invoke();
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
        if (folder is not null) AddFolder(folder);
    }

    /// <summary>The user picks a game file: its folder is added (the folders around it are looked in too).</summary>
    [RelayCommand]
    private async Task AddGameFileAsync()
    {
        if (PickFile is null) return;
        var file = await PickFile("Pick a game file: DOOM.WAD, DOOM2.WAD, HERETIC.WAD, HEXEN.WAD, STRIFE1.WAD, or a ZIP holding one").ConfigureAwait(true);
        if (file is not null && Path.GetDirectoryName(file) is { Length: > 0 } folder) AddFolder(folder);
    }

    private void AddFolder(string folder)
    {
        if (_services.Settings.Current.GameFolders.Contains(folder, StringComparer.Ordinal)) return;
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

    /// <summary>Where "Make me a map" puts the maps it makes.</summary>
    public string MadeMapsFolder => _services.MadeMapsFolder;

    /// <summary>Whether the folder is the one chosen here (else the default).</summary>
    public bool MadeMapsFolderChosen => _services.Settings.Current.MadeMapsFolder is { Length: > 0 };

    [RelayCommand]
    private async Task ChooseMadeMapsFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder("Choose the folder maps made for you go to").ConfigureAwait(true);
        if (folder is null) return;
        _services.Settings.Current.MadeMapsFolder = folder;
        _services.Settings.Save();
        OnPropertyChanged(nameof(MadeMapsFolder));
        OnPropertyChanged(nameof(MadeMapsFolderChosen));
        _foldersChanged = true;
    }

    [RelayCommand]
    private void DefaultMadeMapsFolder()
    {
        _services.Settings.Current.MadeMapsFolder = null;
        _services.Settings.Save();
        OnPropertyChanged(nameof(MadeMapsFolder));
        OnPropertyChanged(nameof(MadeMapsFolderChosen));
        _foldersChanged = true;
    }

    [RelayCommand]
    private void Close()
    {
        _close();
        if (_foldersChanged) FoldersChanged?.Invoke();
    }
}
