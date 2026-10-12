using WadSmith.Core;

namespace WadSmith.Cli;

/// <summary>Exit codes every command shares.</summary>
public static class ExitCode
{
    /// <summary>The command did what it was asked and found nothing wrong.</summary>
    public const int Ok = 0;

    /// <summary>The command ran and found a problem (a failed check, a difference, a bad file).</summary>
    public const int Failed = 1;

    /// <summary>The command line itself was wrong; nothing was done.</summary>
    public const int Usage = 2;
}

/// <summary>One verb of <c>wadsmith-cli</c>.</summary>
internal interface ICommand
{
    string Name { get; }

    /// <summary>One line for the command list.</summary>
    string Summary { get; }

    /// <summary>The usage text shown by <c>wadsmith-cli help VERB</c> and after a usage error.</summary>
    string Usage { get; }

    int Run(CommandArgs args, TextWriter output, TextWriter error);
}

/// <summary>The verb dispatcher. Kept free of <see cref="Console"/> so the tests can run it in-process.</summary>
public static class CommandLine
{
    internal static IReadOnlyList<ICommand> Commands { get; } =
    [
        new Commands.InspectCommand(),
        new Commands.RoundTripCommand(),
        new Commands.ExtractCommand(),
        new Commands.TranslateCommand(),
        new Commands.Render2dCommand(),
        new Commands.ValidateCommand(),
        new Commands.RemixCommand(),
        new Commands.StressGenCommand(),
        new Commands.ScriptCommand(),
        new Commands.RecogniseCommand(),
        new Commands.LandmarksCommand(),
        new Commands.PlayCliCommand(),
        new Commands.MapSettingsCommand(),
    ];

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help" && args.Count == 1)
        {
            PrintHelp(output);
            return args.Count == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        if (args[0] is "--version" or "-V")
        {
            output.WriteLine($"wadsmith-cli {BuildInfo.Stamp}");
            output.WriteLine(BuildInfo.Licence);
            return ExitCode.Ok;
        }

        if (args[0] == "help")
        {
            var wanted = Find(args[1]);
            if (wanted is null)
            {
                error.WriteLine($"There is no command called \"{args[1]}\". Run wadsmith-cli --help for the list.");
                return ExitCode.Usage;
            }

            output.WriteLine(wanted.Usage);
            return ExitCode.Ok;
        }

        var command = Find(args[0]);
        if (command is null)
        {
            error.WriteLine($"There is no command called \"{args[0]}\". Run wadsmith-cli --help for the list.");
            return ExitCode.Usage;
        }

        CommandArgs parsed;
        try
        {
            parsed = CommandArgs.Parse(args.Skip(1).ToArray());
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(command.Usage);
            return ExitCode.Usage;
        }

        try
        {
            return command.Run(parsed, output, error);
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(command.Usage);
            return ExitCode.Usage;
        }
    }

    private static ICommand? Find(string name) => Commands.FirstOrDefault(c => c.Name == name);

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("Usage: wadsmith-cli COMMAND [arguments]");
        output.WriteLine();
        if (Commands.Count > 0)
        {
            output.WriteLine("Commands:");
            var width = Commands.Max(c => c.Name.Length);
            foreach (var c in Commands) output.WriteLine($"  {c.Name.PadRight(width)}  {c.Summary}");
            output.WriteLine();
        }

        output.WriteLine("  --version   Print the version and licence, then exit.");
        output.WriteLine("  help VERB   Show how to use one command.");
    }
}

/// <summary>A command line that cannot be acted on. The message says what to change.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Positional arguments plus <c>--name=value</c> and <c>--flag</c> options.</summary>
public sealed class CommandArgs
{
    private readonly Dictionary<string, string?> _options;
    private readonly HashSet<string> _read = [];

    private CommandArgs(IReadOnlyList<string> positional, Dictionary<string, string?> options)
    {
        Positional = positional;
        _options = options;
    }

    public IReadOnlyList<string> Positional { get; }

    /// <summary>Options the command never asked about, so a typo is reported instead of ignored.</summary>
    public IEnumerable<string> Unread => _options.Keys.Where(k => !_read.Contains(k));

    /// <summary>
    /// Values are always written <c>--name=value</c>, so a flag followed by a file name is never
    /// mistaken for an option with a value. <c>--</c> ends the options.
    /// </summary>
    public static CommandArgs Parse(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var onlyPositional = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (onlyPositional || !a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2)
            {
                if (a == "--" && !onlyPositional)
                {
                    onlyPositional = true;
                    continue;
                }

                positional.Add(a);
                continue;
            }

            var body = a[2..];
            var eq = body.IndexOf('=', StringComparison.Ordinal);
            string name;
            string? value;
            if (eq >= 0)
            {
                name = body[..eq];
                value = body[(eq + 1)..];
            }
            else
            {
                name = body;
                value = null;
            }

            if (!options.TryAdd(name, value)) throw new UsageException($"--{name} is given twice.");
        }

        return new CommandArgs(positional, options);
    }

    public bool Flag(string name)
    {
        _read.Add(name);
        if (!_options.TryGetValue(name, out var value)) return false;
        if (value is not null) throw new UsageException($"--{name} takes no value.");
        return true;
    }

    public string? Option(string name)
    {
        _read.Add(name);
        if (!_options.TryGetValue(name, out var value)) return null;
        return value ?? throw new UsageException($"--{name} needs a value: --{name}=VALUE.");
    }

    /// <summary>Fails the command line if an option was given that no code read.</summary>
    public void RejectUnread()
    {
        var unknown = Unread.ToList();
        if (unknown.Count > 0) throw new UsageException($"Unknown option{(unknown.Count > 1 ? "s" : "")}: {string.Join(", ", unknown.Select(u => "--" + u))}.");
    }
}
