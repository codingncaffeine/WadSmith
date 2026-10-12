namespace WadSmith.Cli;

internal static class Program
{
    public static int Main(string[] args) => CommandLine.Run(args, Console.Out, Console.Error);
}
