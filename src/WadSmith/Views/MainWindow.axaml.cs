using Avalonia.Controls;
using Avalonia.Media;
using WadSmith.App.ViewModels;

namespace WadSmith.App.Views;

public partial class MainWindow : Window
{
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnFirstOpened;
    }

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        // Opened fires on every Show; the start-up work runs once.
        Opened -= OnFirstOpened;
        WindowingBackend.WindowOpened(this);
        if (DataContext is MainViewModel vm)
        {
            var s = vm.Services.Settings.Current;
            if (s.WindowMaximized) WindowState = WindowState.Maximized;
            ApplyScale(s.LargeInterface);
        }
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
