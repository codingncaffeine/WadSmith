using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace WadSmith.Editor.App;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// The application log: one file per run under the state folder (the previous four kept), echoed
/// to the terminal. Logging never throws and never waits: a line is queued and the log's own thread
/// writes it, so the UI thread pays no disk time for it.
/// </summary>
public static class Log
{
    private const int Keep = 5;
    private static readonly BlockingCollection<string> Pending = new(boundedCapacity: 10_000);
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;
    private static Thread? _writer;

    public static string? FilePath { get; private set; }

    public static LogLevel Minimum { get; set; } = LogLevel.Info;

    /// <summary>Opens <c>wadsmith.log</c> in <paramref name="directory"/>; without it, lines go to the terminal only.</summary>
    public static void Initialize(string stamp, string directory)
    {
        lock (Gate)
        {
            if (_writer is not null) return;
            try
            {
                Directory.CreateDirectory(directory);
                for (var i = Keep - 1; i >= 1; i--)
                {
                    var from = Path.Combine(directory, i == 1 ? "wadsmith.log" : $"wadsmith.{i - 1}.log");
                    if (File.Exists(from)) File.Move(from, Path.Combine(directory, $"wadsmith.{i}.log"), overwrite: true);
                }

                FilePath = Path.Combine(directory, "wadsmith.log");
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = false };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not open the log file: {ex.Message}");
            }

            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "WadSmith log" };
            _writer.Start();
        }

        Info($"WadSmith {stamp}");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, ex is null ? message : $"{message}\n{ex}");

    /// <summary>Logs an unexpected failure with its whole chain and returns the line, for a crash report.</summary>
    public static string Crash(string where, Exception ex)
    {
        var line = $"Crash in {where}: {ex}";
        Write(LogLevel.Error, line);
        Flush(TimeSpan.FromSeconds(2));
        return line;
    }

    public static void Write(LogLevel level, string message)
    {
        if (level < Minimum) return;
        var tag = level switch { LogLevel.Debug => "DBG", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "INF" };
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} +{Uptime.Elapsed.TotalSeconds,8:0.000} {tag} {message}");
        if (!Pending.TryAdd(line)) return;
        if (_writer is null) Console.Error.WriteLine(line);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for queued lines to reach the file.</summary>
    public static void Flush(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (Pending.Count > 0 && deadline.Elapsed < timeout) Thread.Sleep(10);
        lock (Gate) _file?.Flush();
    }

    private static void WriteLoop()
    {
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            Console.Error.WriteLine(line);
            lock (Gate)
            {
                _file?.WriteLine(line);
                if (Pending.Count == 0) _file?.Flush();
            }
        }
    }
}
