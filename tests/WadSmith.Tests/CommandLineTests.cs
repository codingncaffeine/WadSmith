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
}
