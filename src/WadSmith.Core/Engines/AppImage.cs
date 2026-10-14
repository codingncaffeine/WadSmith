using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WadSmith.Core.Engines;

/// <summary>
/// An AppImage: one file holding a program and its files, as UZDoom is downloaded for Linux. The engine's data file inside
/// it is taken out by the AppImage's own runtime with <c>--appimage-extract</c>, which unpacks the files named and exits
/// without starting the program inside (measured 2026-10-14 on UZDoom 5.0.3: 4 ms, the bytes the package installs). A file
/// without the AppImage type 2 mark is never run; the runtime is given no screen and no session to reach, and is stopped
/// after the time allowed. What it took out is kept in the cache until the AppImage changes.
/// </summary>
public static class AppImage
{
    /// <summary>Where an engine's data file lies inside its AppImage, as the runtime matches names (a * stays within one folder).</summary>
    private static readonly string[] Patterns = ["usr/share/games/*/?zdoom.pk3", "usr/share/?zdoom/?zdoom.pk3", "usr/bin/?zdoom.pk3"];

    /// <summary>Whether the file begins as an AppImage of type 2 does: an ELF program marked "AI" 2 in its header's padding.</summary>
    public static bool IsAppImage(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[11];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F' && head[8] == (byte)'A' && head[9] == (byte)'I' && head[10] == 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The engine data file inside <paramref name="appImage"/> (<paramref name="family"/>'s, "uzdoom" or "gzdoom", before the
    /// other's), taken out into <paramref name="cacheFolder"/> the first time and kept there; null when the file is no
    /// AppImage, holds no data file, or the runtime failed. <paramref name="run"/> starts the runtime and says whether it
    /// finished well within the time given (a test passes a stand-in). Takes a moment the first time: call it off the UI thread.
    /// </summary>
    public static string? DataFile(string appImage, string family, string cacheFolder, TimeSpan timeout, Func<ProcessStartInfo, TimeSpan, bool>? run = null)
    {
        if (!IsAppImage(appImage)) return null;
        var info = new FileInfo(appImage);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName)))[..16].ToLowerInvariant();
        var folder = Path.Combine(cacheFolder, key);
        var stamp = Path.Combine(folder, "source.txt");
        var source = $"{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n";
        string[] names = family == "uzdoom" ? ["uzdoom.pk3", "gzdoom.pk3"] : ["gzdoom.pk3", "uzdoom.pk3"];
        try
        {
            if (File.Exists(stamp) && File.ReadAllText(stamp) == source && names.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists) is { } kept) return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var work = Path.Combine(cacheFolder, $"{key}.part{Environment.ProcessId}");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);
            foreach (var pattern in Patterns)
            {
                var start = new ProcessStartInfo(info.FullName) { UseShellExecute = false, WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--appimage-extract");
                start.ArgumentList.Add(pattern);

                // Nothing to open a window on or to talk to; and not the variables that would make the runtime run the program
                // after all, or unpack another file.
                foreach (var variable in new[] { "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "APPIMAGE_EXTRACT_AND_RUN", "TARGET_APPIMAGE" }) start.Environment.Remove(variable);
                start.Environment["TMPDIR"] = work;
                if (!(run ?? Run)(start, timeout)) continue;
                var root = Path.Combine(work, "squashfs-root");
                if (!Directory.Exists(root)) continue;
                var found = names.Select(n => Directory.EnumerateFiles(root, n, SearchOption.AllDirectories).FirstOrDefault()).OfType<string>().FirstOrDefault();
                if (found is null) continue;
                Directory.CreateDirectory(folder);
                foreach (var n in names) File.Delete(Path.Combine(folder, n));
                var data = Path.Combine(folder, Path.GetFileName(found));
                File.Move(found, data);
                File.WriteAllText(stamp, source);
                return data;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Starts the runtime and waits for it; one still running at the end of <paramref name="timeout"/> is stopped.</summary>
    private static bool Run(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start);
        if (process is null) return false;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.WaitForExit(timeout))
        {
            process.WaitForExit();
            return process.ExitCode == 0;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }
}
