using WadSmith.Core.Engines;

namespace WadSmith.Tests;

/// <summary>Play in a classic port (Phase 12): the ports found on the PATH per game, their commands, and WadSmith's own copy of their settings.</summary>
public sealed class PortPlayTests
{
    private static Func<string, string?> Env(string path, string home) => name => name switch
    {
        "PATH" => path,
        "HOME" => home,
        _ => null,
    };

    private static string Program(string folder, string name)
    {
        var file = Path.Combine(folder, name);
        File.WriteAllText(file, "#!/bin/sh\n");
        return file;
    }

    [Fact]
    public void ThePortsOnThePathAreFoundForTheirGamesOnly()
    {
        using var temp = new TempFolder();
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName;
        foreach (var p in new[] { "chocolate-doom", "crispy-doom", "crispy-heretic", "dsda-doom", "woof", "nugget-doom" }) Program(bin, p);
        var env = Env(bin, temp.Path);
        Assert.Equal(["Chocolate Doom", "Crispy Doom", "dsda-doom", "Woof!", "Nugget Doom"], PortLocator.Find("doom", env).Select(p => p.Name));
        Assert.Equal(["Crispy Heretic", "dsda-doom"], PortLocator.Find("heretic", env).Select(p => p.Name));
        Assert.Empty(PortLocator.Find("strife", env));

        // A vanilla project plays vanilla in Chocolate and in dsda-doom; a GZDoom project gets each port's best.
        var doom = PortLocator.Find("doom", env);
        Assert.Equal(["vanilla", "limit-removing", "mbf21", "mbf21", "mbf21"], doom.Select(p => p.ExportFor("gzdoom")));
        Assert.Equal(["vanilla", "vanilla", "vanilla", "vanilla", "vanilla"], doom.Select(p => p.ExportFor("vanilla")));
    }

    [Fact]
    public void EachPortIsToldTheMapTheDifficultyAndTheCompatibility()
    {
        var choco = new PortInstall("Chocolate Doom", "/usr/bin/chocolate-doom", "chocolate", ["vanilla"]);
        var dsda = new PortInstall("dsda-doom", "/usr/bin/dsda-doom", "dsda", ["mbf21", "boom", "limit-removing", "vanilla"]);
        var woof = new PortInstall("Woof!", "/usr/bin/woof", "woof", ["mbf21", "boom", "limit-removing", "vanilla"]);
        string Line((string Program, IReadOnlyList<string> Arguments) c) => c.Program + " " + string.Join(' ', c.Arguments);
        Assert.Equal("/usr/bin/chocolate-doom -iwad D2.WAD -file pack.wad -file out.wad -warp 7 -skill 4 -config a.cfg", Line(PortPlay.For(choco, "vanilla", "doom", "D2.WAD", ["pack.wad"], "out.wad", "MAP07", 4, ["-config", "a.cfg"])));
        Assert.Equal("/usr/bin/dsda-doom -iwad D.WAD -file out.wad -warp 2 3 -skill 3 -complevel 3", Line(PortPlay.For(dsda, "vanilla", "doom", "D.WAD", [], "out.wad", "E2M3", 3, [])));
        Assert.Equal("/usr/bin/dsda-doom -iwad D2.WAD -file out.wad -warp 1 -skill 3 -complevel 21", Line(PortPlay.For(dsda, "mbf21", "doom", "D2.WAD", [], "out.wad", "MAP01", 3, [])));
        Assert.Equal("/usr/bin/dsda-doom -iwad H.WAD -file out.wad -warp 1 1 -skill 3", Line(PortPlay.For(dsda, "vanilla", "heretic", "H.WAD", [], "out.wad", "E1M1", 3, [])));
        Assert.Equal("/usr/bin/woof -iwad D2.WAD -file out.wad -warp 12 -skill 5 -complevel boom", Line(PortPlay.For(woof, "boom", "doom", "D2.WAD", [], "out.wad", "MAP12", 9, [])));
        Assert.Throws<ArgumentException>(() => PortPlay.Warp("MYMAP"));
    }

    [Fact]
    public void ThePortsSettingsAreCopiedOnceAndTheUsersAreOnlyRead()
    {
        using var temp = new TempFolder();
        var home = Path.Combine(temp.Path, "home");
        var theirs = Path.Combine(home, ".local", "share", "crispy-doom");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "default.cfg"), "mouse_sensitivity 9\n");
        File.WriteAllText(Path.Combine(theirs, "crispy-doom.cfg"), "crispy_hires 1\n");
        var crispy = new PortInstall("Crispy Doom", "/usr/bin/crispy-doom", "crispy", ["limit-removing", "vanilla"]);
        var data = Path.Combine(temp.Path, "data");
        var args = PortPlay.Settings(crispy, data, Env("", home));
        Assert.Equal(["-config", Path.Combine(data, "engine", "crispy-doom", "default.cfg"), "-extraconfig", Path.Combine(data, "engine", "crispy-doom", "crispy-doom.cfg")], args);
        Assert.Equal("mouse_sensitivity 9\n", File.ReadAllText(args[1]));
        Assert.Equal("crispy_hires 1\n", File.ReadAllText(args[3]));

        // WadSmith's copy is its own from then on: the port changing it, or the user changing theirs, does not cross over.
        File.WriteAllText(args[1], "mouse_sensitivity 3\n");
        File.WriteAllText(Path.Combine(theirs, "default.cfg"), "mouse_sensitivity 12\n");
        PortPlay.Settings(crispy, data, Env("", home));
        Assert.Equal("mouse_sensitivity 3\n", File.ReadAllText(args[1]));
        Assert.Equal("mouse_sensitivity 12\n", File.ReadAllText(Path.Combine(theirs, "default.cfg")));

        // A port the user never ran starts from its own defaults: no file is made up for it.
        var dsda = new PortInstall("dsda-doom", "/usr/bin/dsda-doom", "dsda", ["mbf21"]);
        var dsdaArgs = PortPlay.Settings(dsda, data, Env("", home));
        Assert.False(File.Exists(dsdaArgs[1]));
    }
}
