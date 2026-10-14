using WadSmith.Core.Engines;
using WadSmith.Core.Games;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>
/// WadSmith on Windows: its own files where Windows keeps an app's, and the games found where Windows players keep them:
/// beside a GZDoom unpacked anywhere, in the folders its settings name, and in the Steam libraries.
/// </summary>
public sealed class WindowsFoldersTests
{
    private const string WindowsOnly = "Windows' own file names and variables.";

    [Fact]
    public void OnWindowsSettingsRoamAndTheRestStaysOnThisComputer()
    {
        using var temp = new TempFolder();
        var (roaming, local) = (Path.Combine(temp.Path, "Roaming"), Path.Combine(temp.Path, "Local"));
        string? Env(string name) => name switch { "APPDATA" => roaming, "LOCALAPPDATA" => local, "XDG_DATA_HOME" => Path.Combine(temp.Path, "xdg"), _ => null };
        var paths = AppPaths.Resolve(null, Env, windows: true);
        Assert.Equal(Path.Combine(roaming, "WadSmith"), paths.Config);
        Assert.Equal(Path.Combine(local, "WadSmith", "Data"), paths.Data);
        Assert.Equal(Path.Combine(local, "WadSmith", "State"), paths.State);
        Assert.Equal(Path.Combine(local, "WadSmith", "Cache"), paths.Cache);
        Assert.Equal("WadSmith", Path.GetFileName(paths.Projects));

        // Elsewhere the XDG folders are kept.
        Assert.Equal(Path.Combine(temp.Path, "xdg", "wadsmith"), AppPaths.Resolve(null, Env, windows: false).Data);
    }

    [Fact]
    public void AGZDoomInProgramFilesIsFoundWithItsDataFileItsSettingsAndTheGamesBesideIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var temp = new TempFolder();
        var programs = Path.Combine(temp.Path, "Program Files");
        var gzdoom = Directory.CreateDirectory(Path.Combine(programs, "GZDoom")).FullName;
        var exe = Path.Combine(gzdoom, "gzdoom.exe");
        File.WriteAllText(exe, "");
        var pk3 = Path.Combine(gzdoom, "gzdoom.pk3");
        File.WriteAllBytes(pk3, [0x50, 0x4B, 0x05, 0x06]);
        var iwads = Directory.CreateDirectory(Path.Combine(gzdoom, "iwads")).FullName;
        var ini = Path.Combine(gzdoom, "gzdoom_portable.ini");
        File.WriteAllText(ini, "[IWADSearch.Directories]\nPath=.\nPath=$HOME\nPath=$PROGDIR/iwads\n");
        string? Env(string name) => name == "ProgramFiles" ? programs : null;

        Assert.Contains(EngineLocator.Find(null, Env), e => e.Name == "GZDoom" && e.Program == exe);
        Assert.Equal(pk3, EngineData.Find([], Env, []));

        // The engine's folder, and the one its settings name with $PROGDIR; $HOME is not set on Windows and names nothing.
        var standard = GameLocator.StandardFolders(Env);
        Assert.Contains(gzdoom, standard);
        Assert.Contains(iwads, standard);

        // WadSmith plays with a copy of those settings, and the engine's own file is left as it was.
        var mine = EngineConfig.Prepare(Path.Combine(temp.Path, "data"), Env);
        Assert.Equal(File.ReadAllText(ini), File.ReadAllText(mine));
    }

    [Fact]
    public void TheEngineTheUserChoseIsLookedBesideAndAFolderIsLookedInOnceWhateverItsCase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var temp = new TempFolder();
        var anywhere = Directory.CreateDirectory(Path.Combine(temp.Path, "Doom stuff", "gzdoom-4-14-2")).FullName;
        var exe = Path.Combine(anywhere, "gzdoom.exe");
        File.WriteAllText(exe, "");
        string? Env(string name) => name == "DOOMWADDIR" ? anywhere.ToUpperInvariant() : null;
        Assert.Single(GameLocator.StandardFolders(Env, exe), f => string.Equals(f, anywhere, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EverySteamLibraryAndEveryDoomWadPathFolderIsLookedIn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var temp = new TempFolder();
        var programs = Path.Combine(temp.Path, "Program Files (x86)");
        var steam = Directory.CreateDirectory(Path.Combine(programs, "Steam", "steamapps", "common")).Parent!.Parent!.FullName;
        var second = Directory.CreateDirectory(Path.Combine(temp.Path, "SteamLibrary", "steamapps", "common")).FullName;
        string Escaped(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0" { "path"		"{{Escaped(steam)}}" }
            	"1" { "path"		"{{Escaped(Path.Combine(temp.Path, "SteamLibrary"))}}" }
            }
            """);
        var (one, two) = (Directory.CreateDirectory(Path.Combine(temp.Path, "wads1")).FullName, Directory.CreateDirectory(Path.Combine(temp.Path, "wads2")).FullName);
        string? Env(string name) => name switch { "ProgramFiles(x86)" => programs, "DOOMWADPATH" => $"{one};{two}", _ => null };

        var targets = GameLocator.StandardTargets(Env);
        Assert.Contains(targets, t => t.Folder == Path.Combine(steam, "steamapps", "common") && !t.ReadArchives);
        Assert.Contains(targets, t => t.Folder == second && !t.ReadArchives);
        Assert.Contains(targets, t => t.Folder == one);
        Assert.Contains(targets, t => t.Folder == two);
    }

    [Fact]
    public void UZDoomIsFoundWhereWingetScoopOrAnUnpackedZipPutItBeforeGZDoom()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var temp = new TempFolder();
        var local = Path.Combine(temp.Path, "Local");
        var profile = Path.Combine(temp.Path, "Profile");
        string Exe(string folder, string name)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "");
            return path;
        }

        // winget's portable install, Scoop's (moved by its variable), Explorer's "Extract all" in Downloads, and Chocolatey's GZDoom.
        var winget = Exe(Path.Combine(local, "Microsoft", "WinGet", "Packages", "UZDoom.UZDoom_Microsoft.Winget.Source_8wekyb3d8bbwe"), "uzdoom.exe");
        var pk3 = Path.Combine(Path.GetDirectoryName(winget)!, "uzdoom.pk3");
        File.WriteAllBytes(pk3, [0x50, 0x4B, 0x05, 0x06]);
        var scoop = Exe(Path.Combine(temp.Path, "Scoop", "apps", "uzdoom", "current"), "uzdoom.exe");
        var unzipped = Exe(Path.Combine(profile, "Downloads", "Windows-UZDoom-Release-x86_64"), "uzdoom.exe");
        var chocolatey = Exe(Path.Combine(local, "Programs", "gzdoom"), "gzdoom.exe");
        string? Env(string name) => name switch { "LOCALAPPDATA" => local, "USERPROFILE" => profile, "SCOOP" => Path.Combine(temp.Path, "Scoop"), _ => null };

        var found = EngineLocator.Find(null, Env);
        // Windows names a folder in any case: Chocolatey's "gzdoom" folder is found as Programs\GZDoom.
        Assert.Equal([winget, scoop, unzipped, chocolatey], found.Select(e => e.Program), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["UZDoom", "UZDoom", "UZDoom", "GZDoom"], found.Select(e => e.Name));

        // The data file beside the engine that plays first, and WadSmith's settings for it are UZDoom's own.
        Assert.Equal(pk3, EngineData.For(found, [], Env, null));
        Assert.EndsWith("uzdoom.ini", EngineConfig.Prepare(Path.Combine(temp.Path, "data"), Env, found[0]), StringComparison.Ordinal);
    }
}
