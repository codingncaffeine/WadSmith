using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WadSmith.App.ViewModels;

namespace WadSmith.App.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
        HeightsRamp.Background = WadSmith.App.Canvas.HeightRamp.Brush();
        View3D.CameraMoved += camera => Canvas.ShowCamera(camera);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not EditorViewModel vm) return;
            Apply3D(vm.Show3D);
            vm.Problems.ShowRequested += elements => Canvas.FitTo(elements);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditorViewModel.Show3D)) Apply3D(vm.Show3D);
            };
        };
        Canvas.HoverChanged += (element, at) => (DataContext as EditorViewModel)?.Hover(element, at);
        Canvas.Drawing += (size, step) => (DataContext as EditorViewModel)?.Drawing(size, step);
        Canvas.Moving += move => (DataContext as EditorViewModel)?.Moving(move);
        Canvas.DropHint += hint => (DataContext as EditorViewModel)?.Hint(hint);
    }

    /// <summary>Shows or hides the 3D view beside the plan; hidden, it does no work.</summary>
    private void Apply3D(bool show)
    {
        MapViews.ColumnDefinitions[2].Width = show ? new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0);
        Split3D.IsVisible = show;
        Pane3D.IsVisible = show;
        View3D.IsVisible = show;
    }

    /// <summary>A shelf picture pressed: it is dragged onto the map (the canvas takes the drop).</summary>
    private async void OnShelfTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShelfTile tile || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ShelfDrop.Format, tile.Drop));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy).ConfigureAwait(true);
    }

    private void OnTextRowLostFocus(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as TextRow)?.Commit();

    private void OnTextRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((sender as Control)?.DataContext as TextRow)?.Commit();
        e.Handled = true;
    }
}
