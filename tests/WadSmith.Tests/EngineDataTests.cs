namespace WadSmith.Tests;

/// <summary>GZDoom's data file is found wherever GZDoom was installed, the Flatpak included.</summary>
public sealed class EngineDataTests
{
    private static string Planted(string folder)
    {
        Directory.CreateDirectory(folder);
        var pk3 = Path.Combine(folder, "gzdoom.pk3");
        File.WriteAllBytes(pk3, [0x50, 0x4B, 0x05, 0x06]);
        return pk3;
    }

    [Fact]
    public void TheFlatpakGZDoomsDataIsFoundInAUserInstallBeforeACopyBesideTheGames()
    {
        using var temp = new TempFolder();
        var flatpak = Planted(Path.Combine(temp.Path, "home", ".local", "share", "flatpak", "app", "org.zdoom.GZDoom", "current", "active", "files", "share", "games", "doom"));
        var beside = Planted(Path.Combine(temp.Path, "games"));
        string? Env(string name) => name == "HOME" ? Path.Combine(temp.Path, "home") : null;

        // No distribution install in sight (none listed): the Flatpak's copy comes before the one beside the games.
        Assert.Equal(flatpak, Core.Games.EngineData.Find([Path.Combine(temp.Path, "games")], Env, []));
        Assert.Equal(beside, Core.Games.EngineData.Find([Path.Combine(temp.Path, "games")], _ => null, []));
        Assert.Null(Core.Games.EngineData.Find([], _ => null, []));
    }

    [Fact]
    public void XdgDataHomeMovesTheUserFlatpakFolder()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var flatpak = Planted(Path.Combine(data, "flatpak", "app", "org.zdoom.GZDoom", "current", "active", "files", "share", "games", "doom"));
        string? Env(string name) => name switch { "HOME" => Path.Combine(temp.Path, "home"), "XDG_DATA_HOME" => data, _ => null };
        Assert.Equal(flatpak, Core.Games.EngineData.Find([], Env, []));
    }

    [Fact]
    public void TheUZDoomFlatpaksDataComesBeforeGZDooms()
    {
        using var temp = new TempFolder();
        var system = Path.Combine(temp.Path, "system");
        var gzdoom = Planted(Path.Combine(temp.Path, "home", ".local", "share", "flatpak", "app", "org.zdoom.GZDoom", "current", "active", "files", "share", "games", "doom"));
        var uzdoomFolder = Path.Combine(system, "app", "org.zdoom.UZDoom", "current", "active", "files", "share", "games", "uzdoom");
        Directory.CreateDirectory(uzdoomFolder);
        var uzdoom = Path.Combine(uzdoomFolder, "uzdoom.pk3");
        File.WriteAllBytes(uzdoom, [0x50, 0x4B, 0x05, 0x06]);
        string? Env(string name) => name switch { "HOME" => Path.Combine(temp.Path, "home"), "FLATPAK_SYSTEM_DIR" => system, _ => null };

        // UZDoom's (a system install, in share/games/uzdoom) before GZDoom's (a user install): UZDoom is the one that plays first.
        Assert.Equal(uzdoom, Core.Games.EngineData.Find([], Env, []));
        File.Delete(uzdoom);
        Assert.Equal(gzdoom, Core.Games.EngineData.Find([], Env, []));
    }
}
