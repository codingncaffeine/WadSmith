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
        Assert.True(services.Settings.Flush(TimeSpan.FromSeconds(5)), $"the settings did not reach the disk in 5 s: writer {services.Settings.WriterState}");

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
    public async Task AnEngineChosenIsKeptAnAppImageChosenBecomesAProgramAndOneGoneIsSaid()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "AppImages are Linux's.");
        using var temp = new TempFolder();
        var (services, file) = Services(temp);
        var image = Path.Combine(temp.Path, "Downloads", "Linux-UZDoom-Release-x86_64.AppImage");
        EngineLocatorTests.PlantAppImage(image, startable: false);
        var prefs = new PreferencesViewModel(services, () => { }, _ => { }) { PickProgram = _ => Task.FromResult<string?>(image) };
        await prefs.EnginesFound;
        Assert.Null(prefs.ChosenEngine!.Choice);
        Assert.Equal("The one found first", prefs.Engines[0].Name);

        // Chosen with Choose…: kept, listed as what it is, and made a program so Play can start it.
        await prefs.ChooseEngineCommand.ExecuteAsync(null);
        await prefs.EnginesFound;
        Assert.Equal(image, services.Settings.Current.EnginePath);
        Assert.Equal(("UZDoom (AppImage)", image), (prefs.ChosenEngine!.Name, prefs.ChosenEngine.Choice));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserExecute, File.GetUnixFileMode(image) & UnixFileMode.UserExecute);
        Assert.Contains("marked it", prefs.EngineNote, StringComparison.Ordinal);

        // Back to the one found first, saved.
        prefs.ChosenEngine = prefs.Engines[0];
        await prefs.EnginesFound;
        Assert.Null(services.Settings.Current.EnginePath);
        Assert.True(services.Settings.Flush(TimeSpan.FromSeconds(5)), $"the settings did not reach the disk in 5 s: writer {services.Settings.WriterState}");
        Assert.Null(SettingsStore.Load(file).Current.EnginePath);

        // An engine chosen that has gone is said, and stays chosen until another is.
        var gone = Path.Combine(temp.Path, "gone", "uzdoom");
        services.Settings.Current.EnginePath = gone;
        var again = new PreferencesViewModel(services, () => { }, _ => { });
        await again.EnginesFound;
        Assert.Equal(gone, again.ChosenEngine!.Choice);
        Assert.Contains($"not at {gone} any more", again.EngineNote, StringComparison.Ordinal);
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
