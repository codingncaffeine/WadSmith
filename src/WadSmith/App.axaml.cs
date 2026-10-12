using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;

namespace WadSmith.App;

public partial class App : Application
{
    internal static AppServices Services { get; set; } = null!;

    // Kept alive for the life of the process; a collected registration stops handling the signal.
    private static PosixSignalRegistration? _terminate;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(Services) };

            // SIGTERM (a logout, a kill) closes the window the normal way, so the open map's last changes are kept.
            if (!OperatingSystem.IsWindows())
            {
                _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(() => desktop.MainWindow?.Close());
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
