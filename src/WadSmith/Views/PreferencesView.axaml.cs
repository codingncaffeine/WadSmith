using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WadSmith.App.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView()
    {
        InitializeComponent();

        // Focus inside the sheet, so Escape closes it and Tab moves through it.
        AttachedToVisualTree += (_, _) => Done.Focus();
    }

    /// <summary>About's "Project page": WadSmith's page on GitHub, in the user's browser.</summary>
    private async void OnProjectPage(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri("https://github.com/codingncaffeine/WadSmith")).ConfigureAwait(true);
    }
}
