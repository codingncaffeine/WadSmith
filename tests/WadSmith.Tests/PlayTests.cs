using WadSmith.Core.Engines;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>Finding the engine, its command line, and the settings WadSmith plays with (Phase 4); no engine is started.</summary>
public sealed class PlayTests
{
    [Fact]
    public void EnginesAreFoundOnThePathUZDoomFirstAndAChosenOneComesFirst()
    {
        using var temp = new TempFolder();
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        File.WriteAllText(Path.Combine(bin, "gzdoom" + exe), "");
        File.WriteAllText(Path.Combine(bin, "uzdoom" + exe), "");
        var mine = Path.Combine(temp.Path, "my-engine");
        File.WriteAllText(mine, "");
        string? Env(string name) => name switch { "PATH" => bin, "HOME" => temp.Path, "FLATPAK_SYSTEM_DIR" => Path.Combine(temp.Path, "no-flatpaks"), _ => null };

        // UZDoom before GZDoom (D64), whichever the PATH names first.
        Assert.Equal(["UZDoom", "GZDoom"], EngineLocator.Find(null, Env).Select(e => e.Name));
        var found = EngineLocator.Find(mine, Env);
        Assert.Equal(("Your engine", mine, "custom"), (found[0].Name, found[0].Program, found[0].Kind));
        Assert.Equal(["Your engine", "UZDoom", "GZDoom"], found.Select(e => e.Name));

        // Choosing one of those found keeps its name and moves it to the front.
        var gzdoom = Path.Combine(bin, "gzdoom" + exe);
        Assert.Equal([("GZDoom", gzdoom), ("UZDoom", Path.Combine(bin, "uzdoom" + exe))], EngineLocator.Find(gzdoom, Env).Select(e => (e.Name, e.Program)));

        // A choice no longer there is passed over.
        Assert.Equal("UZDoom", EngineLocator.Find(Path.Combine(temp.Path, "gone"), Env)[0].Name);
    }

    [Fact]
    public void TheCommandLoadsTheGameTheFilesAndTheMapWithWadSmithsSettingsAndLog()
    {
        var request = new PlayRequest("/games/DOOM2.WAD", ["/projects/My map"], "MAP01", 4, "/data/engine/gzdoom.ini", "/data/engine/last-play.log");
        var (program, args) = PlayCommand.For(new EngineInstall("GZDoom", "/usr/bin/gzdoom", "native"), request);
        Assert.Equal("/usr/bin/gzdoom", program);
        // The log is a plain name: the engine is started in the log's folder (UZDoom 5 takes no path there).
        Assert.Equal(["-iwad", "/games/DOOM2.WAD", "-file", "/projects/My map", "+map", "MAP01", "-skill", "4", "-config", "/data/engine/gzdoom.ini", "+logfile", "last-play"], args);

        // The Flatpak sandbox is given the folders it needs and works in the log's folder, and the Flatpak's own ID is run.
        string Folder(string path) => $"--filesystem={Path.GetDirectoryName(path)}";
        foreach (var app in new[] { EngineLocator.UZDoomApp, EngineLocator.GZDoomApp })
        {
            var (flatpak, sandboxed) = PlayCommand.For(new EngineInstall("Flatpak", "/usr/bin/flatpak", "flatpak", app), request);
            Assert.Equal("/usr/bin/flatpak", flatpak);
            Assert.Equal(["run", Folder("/games/DOOM2.WAD"), Folder("/data/engine/gzdoom.ini"), "--filesystem=/projects/My map", $"--cwd={EngineLogFile.Folder(request.Log)}", app, "-iwad"], sandboxed.Take(7));
        }
    }

    [Fact]
    public void AGamePlayedAsItIsStartsAtItsTitleScreenWithTheEnginesOwnSettings()
    {
        var (program, args) = PlayCommand.ForGame(new EngineInstall("UZDoom", "/usr/bin/uzdoom", "native"), "/games/HEXEN.WAD", ["/games/HEXDD.WAD"]);
        Assert.Equal(("/usr/bin/uzdoom", "-iwad /games/HEXEN.WAD -file /games/HEXDD.WAD"), (program, string.Join(' ', args)));

        // In a Flatpak the sandbox may read the games' folders, and nothing else is passed: no map, no settings file, no log.
        var (_, sandboxed) = PlayCommand.ForGame(new EngineInstall("Flatpak", "/usr/bin/flatpak", "flatpak", EngineLocator.UZDoomApp), "/games/DOOM2.WAD", ["/packs/sigil2.wad"]);
        Assert.Equal($"run --filesystem=/games:ro --filesystem=/packs:ro {EngineLocator.UZDoomApp} -iwad /games/DOOM2.WAD -file /packs/sigil2.wad", string.Join(' ', sandboxed));
    }

    [Fact]
    public void TheLogIsWhicheverFileTheEngineWroteInItsFolder()
    {
        using var temp = new TempFolder();
        var log = Path.Combine(temp.Path, "engine", "last-play.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        Assert.Equal(("last-play", Path.GetDirectoryName(log)), (EngineLogFile.Argument(log), EngineLogFile.Folder(log)));
        Assert.Equal("view-2-a", EngineLogFile.Argument(Path.Combine(temp.Path, "view.2.a.log")));
        Assert.Null(EngineLogFile.Written(log));

        // GZDoom writes the name as given; UZDoom 5 writes log-NAME.txt; the newer is this run's.
        var gzdoom = Path.Combine(temp.Path, "engine", "last-play");
        var uzdoom = Path.Combine(temp.Path, "engine", "log-last-play.txt");
        File.WriteAllText(gzdoom, "GZDoom's\n");
        Assert.Equal(gzdoom, EngineLogFile.Written(log));
        File.WriteAllText(uzdoom, "UZDoom's\n");
        File.SetLastWriteTimeUtc(gzdoom, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(uzdoom, EngineLogFile.Written(log));

        // Before a run, what earlier runs wrote goes.
        File.WriteAllText(log, "an older WadSmith's\n");
        EngineLogFile.Clear(log);
        Assert.Null(EngineLogFile.Written(log));
    }

    [Fact]
    public void WadSmithPlaysWithItsOwnCopyOfTheUsersSettingsAndNeverWritesTheirs()
    {
        using var temp = new TempFolder();
        var theirs = Path.Combine(temp.Path, "home/.config/gzdoom/gzdoom.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
        File.WriteAllText(theirs, "[GlobalSettings]\nfullscreen=true\n");
        var before = File.ReadAllBytes(theirs);
        string? Env(string name) => name == "HOME" ? Path.Combine(temp.Path, "home") : null;
        var mine = EngineConfig.Prepare(Path.Combine(temp.Path, "data"), Env);
        Assert.Equal(before, File.ReadAllBytes(mine));
        File.WriteAllText(mine, "changed in the game");
        Assert.Equal(mine, EngineConfig.Prepare(Path.Combine(temp.Path, "data"), Env));
        Assert.Equal("changed in the game", File.ReadAllText(mine));
        Assert.Equal(before, File.ReadAllBytes(theirs));
    }

    [Fact]
    public void UZDoomPlaysWithASettingsFileOfItsOwnCopiedFromItsUsersElseFromGZDooms()
    {
        using var temp = new TempFolder();
        var home = Path.Combine(temp.Path, "home");
        var gzdoomIni = Path.Combine(home, ".config/gzdoom/gzdoom.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(gzdoomIni)!);
        File.WriteAllText(gzdoomIni, "[GlobalSettings]\n# GZDoom's\n");
        string? Env(string name) => name == "HOME" ? home : null;
        var uzdoom = new EngineInstall("UZDoom", "/usr/bin/uzdoom", "native");
        var gzdoom = new EngineInstall("GZDoom", "/usr/bin/gzdoom", "native");

        // No UZDoom settings yet: UZDoom's own file starts as a copy of GZDoom's, and the two stay apart.
        var data = Path.Combine(temp.Path, "data");
        var forUZDoom = EngineConfig.Prepare(data, Env, uzdoom);
        Assert.Equal(Path.Combine(data, "engine", "uzdoom.ini"), forUZDoom);
        Assert.Equal("[GlobalSettings]\n# GZDoom's\n", File.ReadAllText(forUZDoom));
        Assert.Equal(Path.Combine(data, "engine", "gzdoom.ini"), EngineConfig.Prepare(data, Env, gzdoom));

        // With the user's own UZDoom settings (under XDG_CONFIG_HOME, where UZDoom keeps them), those are copied.
        var config = Path.Combine(temp.Path, "config");
        var uzdoomIni = Path.Combine(config, "uzdoom/uzdoom.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(uzdoomIni)!);
        File.WriteAllText(uzdoomIni, "[GlobalSettings]\n# UZDoom's\n");
        string? WithConfig(string name) => name switch { "HOME" => home, "XDG_CONFIG_HOME" => config, _ => null };
        var other = Path.Combine(temp.Path, "data2");
        Assert.Equal("[GlobalSettings]\n# UZDoom's\n", File.ReadAllText(EngineConfig.Prepare(other, WithConfig, uzdoom)));
        Assert.Equal("[GlobalSettings]\n# GZDoom's\n", File.ReadAllText(EngineConfig.Prepare(other, WithConfig, gzdoom)));

        // The UZDoom Flatpak's settings are in its own config folder; an engine whose file names UZDoom is UZDoom's kind.
        Assert.Contains((Path.Combine(home, ".var/app", EngineLocator.UZDoomApp, "config/uzdoom/uzdoom.ini"), "uzdoom"), EngineConfig.UserSettings(Env, []));
        Assert.Equal("uzdoom", new EngineInstall("Your engine", "/opt/Linux-UZDoom-Release-x86_64.AppImage", "custom").Family);
        Assert.Equal("gzdoom", new EngineInstall("Your engine", "/opt/doom-engine", "custom").Family);
    }

    [Fact]
    public void AnAddOnPlaysOverItsBaseGameWithTheProjectOnTop()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "tnt");
        GameDefinition Def(string id, string? over) => new() { Id = id, Title = id, Engine = "doom", MapNamespace = "doom", MustContain = [], AddOnFor = over };
        FoundGame[] games = [new(Def("doom2", null), "/games/DOOM2.WAD", 1), new(Def("tnt", "doom2"), "/games/TNT.WAD", 1)];
        var (request, why) = PlayRequests.For(project, "MAP01", games, "/c", "/l", 3);
        Assert.Null(why);
        Assert.Equal("/games/DOOM2.WAD", request!.Iwad);
        Assert.Equal(["/games/TNT.WAD", Path.GetFullPath(project.Folder)], request.Files);

        var (none, missing) = PlayRequests.For(project, "MAP01", games[1..], "/c", "/l", 3);
        Assert.Null(none);
        Assert.Contains("another game", missing);
    }
}
