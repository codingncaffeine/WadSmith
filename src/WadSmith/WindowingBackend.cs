using Avalonia;
using Avalonia.Controls;
using WadSmith.Editor.App;

namespace WadSmith.App;

/// <summary>
/// Which windowing backend WadSmith runs on. On a Wayland session the native Wayland backend is the
/// default, because it renders at the monitor's fractional scale. <c>WADSMITH_WAYLAND=0</c> pins
/// X11 (through XWayland) and <c>=1</c> pins Wayland. A Wayland start that dies before a window opens
/// leaves a note, and the next start takes X11; the pattern comes from Mailbox and Tuxflix.
/// </summary>
internal static class WindowingBackend
{
    public const string Variable = "WADSMITH_WAYLAND";

    private static bool _attempting;

    private static bool? Requested => Environment.GetEnvironmentVariable(Variable)?.Trim() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };

    private static bool InWaylandSession => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string Breadcrumb => Path.Combine(App.Services?.Paths.State ?? Path.GetTempPath(), "wayland-attempt");

    public static AppBuilder Apply(AppBuilder builder)
    {
        builder = builder.With(new X11PlatformOptions { OverlayPopups = true });
        switch (Requested)
        {
            case true:
                return WithDrawnDecorations(builder).UseWayland();
            case false:
                return builder;
        }

        if (!InWaylandSession) return builder;
        if (File.Exists(Breadcrumb))
        {
            Log.Warn($"Windowing: the Wayland backend did not open a window last time; using X11. Delete {Breadcrumb} or set {Variable}=1 to retry.");
            return builder;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Breadcrumb)!);
            File.WriteAllText(Breadcrumb, DateTimeOffset.Now.ToString("O") + "\n");
            _attempting = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Windowing: the Wayland attempt could not be noted ({ex.Message}).");
        }

        return WithDrawnDecorations(builder).UseWaylandWithFallback();
    }

    /// <summary>Called once a window is on screen, the only proof the backend works.</summary>
    public static void WindowOpened(TopLevel window)
    {
        var impl = window.PlatformImpl?.GetType().Namespace ?? "";
        Log.Info($"Windowing: {(impl.StartsWith("Avalonia.Wayland", StringComparison.Ordinal) ? "native Wayland" : impl.StartsWith("Avalonia.X11", StringComparison.Ordinal) ? InWaylandSession ? "X11 through XWayland" : "X11" : impl)}");
        if (!_attempting) return;
        _attempting = false;
        var breadcrumb = Breadcrumb;
        _ = Task.Run(() =>
        {
            try
            {
                File.Delete(breadcrumb);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Windowing: the Wayland attempt note could not be cleared ({ex.Message}).");
            }
        });
    }

    private static AppBuilder WithDrawnDecorations(AppBuilder builder)
    {
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
        return builder.With(new WaylandPlatformOptions { ForceDrawnDecorations = true });
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
    }
}
