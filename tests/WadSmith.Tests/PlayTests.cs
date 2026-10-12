using WadSmith.Core.Engines;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>Finding the engine, its command line, and the settings WadSmith plays with (Phase 4); no engine is started.</summary>
public sealed class PlayTests
{
    [Fact]
    public void EnginesAreFoundOnThePathAndAChosenOneComesFirst()
    {
        using var temp = new TempFolder();
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "gzdoom"), "");
        File.WriteAllText(Path.Combine(bin, "uzdoom"), "");
        var mine = Path.Combine(temp.Path, "my-engine");
        File.WriteAllText(mine, "");
        string? Env(string name) => name switch { "PATH" => bin, "HOME" => temp.Path, _ => null };
        Assert.Equal(["GZDoom", "UZDoom"], EngineLocator.Find(null, Env).Select(e => e.Name));
        var found = EngineLocator.Find(mine, Env);
        Assert.Equal(("Your engine", mine, "custom"), (found[0].Name, found[0].Program, found[0].Kind));

        // The Flatpak one is known by its installed folder (with flatpak itself on the path).
        File.WriteAllText(Path.Combine(bin, "flatpak"), "");
        Directory.CreateDirectory(Path.Combine(temp.Path, ".local/share/flatpak/app/org.zdoom.GZDoom"));
        Assert.Contains(EngineLocator.Find(null, Env), e => e.Kind == "flatpak");
    }

    [Fact]
    public void TheCommandLoadsTheGameTheFilesAndTheMapWithWadSmithsSettingsAndLog()
    {
        var request = new PlayRequest("/games/DOOM2.WAD", ["/projects/My map"], "MAP01", 4, "/data/engine/gzdoom.ini", "/data/engine/last-play.log");
        var (program, args) = PlayCommand.For(new EngineInstall("GZDoom", "/usr/bin/gzdoom", "native"), request);
        Assert.Equal("/usr/bin/gzdoom", program);
        Assert.Equal(["-iwad", "/games/DOOM2.WAD", "-file", "/projects/My map", "+map", "MAP01", "-skill", "4", "-config", "/data/engine/gzdoom.ini", "+logfile", "/data/engine/last-play.log"], args);

        // The Flatpak sandbox is given the folders it needs.
        var (flatpak, sandboxed) = PlayCommand.For(new EngineInstall("GZDoom (Flatpak)", "/usr/bin/flatpak", "flatpak"), request);
        Assert.Equal("/usr/bin/flatpak", flatpak);
        string Folder(string path) => $"--filesystem={Path.GetDirectoryName(path)}";
        Assert.Equal(["run", Folder("/games/DOOM2.WAD"), Folder("/data/engine/gzdoom.ini"), "--filesystem=/projects/My map", PlayCommand.FlatpakApp], sandboxed.Take(5));
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
