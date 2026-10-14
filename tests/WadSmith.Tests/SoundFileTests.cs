using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Sounds and music of the user's own (Phase 7): a sound placed in the map with its own way of playing, music as the map's.</summary>
public sealed class SoundFileTests
{
    /// <summary>An Ogg stream's first bytes: enough for the content check, which is all WadSmith reads.</summary>
    internal static byte[] Ogg() => [.. "OggS"u8, .. new byte[60]];

    internal static byte[] Midi() => [.. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 0, 96];

    private static (TempFolder Temp, Project Project, GameResources Game) NewProject()
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        return (temp, project, game);
    }

    [Fact]
    public void ASoundBecomesALumpASoundTableLineAndANumberInTheClassThatPlaysIt()
    {
        var (temp, project, game) = NewProject();
        using var _ = temp;
        var rain = Path.Combine(temp.Path, "Heavy rain.ogg");
        File.WriteAllBytes(rain, Ogg());
        var creak = Path.Combine(temp.Path, "creak.wav");
        File.WriteAllBytes(creak, [.. "RIFF"u8, 36, 0, 0, 0, .. "WAVE"u8, .. new byte[32]]);

        var first = SoundFiles.ImportSound(project, game, rain);
        var second = SoundFiles.ImportSound(project, game, creak);

        Assert.Equal((0, "Heavy rain", "HEAVYRAI"), (first.Sound, first.Title, first.Lump));
        Assert.Equal((1, "CREAK"), (second.Sound, second.Lump));
        Assert.Equal(first.Number, second.Number);
        Assert.True(File.Exists(Path.Combine(project.Folder, "sounds", "HEAVYRAI.ogg")));
        Assert.True(File.Exists(Path.Combine(project.Folder, "sounds", "CREAK.wav")));
        var table = File.ReadAllText(Path.Combine(project.Folder, ActorBorrowing.SoundFile));
        Assert.Contains("wadsmith/heavyrai HEAVYRAI\n", table, StringComparison.Ordinal);
        Assert.Contains("wadsmith/creak CREAK\n", table, StringComparison.Ordinal);
        var script = File.ReadAllText(Path.Combine(project.Folder, SoundFiles.ScriptFile));
        Assert.Contains("case 0: return \"wadsmith/heavyrai\";", script, StringComparison.Ordinal);
        Assert.Contains("case 1: return \"wadsmith/creak\";", script, StringComparison.Ordinal);
        Assert.Contains($"#include \"{SoundFiles.ScriptFile}\"", File.ReadAllText(Path.Combine(project.Folder, DukeBorrowing.ScriptFile)), StringComparison.Ordinal);
        Assert.Equal("Sound", game.Things.Find(first.Number)?.Name);

        // The class is known by its number when the project is opened again, and MAPINFO places it by that number.
        var again = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, GameFiles.Require("freedoom2.wad"), 0)], null)!;
        Assert.Equal(SoundFiles.ClassName, again.Things.Find(first.Number)?.ClassName);
        Assert.Contains($"{first.Number} = {SoundFiles.ClassName}", ProjectMapInfo.Text([], MapDefinitions.None, _ => [], project.CopiedNow()), StringComparison.Ordinal);
    }

    [Fact]
    public void MusicIsNoSoundAndBecomesTheGamesMusicToChoose()
    {
        var (temp, project, game) = NewProject();
        using var _ = temp;
        var theme = Path.Combine(temp.Path, "theme.mid");
        File.WriteAllBytes(theme, Midi());

        Assert.Throws<WadSmith.Core.CorruptDataException>(() => SoundFiles.ImportSound(project, game, theme));
        var name = SoundFiles.ImportMusic(project, game, theme);
        Assert.Equal("THEME", name);
        Assert.True(File.Exists(Path.Combine(project.Folder, "music", "THEME.mid")));
        Assert.Contains("THEME", game.Music);
    }

    [Fact]
    public void ASoundsSettingsLiveInItsArgumentsAndSayWhatItDoes()
    {
        var t = new Thing();
        var settings = new SoundSettings(3, SoundPlays.Random, 40, true, 2, 9);
        settings.Args.CopyTo(t.Args, 0);
        Assert.Equal(settings, SoundSettings.Of(t));
        Assert.Equal([3, 40, 1 | 4, 2, 9], t.Args);
        Assert.Equal("Plays rain now and then, every 2 to 9 seconds, at 40% volume, heard everywhere in the map.", SoundFiles.Describe(settings, "rain"));
        Assert.Equal("Plays rain once, when a player first comes into its room, at 100% volume, heard nearby.", SoundFiles.Describe(SoundSettings.New(0) with { Plays = SoundPlays.Once }, "rain"));
    }
}
