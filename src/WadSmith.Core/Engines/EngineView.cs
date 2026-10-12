using System.Diagnostics;
using System.Globalization;

namespace WadSmith.Core.Engines;

/// <summary>Why the game's view could not be taken, in words for the user.</summary>
public sealed class EngineViewException(string message) : Exception(message);

/// <summary>
/// How the engine draws the view on the private screen. Measured 2026-10-12 under Xvfb: OpenGL there reads back a black
/// picture (Mesa's software GL on a bare X server), Vulkan draws on the real GPU and reads back its own image, and the
/// classic software renderer draws on the CPU; so Vulkan first, the software renderer when a picture comes back black.
/// </summary>
public enum EngineRenderer
{
    Vulkan,
    Software,
}

/// <summary>Where the game's view is taken from: the player's feet (or a held height, as from the air), facing an angle, looking up or down by a pitch.</summary>
public readonly record struct ViewSpot(double X, double Y, double Angle, double? Z = null, double Pitch = 0)
{
    /// <summary>The spot as the add-on reads it: "x y angle" or "x y angle z pitch". A leading space keeps the engine from taking a negative x for an option.</summary>
    public string Text => Z is { } z
        ? string.Create(CultureInfo.InvariantCulture, $" {X:0.##} {Y:0.##} {Angle:0.##} {z:0.##} {Pitch:0.##}")
        : string.Create(CultureInfo.InvariantCulture, $" {X:0.##} {Y:0.##} {Angle:0.##}");
}

/// <summary>
/// "Show me the game's view" (D41): the engine's own picture from a spot. GZDoom starts on a private virtual screen
/// (Xvfb) with a copy of WadSmith's own settings and a private home, a small add-on stands the player at the spot, one
/// screenshot is taken, and the engine is stopped; nothing appears on the desktop and nothing of the user's changes.
/// Without Xvfb the view is refused with the package to install.
/// </summary>
public static class EngineView
{
    public const string Setting = "wsview";

    private const string Script = """
        version "4.6"

        // Written by WadSmith for the game's view: stands the player at the spot in wsview ("x y angle", or "x y angle z
        // pitch" to hold the view at a height) and keeps them there, so a screenshot a moment later shows the game's own
        // picture from there.
        class WadSmithView : StaticEventHandler
        {
        	override void WorldTick()
        	{
        		Array<String> parts;
        		CVar.FindCVar("wsview").GetString().Split(parts, " ", TOK_SKIPEMPTY);
        		if (parts.Size() < 3) return;
        		let mo = players[consoleplayer].mo;
        		if (!mo || level.maptime < 2 || level.maptime >= 700) return;
        		Vector2 at = (parts[0].ToDouble(), parts[1].ToDouble());
        		double z = parts.Size() >= 4 ? parts[3].ToDouble() : level.PointInSector(at).floorplane.ZatPoint(at);
        		mo.bNoGravity = parts.Size() >= 4;
        		mo.SetOrigin((at, z), false);
        		mo.vel = (0, 0, 0);
        		mo.angle = parts[2].ToDouble();
        		mo.pitch = parts.Size() >= 5 ? parts[4].ToDouble() : 0;
        		if (level.maptime == 2) Console.Printf("WADSMITH VIEW %.0f %.0f", mo.pos.x, mo.pos.y);
        	}
        }
        """;

    /// <summary>Writes the add-on (the same bytes every time; a file already holding them is left alone) and returns its path.</summary>
    public static string WriteAddOn(string dataFolder)
    {
        var folder = Path.Combine(dataFolder, "engine");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "wadsmith-view.pk3");
        var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = new StreamWriter(entry.Open());
                w.Write(text.Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            Add("zscript.txt", Script);
            Add("cvarinfo.txt", "server noarchive string wsview = \"\";\n");
            Add("mapinfo.txt", "GameInfo\n{\n\tAddEventHandlers = \"WadSmithView\"\n}\n");
        }

        var data = bytes.ToArray();
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return path;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>What to install when no private screen can be made.</summary>
    public const string NeedsScreen = "To see the game's own view, install Xvfb (the package xorg-server-xvfb on Arch, xvfb on Debian and Ubuntu): it gives the game a private screen, so nothing opens on your desktop.";

    /// <summary>The program that makes a private screen (Xvfb on PATH), or null.</summary>
    public static string? FindScreen(Func<string, string?> env)
    {
        if (OperatingSystem.IsWindows()) return null;
        var path = env("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, "Xvfb")).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Takes the game's picture from <paramref name="spot"/>, <paramref name="width"/> by <paramref name="height"/>, and
    /// returns the PNG's path (under the data folder; the next view replaces it). <paramref name="screenProgram"/> is
    /// Xvfb, or null to start the engine on the screen already set (tests with a stand-in engine). Takes a few seconds:
    /// call it on a worker.
    /// </summary>
    public static async Task<string> CaptureAsync(EngineInstall engine, PlayRequest request, ViewSpot spot, int width, int height, string dataFolder, string? screenProgram, TimeSpan timeout, CancellationToken cancel, EngineRenderer renderer = EngineRenderer.Vulkan)
    {
        var work = Path.Combine(dataFolder, "engine", "view");
        var shots = Path.Combine(work, "shots");
        var home = Path.Combine(work, "home");
        var run = Path.Combine(work, "run");
        foreach (var d in new[] { work, shots, home, run }) Directory.CreateDirectory(d);
        foreach (var old in Directory.GetFiles(shots)) File.Delete(old);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Its own copy of WadSmith's settings each time, so what the engine saves at exit (its window, its screen) never reaches Play's.
        var config = Path.Combine(work, "view.ini");
        if (File.Exists(request.Config)) File.Copy(request.Config, config, overwrite: true);
        var log = Path.Combine(work, "engine-view.log");
        if (File.Exists(log)) File.Delete(log);
        var shot = Path.Combine(work, "shot.cfg");
        // Ten seconds of game time before the screenshot: with software drawing the map's first frame can take several (a
        // shot at four seconds came out black, 2026-10-12).
        await File.WriteAllTextAsync(shot, "wait 350; screenshot\n", cancel).ConfigureAwait(false);
        var addOn = WriteAddOn(dataFolder);

        var (program, arguments) = PlayCommand.For(engine, request with { Config = config, Log = log, From = null, FromAddOn = null });
        var args = new List<string>(arguments);
        args.AddRange(["-file", addOn, "+set", Setting, spot.Text, "+set", "screenshot_dir", shots, "+set", "screenshot_type", "png", "+set", "crosshair", "0", "+set", "screenblocks", "12", "+exec", shot]);
        args.AddRange(["-width", width.ToString(CultureInfo.InvariantCulture), "-height", height.ToString(CultureInfo.InvariantCulture), "-nosound", "-nomusic", "+vid_fullscreen", "0", "+queryiwad", "0"]);
        args.AddRange(renderer == EngineRenderer.Vulkan ? ["+vid_preferbackend", "1"] : ["+vid_preferbackend", "0", "+vid_rendermode", "0"]);

        Process? screen = null;
        var display = "";
        try
        {
            if (screenProgram is not null)
            {
                (screen, display) = StartScreen(screenProgram, width, height);
                await WaitForScreenAsync(display, screen, cancel).ConfigureAwait(false);
            }

            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
            foreach (var a in args) start.ArgumentList.Add(a);
            if (screen is not null)
            {
                start.Environment["DISPLAY"] = display;
                start.Environment["SDL_VIDEODRIVER"] = "x11";
                start.Environment.Remove("WAYLAND_DISPLAY");
            }

            start.Environment["HOME"] = home;
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
            start.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
            start.Environment["XDG_RUNTIME_DIR"] = run;
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "disabled:";
            start.Environment["SDL_VIDEO_ALLOW_SCREENSAVER"] = "1";
            using var process = Process.Start(start) ?? throw new EngineViewException($"{engine.Name} did not start.");
            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                var until = DateTime.UtcNow + timeout;
                string? png = null;
                while (DateTime.UtcNow < until)
                {
                    cancel.ThrowIfCancellationRequested();
                    png = Directory.GetFiles(shots, "*.png").OrderBy(f => new FileInfo(f).LastWriteTimeUtc).LastOrDefault();
                    if (png is not null && Settled(png)) break;
                    if (process.HasExited && png is null) break;
                    await Task.Delay(200, cancel).ConfigureAwait(false);
                }

                if (png is null)
                {
                    // An engine that stopped: wait for the last of what it said before quoting it.
                    if (process.HasExited) process.WaitForExit();
                    string said;
                    lock (output) said = output.ToString();
                    var lines = (File.Exists(log) ? File.ReadAllLines(log) : said.Split('\n')).Where(l => l.Length > 0).TakeLast(3);
                    throw new EngineViewException(process.HasExited ? $"{engine.Name} stopped before it drew the view. {string.Join(" ", lines)}" : $"{engine.Name} drew nothing in {timeout.TotalSeconds:0} seconds. {string.Join(" ", lines)}");
                }

                var result = Path.Combine(work, "view.png");
                File.Copy(png, result, overwrite: true);
                return result;
            }
            finally
            {
                Stop(process);
            }
        }
        finally
        {
            if (screen is not null) Stop(screen);
        }
    }

    /// <summary>A screenshot the engine has finished writing: the same size twice, a moment apart.</summary>
    private static bool Settled(string png)
    {
        var a = new FileInfo(png).Length;
        Thread.Sleep(150);
        return a > 0 && new FileInfo(png).Length == a;
    }

    /// <summary>Starts Xvfb on a display number nobody holds, the screen the size of the picture wanted.</summary>
    private static (Process Screen, string Display) StartScreen(string program, int width, int height)
    {
        for (var n = 90; n < 200; n++)
        {
            // A lock whose process is gone (a screen killed outright) is stale: ours to take away, anyone else's to leave.
            var lockFile = $"/tmp/.X{n}-lock";
            if (File.Exists(lockFile))
            {
                int pid;
                try
                {
                    if (!int.TryParse(File.ReadAllText(lockFile).Trim(), out pid) || Directory.Exists($"/proc/{pid}")) continue;
                    File.Delete(lockFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            var socket = $"/tmp/.X11-unix/X{n}";
            if (File.Exists(socket))
            {
                // A socket without its lock is left over from a screen that was killed: ours to take away, anyone else's to leave.
                try
                {
                    File.Delete(socket);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { $":{n}", "-screen", "0", $"{width}x{height}x24", "-nolisten", "tcp", "-nocursor" }) start.ArgumentList.Add(a);
            var screen = Process.Start(start) ?? throw new EngineViewException("The private screen (Xvfb) did not start.");
            screen.BeginOutputReadLine();
            screen.BeginErrorReadLine();
            return (screen, $":{n}");
        }

        throw new EngineViewException("No display number was free for the private screen (Xvfb).");
    }

    private static async Task WaitForScreenAsync(string display, Process screen, CancellationToken cancel)
    {
        var socket = $"/tmp/.X11-unix/X{display.TrimStart(':')}";
        for (var i = 0; i < 50; i++)
        {
            if (File.Exists(socket)) return;
            if (screen.HasExited) throw new EngineViewException($"The private screen (Xvfb) stopped at once (exit code {screen.ExitCode}).");
            await Task.Delay(100, cancel).ConfigureAwait(false);
        }

        throw new EngineViewException("The private screen (Xvfb) did not come up in five seconds.");
    }

    /// <summary>Stops a process: asked first (the screen then takes its socket and lock files with it), then made to.</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (process.HasExited) return;
            if (!OperatingSystem.IsWindows())
            {
                Terminate(process.Id, 15);
                if (process.WaitForExit(2000)) return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill")]
    private static extern int Terminate(int pid, int signal);
}
