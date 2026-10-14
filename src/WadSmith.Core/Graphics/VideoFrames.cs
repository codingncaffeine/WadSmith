using System.Diagnostics;
using System.Globalization;

namespace WadSmith.Core.Graphics;

/// <summary>
/// Frames from a video file through the user's own ffmpeg (Phase 7, §3.6): a few seconds at a few frames a second, made
/// small enough for a texture, as PNG files in a folder of their own that is removed afterwards. WadSmith ships no video
/// decoder: ffmpeg is the one every Linux distribution and Windows package manager has.
/// </summary>
public static class VideoFrames
{
    /// <summary>How many frames a second are kept, how many seconds from the start, and the longest side in pixels.</summary>
    public const int FramesASecond = 10;

    public const double Seconds = 10;

    public const int Longest = 320;

    /// <summary>Whether <paramref name="path"/> names a video file.</summary>
    public static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".webm" or ".mkv" or ".mov" or ".avi" or ".m4v";

    /// <summary>The ffmpeg program on the PATH, or null when there is none.</summary>
    public static string? Ffmpeg()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : ["ffmpeg"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The video's first <paramref name="seconds"/> at <paramref name="fps"/> frames a second, fitted within
    /// <paramref name="longest"/> pixels. Runs ffmpeg and waits for it: call it on a worker.
    /// </summary>
    public static IReadOnlyList<AnimationFrame> Read(string path, int fps = FramesASecond, double seconds = Seconds, int longest = Longest)
    {
        var ffmpeg = Ffmpeg() ?? throw new CorruptDataException($"{Path.GetFileName(path)} is a video: WadSmith takes its frames with ffmpeg, which was not found. Install ffmpeg (it is in every Linux distribution, and in winget and Chocolatey on Windows) and try again.");
        var folder = Path.Combine(Path.GetTempPath(), "wadsmith-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var c = CultureInfo.InvariantCulture;
            var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-t", seconds.ToString(c), "-i", path,
                "-vf", $"fps={fps},scale={longest}:{longest}:force_original_aspect_ratio=decrease", "-an", Path.Combine(folder, "f%04d.png"),
            })
            {
                start.ArgumentList.Add(a);
            }

            using var process = Process.Start(start) ?? throw new CorruptDataException("ffmpeg could not be started.");
            var said = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new CorruptDataException($"ffmpeg took over two minutes on {Path.GetFileName(path)} and was stopped.");
            }

            var files = Directory.GetFiles(folder, "f*.png").Order(StringComparer.Ordinal).ToList();
            if (files.Count == 0) throw new CorruptDataException($"ffmpeg found no pictures in {Path.GetFileName(path)}. {said.Result.Trim()}");
            var each = (int)Math.Round(1000.0 / fps);
            return [.. files.Select(f => new AnimationFrame(ImageCodec.Decode(File.ReadAllBytes(f), Path.GetFileName(f)), each))];
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
