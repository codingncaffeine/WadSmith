using WadSmith.Cli;

namespace WadSmith.Tests;

public sealed class CommandLineTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CommandLine.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void VersionNamesTheToolAndTheLicence()
    {
        var (code, output, _) = Run("--version");
        Assert.Equal(ExitCode.Ok, code);
        Assert.StartsWith("wadsmith-cli 0.", output, StringComparison.Ordinal);
        Assert.Contains("GPL version 3 or later", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NoArgumentsPrintsUsageAndFails()
    {
        var (code, output, _) = Run();
        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("Usage: wadsmith-cli", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerbIsAUsageError()
    {
        var (code, _, error) = Run("frobnicate");
        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("no command called \"frobnicate\"", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsTakeValuesOnlyWithAnEqualsSign()
    {
        var args = CommandArgs.Parse(["--sheet", "doom2.wad", "--out=dir", "--", "--literal"]);
        Assert.True(args.Flag("sheet"));
        Assert.Equal("dir", args.Option("out"));
        Assert.Equal(["doom2.wad", "--literal"], args.Positional);
        args.RejectUnread();
    }

    [Fact]
    public void AnOptionNobodyReadIsRejected()
    {
        var args = CommandArgs.Parse(["--colour=red"]);
        var ex = Assert.Throws<UsageException>(args.RejectUnread);
        Assert.Contains("--colour", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagGivenAValueIsAUsageError()
    {
        var args = CommandArgs.Parse(["--sheet=yes"]);
        Assert.Throws<UsageException>(() => args.Flag("sheet"));
    }

    [Fact]
    public void AScriptIsBuiltCheckedAndWritten()
    {
        using var temp = new TempFolder();
        var script = Path.Combine(temp.Path, "two.txt");
        File.WriteAllText(script, "room 0 0 256 256\nroom 256 64 384 192\ndoor 256 128\nstart 64 64\nexit 0 128\n");
        var wad = Path.Combine(temp.Path, "two.wad");
        var (code, output, _) = Run("script", script, "--out=" + wad);
        Assert.Equal(0, code);
        Assert.Contains("every area closed", output);
        Assert.Contains("problems: none", output);
        var read = Core.Maps.MapConversion.Read(Core.Maps.MapLocator.Find(Core.Containers.WadContainer.Open(wad)).Single());
        Assert.Equal(3, read.Sectors.Count);

        // A line that cannot be done fails the run, and says why.
        File.WriteAllText(script, "room 0 0 256 256\ndoor 900 900\n");
        (code, output, _) = Run("script", script, "--out=" + wad);
        Assert.NotEqual(0, code);
        Assert.Contains("FAIL", output);
        Assert.Contains("no wall near", output);
    }

    [Fact]
    public void RecognitionInDoomIIIsMeasuredAgainstTheGamesOwnMarks()
    {
        var doom2 = GameFiles.Require("DOOM2.WAD");
        var (code, output, error) = Run("recognise", doom2, "--engine-data=" + GameFiles.Require("gzdoom.pk3"));
        Assert.True(code == 0, error + output);
        Assert.Contains("doors           526 found,    526 marked: precision 100.0 %, recall 100.0 %", output);
        Assert.Contains("MAP32", output);
    }
}
