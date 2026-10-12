using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WadSmith.App.ViewModels;

namespace WadSmith.App.Views;

public partial class MainWindow : Window
{
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnFirstOpened;
        WindowFrame.Apply(this);
        WindowFrame.Drags(this, TitleBar);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
            {
                main.PickFolder = PickFolderAsync;
                main.PickFile = PickFileAsync;
                main.LargeInterfaceChanged += ApplyScale;
            }

            // The desktop's own title bar for those who prefer it.
            if (DataContext is MainViewModel { Services.Settings.Current.UseSystemTitleBar: true })
            {
                WindowDecorations = WindowDecorations.Full;
                ExtendClientAreaToDecorationsHint = false;
                TitleBar.IsVisible = false;
                Frame.BorderThickness = new Avalonia.Thickness(0);
            }
        };
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        if (StorageProvider is not { CanPickFolder: true } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFileAsync(string title)
    {
        if (StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = GameFileTypes.Filter }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A maximised window has no edge to resize from and no border to show.
        if (change.Property == WindowStateProperty && WindowDecorations == WindowDecorations.None)
        {
            Frame.BorderThickness = new Avalonia.Thickness(WindowState == WindowState.Normal ? 1 : 0);
        }
    }

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        // Opened fires on every Show; the start-up work runs once.
        Opened -= OnFirstOpened;
        WindowingBackend.WindowOpened(this);
        if (DataContext is MainViewModel vm)
        {
            var s = vm.Services.Settings.Current;
            FitToScreen(s.WindowWidth, s.WindowHeight);
            if (s.WindowMaximized) WindowState = WindowState.Maximized;
            ApplyScale(s.LargeInterface);
        }
    }

    /// <summary>
    /// The remembered size, but never larger than the screen's working area: on Wayland nothing else
    /// shrinks a window, and on a small screen (a handheld, a laptop) the caption buttons would end up
    /// off the screen's edge.
    /// </summary>
    private void FitToScreen(double width, double height)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            // Avalonia's Wayland backend learns about screens after the window opens; fit again then.
            Editor.App.Log.Info("Window: no screen reported yet; fitting when one is.");
            Width = width;
            Height = height;
            Screens.Changed += RefitOnce;
            return;
        }

        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, area.Height));
        Editor.App.Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Window: screen working area {area.Width:0}x{area.Height:0} at scale {screen.Scaling:0.##}; window {Width:0}x{Height:0}"));
    }

    private void RefitOnce(object? sender, EventArgs e)
    {
        Screens.Changed -= RefitOnce;
        if (WindowState == WindowState.Normal) FitToScreen(Width, Height);
    }

    /// <summary>Large interface: everything one quarter bigger.</summary>
    internal void ApplyScale(bool large) => Scaler.LayoutTransform = large ? new ScaleTransform(1.25, 1.25) : null;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || DataContext is not MainViewModel vm) return;
        var s = vm.Services.Settings.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }

        vm.Services.Settings.Save();
        if (vm.Page is not EditorViewModel) return;

        // The open map's last changes reach the disk before the window goes.
        e.Cancel = true;
        _closing = true;
        _ = CloseAfterFlushAsync(vm);
    }

    private async Task CloseAfterFlushAsync(MainViewModel vm)
    {
        await vm.CloseEditorAsync().ConfigureAwait(true);
        Close();
    }
}
