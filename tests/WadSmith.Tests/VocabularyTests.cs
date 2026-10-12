using WadSmith.App;
using WadSmith.App.ViewModels;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>Classic names (D3): every label shows one name and its tooltip the other, whichever is chosen.</summary>
public sealed class VocabularyTests
{
    [Fact]
    public void ALabelShowsOneNameAndItsTooltipTheOther()
    {
        var words = Vocabulary.Load();
        Assert.Equal("Brightness", words.Label("Brightness"));
        Assert.Equal("Brightness of all. Classic name: Light level of all.", words.Tip("Brightness of all", "Brightness of all."));
        Assert.Equal("Classic name: Sector.", words.Tip("ITS AREA", ""));
        Assert.Equal("Floor height", words.Label("Floor height"));
        Assert.Equal("Sets it.", words.Tip("Floor height", "Sets it."));

        words.Classic = true;
        Assert.Equal("Light level", words.Label("Brightness"));
        Assert.Equal("Light level", words.Label("brightness"));
        Assert.Equal("SECTOR", words.Label("ITS AREA"));
        Assert.Equal("Sets it. WadSmith calls it “Brightness”.", words.Tip("Brightness", "Sets it."));
        Assert.Equal("WadSmith calls it “Its area”.", words.Tip("ITS AREA", ""));
        Assert.Equal("Brightness", words.FriendlyFor("light level"));
    }

    [Fact]
    public void EveryEntryNamesSomethingElse()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Core.DataFiles.ReadText("ui/vocabulary.json"));
        var friendly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.RootElement.GetProperty("labels").EnumerateArray())
        {
            var (f, c) = (e.GetProperty("friendly").GetString()!, e.GetProperty("classic").GetString()!);
            Assert.True(friendly.Add(f), $"{f} is listed twice");
            Assert.NotEqual(f, c, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheInspectorShowsTheChosenNamesAndChangesWithThePreference()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "P", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", _ => { });
        var words = Vocabulary.Load();
        var inspector = new InspectorViewModel(session, words);
        session.Selection.Set(session.Map.Sectors[0]);
        Assert.Equal("Room area", inspector.Heading);
        Assert.Equal("Classic name: Sector.", inspector.HeadingTip);
        var light = inspector.Rows.Single(r => r.Label == "Brightness");
        Assert.EndsWith("Classic name: Light level.", light.Tooltip);
        Assert.Contains(inspector.Rows, r => r.Label == "Floor height");

        words.Classic = true;
        inspector.Rebuild();
        Assert.Equal("Sector", inspector.Heading);
        Assert.Equal("WadSmith calls it “Room area”.", inspector.HeadingTip);
        Assert.EndsWith("WadSmith calls it “Brightness”.", inspector.Rows.Single(r => r.Label == "Light level").Tooltip);
        Assert.DoesNotContain(inspector.Rows, r => r.Label == "Brightness");

        // A wall's rows: the flags in the classic words.
        session.Selection.Set(session.Map.Lines[0]);
        Assert.Equal("One-sided linedef", inspector.Heading);
        Assert.Contains(inspector.Rows, r => r.Label == "Impassable");
    }

    [Fact]
    public void PreferencesSwitchTheNamesAndTheKeysAtOnce()
    {
        using var temp = new TempFolder();
        var paths = AppPaths.Resolve(temp.Path, _ => null);
        paths.EnsureCreated();
        var services = new AppServices(paths, SettingsStore.Load(paths.SettingsFile), new LaunchOptions(temp.Path, null));
        var prefs = new PreferencesViewModel(services, () => { }, _ => { });
        var told = 0;
        prefs.VeteranChanged += () => told++;
        Assert.False(services.Words.Classic);

        prefs.ClassicNames = true;
        prefs.DoomBuilderKeys = true;
        prefs.DoomBuilderKeys = true;
        Assert.True(services.Words.Classic);
        Assert.True(services.Settings.Current.DoomBuilderKeys);
        Assert.Equal(2, told);
        Assert.True(services.Settings.Flush(TimeSpan.FromSeconds(5)));
        Assert.True(SettingsStore.Load(paths.SettingsFile).Current.ClassicNames);
    }
}

/// <summary>Through the real window: Preferences' switch reaches the open map's Inspector, tools and top bar at once.</summary>
[Collection(TimingCollection.Name)]
public sealed class ClassicNamesInTheWindowTests
{
    [Fact]
    public async Task ThePreferenceRenamesTheOpenMapAtOnce()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.Session.Selection.Set(editor.Session.Map.Sectors[0]);
                Ui.Pump(0.2);
                var before = Shown(window, editor);
                vm.OpenPreferencesCommand.Execute(null);
                vm.Preferences!.ClassicNames = true;
                vm.Preferences!.CloseCommand.Execute(null);
                Ui.Pump(0.3);
                return (before, after: Shown(window, editor));
            }
            finally
            {
                App.App.Services!.Settings.Current.ClassicNames = false;
                App.App.Services.Words.Classic = false;
                window.Close();
            }
        });
        Assert.Equal("Room area | Room | ▶ Play | Brightness", seen.before);
        Assert.Equal("Sector | Sectors | ▶ Test | Light level", seen.after);
    }

    // The Inspector's heading, the Room tool's button, the Play button and the room's light row, as the window shows them.
    private static string Shown(Avalonia.Controls.Window window, EditorViewModel editor)
    {
        var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ContentControl>().Where(b => b is Avalonia.Controls.Button or Avalonia.Controls.SplitButton).Select(b => b.Content as string).ToList();
        var room = buttons.Contains("Sectors") ? "Sectors" : buttons.Contains("Room") ? "Room" : "?";
        var play = buttons.FirstOrDefault(b => b?.StartsWith('▶') == true) ?? "?";
        var light = editor.Inspector.Rows.Select(r => r.Label).FirstOrDefault(l => l is "Brightness" or "Light level") ?? "?";
        return $"{editor.Inspector.Heading} | {room} | {play} | {light}";
    }
}
