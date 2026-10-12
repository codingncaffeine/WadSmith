using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;

namespace WadSmith.App;

public partial class App : Application
{
    internal static AppServices Services { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(Services) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
