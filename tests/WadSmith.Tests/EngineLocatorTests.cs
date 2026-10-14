using WadSmith.Core.Engines;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

/// <summary>UZDoom and GZDoom are found however they were installed (D64), and each engine's own data file is read; no engine is started.</summary>
public sealed class EngineLocatorTests
{
    /// <summary>A file that begins as an AppImage of type 2 does (an ELF program marked "AI" 2), startable or not yet.</summary>
    internal static void PlantAppImage(string path, bool startable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, (byte)'A', (byte)'I', 2, 0, 0, 0, 0, 0]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | (startable ? UnixFileMode.UserExecute : 0));
    }

    private static string Planted(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name);
        File.WriteAllBytes(file, [0x50, 0x4B, 0x05, 0x06]);
        return file;
    }

    [Fact]
    public void TheUZDoomFlatpakComesBeforeGZDoomsAndIsChosenByItsExportedCommand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Flatpaks are Linux's.");
        using var temp = new TempFolder();
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "flatpak"), "");
        var system = Path.Combine(temp.Path, "system-flatpak");
        var user = Path.Combine(temp.Path, "home/.local/share/flatpak");
        void Install(string root, string app)
        {
            Directory.CreateDirectory(Path.Combine(root, "app", app, "current", "active", "files"));
            Directory.CreateDirectory(Path.Combine(root, "exports", "bin"));
            File.WriteAllText(Path.Combine(root, "exports", "bin", app), "#!/bin/sh\n");
        }

        // GZDoom installed for the system (as Flathub's end of life left it), UZDoom for the user.
        Install(system, EngineLocator.GZDoomApp);
        Install(user, EngineLocator.UZDoomApp);
        string? Env(string name) => name switch { "PATH" => bin, "HOME" => Path.Combine(temp.Path, "home"), "FLATPAK_SYSTEM_DIR" => system, _ => null };

        var found = EngineLocator.Find(null, Env).Where(e => e.Kind == "flatpak").ToList();
        Assert.Equal(
            [("UZDoom (Flatpak)", EngineLocator.UZDoomApp, Path.Combine(user, "exports/bin", EngineLocator.UZDoomApp)), ("GZDoom (Flatpak)", EngineLocator.GZDoomApp, Path.Combine(system, "exports/bin", EngineLocator.GZDoomApp))],
            found.Select(e => (e.Name, e.App, e.Choice)));
        Assert.All(found, e => Assert.Equal(Path.Combine(bin, "flatpak"), e.Program));

        // Chosen by its exported command (what Preferences keeps), it comes first and is still that Flatpak.
        var chosen = EngineLocator.Find(Path.Combine(system, "exports/bin", EngineLocator.GZDoomApp), Env)[0];
        Assert.Equal(("GZDoom (Flatpak)", "flatpak", EngineLocator.GZDoomApp), (chosen.Name, chosen.Kind, chosen.App));

        // Its data file is the Flatpak's own: UZDoom's lies in share/games/uzdoom.
        var pk3 = Planted(Path.Combine(user, "app", EngineLocator.UZDoomApp, "current/active/files/share/games/uzdoom"), "uzdoom.pk3");
        Assert.Equal(pk3, EngineData.For(found, [], Env, null));
        Assert.Equal(pk3, EngineData.Find([], Env, []));
    }

    [Fact]
    public void AppImagesThatCanStartAreFoundWhereTheyAreKeptNewestFirst()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "AppImages are Linux's.");
        using var temp = new TempFolder();
        var home = Path.Combine(temp.Path, "home");
        var older = Path.Combine(home, "Applications", "Linux-UZDoom-4.14.3.AppImage");
        var newer = Path.Combine(home, "Téléchargements", "Linux-UZDoom-Release-x86_64.AppImage");
        var unstarted = Path.Combine(home, "Downloads", "uzdoom-5.0.2.AppImage");
        PlantAppImage(older, startable: true);
        PlantAppImage(newer, startable: true);
        PlantAppImage(unstarted, startable: false);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-30));

        // The downloads folder in another language, as the user's folder settings name it.
        Directory.CreateDirectory(Path.Combine(home, ".config"));
        File.WriteAllText(Path.Combine(home, ".config/user-dirs.dirs"), "# written by xdg-user-dirs-update\nXDG_DOWNLOAD_DIR=\"$HOME/Téléchargements\"\n");
        string? Env(string name) => name switch { "HOME" => home, "PATH" => "", "FLATPAK_SYSTEM_DIR" => Path.Combine(temp.Path, "none"), _ => null };

        Assert.Equal([("UZDoom (AppImage)", newer), ("UZDoom (AppImage)", older)], EngineLocator.Find(null, Env).Where(e => e.Kind == "appimage").Select(e => (e.Name, e.Program)));

        // One never started is not found, but chosen it is what it is.
        var chosen = EngineLocator.Find(unstarted, Env)[0];
        Assert.Equal(("UZDoom (AppImage)", "appimage", unstarted, "uzdoom"), (chosen.Name, chosen.Kind, chosen.Program, chosen.Family));
    }

    [Fact]
    public void AFileChosenIsNamedAfterTheEngineItsNameSays()
    {
        using var temp = new TempFolder();
        string? Env(string name) => name switch { "HOME" => temp.Path, "PATH" => "", "FLATPAK_SYSTEM_DIR" => Path.Combine(temp.Path, "none"), _ => null };
        var gzdoom = Path.Combine(temp.Path, "builds", "gzdoom-g4.14.2");
        var other = Path.Combine(temp.Path, "builds", "doom-engine");
        Directory.CreateDirectory(Path.Combine(temp.Path, "builds"));
        File.WriteAllText(gzdoom, "");
        File.WriteAllText(other, "");
        Assert.Equal(("GZDoom", "custom"), (EngineLocator.Describe(gzdoom, Env)!.Name, EngineLocator.Describe(gzdoom, Env)!.Kind));
        Assert.Equal(("Your engine", "gzdoom"), (EngineLocator.Describe(other, Env)!.Name, EngineLocator.Describe(other, Env)!.Family));
        Assert.Null(EngineLocator.Describe(Path.Combine(temp.Path, "builds", "gone"), Env));
    }

    [Fact]
    public void AnEnginesOwnDataFileIsReadFirstBesideItOrInItsPackagesFolderThroughALinkToo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A link needs privileges on Windows.");
        using var temp = new TempFolder();

        // A /usr of its own: bin/uzdoom a link to a launcher elsewhere (as the AUR's uzdoom-bin installs it), the data in
        // share/games/uzdoom; GZDoom's in share/gzdoom (Arch's package).
        var usr = Path.Combine(temp.Path, "usr");
        var launcher = Path.Combine(temp.Path, "cache/uzdoom-bin/uzdoom");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(launcher, "#!/bin/bash\n");
        Directory.CreateDirectory(Path.Combine(usr, "bin"));
        File.CreateSymbolicLink(Path.Combine(usr, "bin/uzdoom"), launcher);
        File.WriteAllText(Path.Combine(usr, "bin/gzdoom"), "");
        var uzdoomData = Planted(Path.Combine(usr, "share/games/uzdoom"), "uzdoom.pk3");
        var gzdoomData = Planted(Path.Combine(usr, "share/gzdoom"), "gzdoom.pk3");
        var uz = new EngineInstall("UZDoom", Path.Combine(usr, "bin/uzdoom"), "native");
        var gz = new EngineInstall("GZDoom", Path.Combine(usr, "bin/gzdoom"), "native");

        // The engine that plays reads its own data: UZDoom's when it is first, GZDoom's when that one was chosen.
        Assert.Equal(uzdoomData, EngineData.For([uz, gz], [], _ => null, null));
        Assert.Equal(gzdoomData, EngineData.For([gz, uz], [], _ => null, null));

        // A portable build keeps it beside itself.
        var portable = Path.Combine(temp.Path, "Games/uzdoom-build");
        var beside = Planted(portable, "uzdoom.pk3");
        File.WriteAllText(Path.Combine(portable, "uzdoom"), "");
        Assert.Equal(beside, EngineData.For([new EngineInstall("UZDoom", Path.Combine(portable, "uzdoom"), "custom"), uz], [], _ => null, null));
    }
}
