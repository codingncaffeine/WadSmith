using WadSmith.App;
using WadSmith.App.ViewModels;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>Preferences save every change at once and reach what reads them.</summary>
public sealed class PreferencesTests
{
    private static (AppServices Services, string SettingsFile) Services(TempFolder temp)
    {
        var paths = AppPaths.Resolve(temp.Path, _ => null);
        paths.EnsureCreated();
        var settings = SettingsStore.Load(paths.SettingsFile);
        return (new AppServices(paths, settings, new LaunchOptions(temp.Path, null)), paths.SettingsFile);
    }

    [Fact]
    public void EachChangeIsSavedAndTheLargerInterfaceAppliesAtOnce()
    {
        using var temp = new TempFolder();
        var (services, file) = Services(temp);
        services.Settings.Current.GameFolders.Add("/games/one");
        services.Settings.Current.CoachSeen.AddRange(["zoom", "select"]);
        var scaled = new List<bool>();
        var closed = false;
        var prefs = new PreferencesViewModel(services, () => closed = true, scaled.Add);
        var rescans = 0;
        prefs.FoldersChanged += () => rescans++;

        prefs.LargeInterface = true;
        prefs.CoachTips = false;
        prefs.UseSystemTitleBar = true;
        prefs.ShowTipsAgainCommand.Execute(null);
        prefs.RemoveFolderCommand.Execute(prefs.GameFolders[0]);
        prefs.CloseCommand.Execute(null);
        Assert.True(services.Settings.Flush(TimeSpan.FromSeconds(5)));

        Assert.Equal([true], scaled);
        Assert.True(closed);
        Assert.Equal(1, rescans);
        var saved = SettingsStore.Load(file).Current;
        Assert.True(saved.LargeInterface);
        Assert.False(saved.CoachTips);
        Assert.True(saved.UseSystemTitleBar);
        Assert.Empty(saved.CoachSeen);
        Assert.Empty(saved.GameFolders);
    }

    [Fact]
    public void ClosingWithoutTouchingTheFoldersDoesNotLookForGamesAgain()
    {
        using var temp = new TempFolder();
        var (services, _) = Services(temp);
        var prefs = new PreferencesViewModel(services, () => { }, _ => { });
        var rescans = 0;
        prefs.FoldersChanged += () => rescans++;
        prefs.CoachTips = false;
        prefs.CloseCommand.Execute(null);
        Assert.Equal(0, rescans);
    }
}
